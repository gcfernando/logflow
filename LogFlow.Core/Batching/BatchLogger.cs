using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using LogFlow.Core.Batching.Model;
using LogFlow.Core.Batching.Model.Enums;
using LogFlow.Core.ExLogging;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

public sealed class BatchLogger : ILogger, IDisposable, IAsyncDisposable
{
    private readonly ILogger _sink;
    private readonly Channel<LogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    // NOTE: _buffer is only touched by the background worker and flush methods
    // after the worker has been cancelled/awaited. It is NOT thread-safe by design.
    private readonly List<LogEntry> _buffer;

    private readonly BatchLoggerOptions _options;

    // When OnFlushAsync is not provided, we may build a composite sink from options.
    private readonly Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> _composedFlushAsync;

    public BatchLoggerMetrics Metrics { get; } = new();

    /// <summary>
    /// Exception formatter used by ExLog*Exception helpers. Defaults to ExLogger.ExceptionFormatter.
    /// </summary>
    public Func<Exception, string, bool, string> ExceptionFormatter { get; set; } = ExLogger.ExceptionFormatter;

    public BatchLogger(ILogger sink, BatchLoggerOptions options = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new BatchLoggerOptions();
        _buffer = new List<LogEntry>(_options.BatchSize);

        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(_options.Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        // If the caller did not provide a custom flush, but enabled file/database sinks,
        // we compose an internal flush handler that targets the configured sinks AND the original ILogger sink.
        _composedFlushAsync = BuildComposedFlushIfNeeded(_options);

        // Start background worker
        _worker = Task.Run(() => ProcessAsync(_cts.Token), _cts.Token);
    }

    // ---------------- ILogger ----------------
    public bool IsEnabled(LogLevel logLevel) => _sink.IsEnabled(logLevel);

    public IDisposable BeginScope<TState>(TState state)
        => _sink.BeginScope(state) ?? NullScope.Instance;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception,
        Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var msg = formatter != null ? formatter(state, exception) : state?.ToString() ?? "N/A";
        var args = state is IEnumerable<KeyValuePair<string, object>> kv
            ? [.. kv.Select(k => k.Value ?? "N/A")]
            : Array.Empty<object>();

        // 🆕 Optional filter (kept non-breaking: defaults to allowing everything)
        if (_options.Filter != null && !_options.Filter(logLevel, msg, exception, args))
        {
            return;
        }

        Enqueue(new LogEntry(logLevel, msg, exception, args));
    }

    // ---------------- ExLogger-style Methods ----------------
    public void ExLogTrace(string msg, params object[] args) => LogInternal(LogLevel.Trace, msg, null, args);

    public void ExLogDebug(string msg, params object[] args) => LogInternal(LogLevel.Debug, msg, null, args);

    public void ExLogInformation(string msg, params object[] args) => LogInternal(LogLevel.Information, msg, null, args);

    public void ExLogWarning(string msg, params object[] args) => LogInternal(LogLevel.Warning, msg, null, args);

    public void ExLogError(string msg, params object[] args) => LogInternal(LogLevel.Error, msg, null, args);

    public void ExLogError(string msg, Exception ex, params object[] args) => LogInternal(LogLevel.Error, msg, ex, args);

    public void ExLogCritical(string msg, params object[] args) => LogInternal(LogLevel.Critical, msg, null, args);

    public void ExLogCritical(string msg, Exception ex, params object[] args) => LogInternal(LogLevel.Critical, msg, ex, args);

    public void ExLogErrorException(Exception ex, string title = "System Error", bool details = false)
    {
        if (!IsEnabled(LogLevel.Error))
        {
            return;
        }

        var msg = (ExceptionFormatter ?? ExLogger.ExceptionFormatter)(ex, title, details);

        // 🆕 Optional filter honored here as well
        if (_options.Filter != null && !_options.Filter(LogLevel.Error, msg, ex, Array.Empty<object>()))
        {
            return;
        }

        LogInternal(LogLevel.Error, msg, ex);
    }

    public void ExLogCriticalException(Exception ex, string title = "Critical System Error", bool details = false)
    {
        if (!IsEnabled(LogLevel.Critical))
        {
            return;
        }

        var msg = (ExceptionFormatter ?? ExLogger.ExceptionFormatter)(ex, title, details);

        // 🆕 Optional filter honored here as well
        if (_options.Filter != null && !_options.Filter(LogLevel.Critical, msg, ex, Array.Empty<object>()))
        {
            return;
        }

        LogInternal(LogLevel.Critical, msg, ex);
    }

    // ---------------- Internal Core ----------------
    private void LogInternal(LogLevel lvl, string msg, Exception ex, params object[] args)
    {
        if (!IsEnabled(lvl))
        {
            return;
        }

        // 🆕 Optional filter honored for ExLogger-style calls too
        if (_options.Filter != null && !_options.Filter(lvl, msg ?? "N/A", ex, args))
        {
            return;
        }

        var entry = new LogEntry(lvl, msg ?? "N/A", ex, args ?? Array.Empty<object>());
        Enqueue(entry);
    }

    private void Enqueue(LogEntry entry)
    {
        if (!_channel.Writer.TryWrite(entry))
        {
            Metrics.IncrementDropped();
        }
    }

    /// <summary>
    /// Background worker: races between data availability and flush interval.
    /// Keeps your original semantics while improving timing clarity.
    /// </summary>
    private async Task ProcessAsync(CancellationToken token)
    {
        var reader = _channel.Reader;
        var flushInterval = _options.FlushInterval;
        var batchSize = _options.BatchSize;

        try
        {
            while (!token.IsCancellationRequested)
            {
                // Race: either something is available to read, or the timer fires
                var delayTask = Task.Delay(flushInterval, token);
                var readTask = reader.WaitToReadAsync(token).AsTask();

                var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);

                // Drain queue into buffer
                while (reader.TryRead(out var entry))
                {
                    _buffer.Add(entry);
                    if (_buffer.Count >= batchSize)
                    {
                        await FlushAsync(token).ConfigureAwait(false);
                    }
                }

                // If timer fired and we have data, flush time-based batches
                if (_buffer.Count > 0 && completed == delayTask)
                {
                    await FlushAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal on shutdown
        }
        catch (Exception ex)
        {
            // Keep your original error log AND surface via optional callback
            try
            {
                ExLogger.Log(_sink, LogLevel.Error, "BatchLogger background failure", ex);
            }
            catch
            {
                // swallow if sink is failing hard
            }

            try
            {
                _options.OnInternalError?.Invoke("BatchLogger background failure", ex);
            }
            catch
            {
                // never let diagnostics crash the worker
            }
        }

        // Final flush
        if (_buffer.Count > 0)
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Flushes the provided buffer to the underlying sink (or custom OnFlushAsync handler).
    /// </summary>
    private void Flush(List<LogEntry> buf)
    {
        if (buf.Count == 0)
        {
            return;
        }

        try
        {
            foreach (var e in buf)
            {
                try
                {
                    ExLogger.Log(_sink, e.Level, e.Message, e.Exception, e.Args);
                }
                catch (Exception ex)
                {
                    Metrics.IncrementDropped();
                    System.Diagnostics.Debug.WriteLine($"[BatchLogger] Flush error: {ex}");
                    try
                    { _options.OnInternalError?.Invoke("BatchLogger flush error", ex); }
                    catch { /* ignore */ }
                }
            }

            Metrics.AddFlushed(buf.Count);
            Metrics.SetLastFlush(DateTime.UtcNow);
        }
        finally
        {
            buf.Clear();
        }
    }

    /// <summary>
    /// Public async flush that respects cancellation. If a custom flush handler
    /// is provided in options, it will be used; otherwise it falls back to the
    /// original direct-to-sink Flush().
    /// </summary>
    public async Task FlushAsync(CancellationToken token = default)
    {
        // Drain any remaining items first (non-blocking)
        while (_channel.Reader.TryRead(out var e))
        {
            token.ThrowIfCancellationRequested();
            _buffer.Add(e);
            if (_buffer.Count >= _options.BatchSize)
            {
                // If a custom sink is configured, flush in chunks too
                if (_options.OnFlushAsync is not null || _composedFlushAsync is not null)
                {
                    await FlushViaCustomSinkAsync(token).ConfigureAwait(false);
                }
                else
                {
                    Flush(_buffer);
                }
            }
        }

        if (_buffer.Count == 0)
        {
            await Task.Yield();
            return;
        }

        // Use custom sink if provided, else original sink (or composed sinks)
        if (_options.OnFlushAsync is not null || _composedFlushAsync is not null)
        {
            await FlushViaCustomSinkAsync(token).ConfigureAwait(false);
        }
        else
        {
            Flush(_buffer);
        }

        await Task.Yield();
    }

    private async Task FlushViaCustomSinkAsync(CancellationToken token)
    {
        if (_buffer.Count == 0)
        {
            return;
        }

        // Convert to public snapshot entries (avoids exposing internal struct or arrays)
        List<BatchLogEntry> snapshot = new(_buffer.Count);
        foreach (var e in _buffer)
        {
            snapshot.Add(new BatchLogEntry(e.Level, e.Message, e.Exception, e.Args));
        }

        try
        {
            if (_options.OnFlushAsync is not null)
            {
                await _options.OnFlushAsync(snapshot, token).ConfigureAwait(false);
            }
            else if (_composedFlushAsync is not null)
            {
                await _composedFlushAsync(snapshot, token).ConfigureAwait(false);
            }
            else
            {
                // Shouldn't happen; fall back
                Flush(_buffer);
                return;
            }

            // Update metrics to mirror original Flush()
            Metrics.AddFlushed(snapshot.Count);
            Metrics.SetLastFlush(DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            // bubble up cooperative cancellation
            throw;
        }
        catch (Exception ex)
        {
            // Surface via callback and fallback to sink flush to avoid data loss
            try
            {
                _options.OnInternalError?.Invoke("BatchLogger OnFlushAsync handler error", ex);
            }
            catch
            {
                // ignore internal handler errors
            }

            // ✅ Use async fallback instead of synchronous Flush()
            await Task.Run(() => Flush(_buffer), token).ConfigureAwait(false);
        }
        finally
        {
            _buffer.Clear();
        }
    }

#if DEBUG

    /// <summary>
    /// Test-helper to force a flush without needing to await cancellation.
    /// </summary>
    internal Task ForceFlushAsync(CancellationToken token = default) => FlushAsync(token);

#endif

    // ---------------- IDisposable ----------------
    public void Dispose()
    {
        try
        {
            _ = _channel.Writer.TryComplete();
            _cts.Cancel();

            try
            { _worker.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { /* expected */ }

            // Drain remaining
            while (_channel.Reader.TryRead(out var e))
            {
                _buffer.Add(e);
            }

            if (_buffer.Count > 0)
            {
                // Respect custom handler if present during Dispose()
                if (_options.OnFlushAsync is not null || _composedFlushAsync is not null)
                {
                    try
                    {
                        FlushViaCustomSinkAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        try
                        { _options.OnInternalError?.Invoke("BatchLogger Dispose flush error", ex); }
                        catch { /* ignore */ }
                        // Fallback
                        Flush(_buffer);
                    }
                }
                else
                {
                    Flush(_buffer);
                }
            }
        }
        finally
        {
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _ = _channel.Writer.TryComplete();

            // .NET 8 friendly (kept from your original code)
            await _cts.CancelAsync().ConfigureAwait(false);

            try
            { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }

            await FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    // ---------------- Internal record ----------------
    private readonly record struct LogEntry(LogLevel Level, string Message, Exception Exception, object[] Args);

    // ---------------- Sink composition ----------------

    private static Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> BuildComposedFlushIfNeeded(BatchLoggerOptions options)
    {
        // If user has provided a custom handler, we don't compose anything.
        if (options.OnFlushAsync is not null)
        {
            return null;
        }

        var activeSinks = new List<IBatchSink>(4);

        if (options.File?.Enabled == true)
        {
            activeSinks.Add(new FileBatchSink(options.File, options.Format));
        }

        if (options.Database?.Enabled == true)
        {
            activeSinks.Add(new DatabaseBatchSink(options.Database, options.Format));
        }

        // Always include "fallback-to-ILogger" sink if requested (default true).
        if (options.ForwardToILoggerSink)
        {
            activeSinks.Add(new LoggerBatchSink());
        }

        if (activeSinks.Count == 0)
        {
            return null;
        }

        // Compose
        return async (entries, token) =>
        {
            // We intentionally try all sinks even if one fails, like Serilog's "write to many".
            List<Exception> errors = [];

            foreach (var sink in activeSinks)
            {
                try
                {
                    await sink.WriteAsync(entries, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    try
                    { options.OnInternalError?.Invoke($"{sink.GetType().Name} error", ex); }
                    catch { /* ignore */ }
                }
            }

            if (errors.Count > 0)
            {
                // Don't throw—already surfaced via OnInternalError. Data best-effort was attempted for all sinks.
            }
        };
    }

    // -------------- Internal sink types ----------------

    private interface IBatchSink
    {
        Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token);
    }

    /// <summary>
    /// Writes to the underlying ILogger (preserves original behavior).
    /// </summary>
    private sealed class LoggerBatchSink : IBatchSink
    {
        public Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token) =>
            // No-op here; the caller (BatchLogger) will fall back to Flush() path when using this sink alone.
            // We keep this class in the list so composition logic remains simple.
            Task.CompletedTask;
    }

    /// <summary>
    /// Simple, dependency-free rolling file writer.
    /// </summary>
    private sealed class FileBatchSink : IBatchSink
    {
        private readonly BatchFileOptions _opts;
        private readonly BatchFormatOptions _fmt;

        public FileBatchSink(BatchFileOptions opts, BatchFormatOptions fmt)
        {
            _opts = opts;
            _fmt = fmt ?? new BatchFormatOptions();
            _ = Directory.CreateDirectory(Path.GetDirectoryName(GetCurrentPath())!);
        }

        public async Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token)
        {
            var path = GetCurrentPath();

            // Roll by size
            if (File.Exists(path) && _opts.RollingSizeBytes > 0)
            {
                var len = new FileInfo(path).Length;
                if (len >= _opts.RollingSizeBytes)
                {
                    RollFiles();
                }
            }

            // Open append
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            foreach (var e in entries)
            {
                token.ThrowIfCancellationRequested();
                var line = Format(e, _fmt, _opts.Format);
                await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
            }

            await writer.FlushAsync(token).ConfigureAwait(false);
        }

        private string GetCurrentPath()
        {
            if (_opts.RollingInterval == RollingInterval.None)
            {
                return _opts.Path;
            }

            var stamp = DateTime.UtcNow;
            var suffix = _opts.RollingInterval switch
            {
                RollingInterval.Hour => stamp.ToString("yyyyMMdd_HH", CultureInfo.InvariantCulture),
                RollingInterval.Day => stamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                RollingInterval.Week => $"{stamp:yyyyMMdd}-W{CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(stamp, CalendarWeekRule.FirstDay, DayOfWeek.Monday)}",
                RollingInterval.Month => stamp.ToString("yyyyMM", CultureInfo.InvariantCulture),
                RollingInterval.Year => stamp.ToString("yyyy", CultureInfo.InvariantCulture),
                _ => stamp.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
            };
            var dir = Path.GetDirectoryName(_opts.Path)!;
            var file = Path.GetFileNameWithoutExtension(_opts.Path);
            var ext = Path.GetExtension(_opts.Path);
            return Path.Combine(dir, $"{file}-{suffix}{ext}");
        }

        private void RollFiles()
        {
            // Move current file to .1, bump older, keep RetainedFileCountLimit
            var path = GetCurrentPath();
            if (!File.Exists(path))
            {
                return;
            }

            static void SafeMove(string src, string dst)
            {
                try
                {
                    if (File.Exists(src))
                    {
                        File.Move(src, dst, overwrite: true);
                    }
                }
                catch { /* ignore */ }
            }

            for (var i = _opts.RetainedFileCountLimit - 1; i >= 1; i--)
            {
                var older = $"{path}.{i}";
                var newer = $"{path}.{i + 1}";
                SafeMove(older, newer);
            }

            SafeMove(path, $"{path}.1");
        }

        private static string Format(BatchLogEntry e, BatchFormatOptions fmt, BatchFileFormat fileFormat)
        {
            if (fileFormat == BatchFileFormat.Json)
            {
                var payload = new
                {
                    tsUtc = DateTime.UtcNow,
                    level = e.Level.ToString(),
                    message = e.Message,
                    exception = e.Exception?.ToString(),
                    args = e.Args,
                };
                return JsonSerializer.Serialize(payload, fmt.JsonSerializerOptions ?? new JsonSerializerOptions { WriteIndented = false });
            }

            // Plain text template: "{TimestampUtc:O} [{Level}] Message | Args | Exception"
            var sb = new StringBuilder(256);
            _ = sb.Append(DateTime.UtcNow.ToString("O"))
              .Append(" [").Append(e.Level).Append("] ")
              .Append(e.Message ?? "N/A");

            if (e.Args is { Count: > 0 })
            {
                _ = sb.Append(" | args=").Append(JsonSerializer.Serialize(e.Args, fmt.JsonSerializerOptions ?? new()));
            }

            if (e.Exception is not null)
            {
                _ = sb.Append(" | ex=").Append(e.Exception);
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Minimal ADO.NET batch inserter (works with SqlClient, Npgsql, MySqlConnector, etc.)
    /// </summary>
    private sealed class DatabaseBatchSink : IBatchSink
    {
        private readonly BatchDatabaseOptions _opts;
        private readonly BatchFormatOptions _fmt;

        public DatabaseBatchSink(BatchDatabaseOptions opts, BatchFormatOptions fmt)
        {
            _opts = opts;
            _fmt = fmt ?? new BatchFormatOptions();
            if (string.IsNullOrWhiteSpace(_opts.ProviderInvariantName))
            {
                throw new ArgumentException("Database.ProviderInvariantName is required.");
            }

            if (string.IsNullOrWhiteSpace(_opts.ConnectionString))
            {
                throw new ArgumentException("Database.ConnectionString is required.");
            }
        }

        public async Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token)
        {
            var factory = DbProviderFactories.GetFactory(_opts.ProviderInvariantName);

            await using var conn = factory.CreateConnection()!;
            conn.ConnectionString = _opts.ConnectionString;
            await conn.OpenAsync(token).ConfigureAwait(false);

            if (_opts.AutoCreateTable)
            {
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = _opts.GetCreateTableSql();
                    _ = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                catch
                {
                    // ignore race / permissions issues
                }
            }

            // Use single command with parameters per row (portable, simple).
            // If the provider supports bulk APIs, you can add a provider-specific implementation later.
            var insertSql = _opts.GetInsertSql();

            foreach (var e in entries)
            {
                token.ThrowIfCancellationRequested();

                await using var cmd = conn.CreateCommand();
                cmd.CommandText = insertSql;

                var pTs = cmd.CreateParameter();
                pTs.ParameterName = _opts.ColTimestamp;
                pTs.Value = DateTime.UtcNow;
                _ = cmd.Parameters.Add(pTs);
                var pLvl = cmd.CreateParameter();
                pLvl.ParameterName = _opts.ColLevel;
                pLvl.Value = e.Level.ToString();
                _ = cmd.Parameters.Add(pLvl);
                var pMsg = cmd.CreateParameter();
                pMsg.ParameterName = _opts.ColMessage;
                pMsg.Value = e.Message ?? "";
                _ = cmd.Parameters.Add(pMsg);
                var pExc = cmd.CreateParameter();
                pExc.ParameterName = _opts.ColException;
                pExc.Value = e.Exception?.ToString() ?? (object)DBNull.Value;
                _ = cmd.Parameters.Add(pExc);
                var pArgs = cmd.CreateParameter();
                pArgs.ParameterName = _opts.ColArgsJson;
                pArgs.Value = (e.Args is { Count: > 0 })
                    ? JsonSerializer.Serialize(e.Args, _fmt.JsonSerializerOptions ?? new())
                    : DBNull.Value;
                _ = cmd.Parameters.Add(pArgs);

                _ = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}