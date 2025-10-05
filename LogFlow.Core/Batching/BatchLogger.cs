using System.Buffers;
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

/*
 * Developer ::> Gehan Fernando
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

public sealed class BatchLogger : ILogger, IDisposable, IAsyncDisposable
{
    private readonly ILogger _sink;
    private readonly Channel<LogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly object _sync = new();
    private readonly BatchLoggerOptions _options;
    private LogEntry[] _bufferArray;
    private int _bufferCount;
    private int _approxQueueLength;

    private readonly List<LogEntry> _buffer = [];
    private readonly Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> _composedFlushAsync;
    private readonly ArrayPool<LogEntry> _entryPool = ArrayPool<LogEntry>.Shared;

    public BatchLoggerMetrics Metrics { get; } = new();
    public Func<Exception, string, bool, string> ExceptionFormatter { get; set; } = ExLogger.ExceptionFormatter;

    public BatchLogger(ILogger sink, BatchLoggerOptions options = null, bool disableBackgroundWorker = false)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? new BatchLoggerOptions();
        _buffer = new List<LogEntry>(_options.BatchSize);

        _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(_options.Capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = _options.ChannelFullMode
        });

        _ = _cts.Token.Register(() => _channel.Writer.TryComplete());

        _composedFlushAsync = BuildComposedFlushIfNeeded(_options, _sink);

        if (!disableBackgroundWorker)
        {
            _worker = Task.Run(() => ProcessAsync(_cts.Token), _cts.Token);
        }

        _bufferArray = _entryPool.Rent(_options.BatchSize);
        _bufferCount = 0;
    }

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

        var args = Array.Empty<object>();

        if (state is IEnumerable<KeyValuePair<string, object>> kvPairs)
        {
            List<object> list = null;
            foreach (var kv in kvPairs)
            {
                list ??= new(8);
                list.Add(kv.Value ?? "N/A");
            }

            args = list?.ToArray() ?? Array.Empty<object>();
        }

        if (_options.Filter != null && !_options.Filter(logLevel, msg, exception, args))
        {
            return;
        }

        Enqueue(new LogEntry(logLevel, msg, exception, args));
    }

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

        if (_options.Filter != null && !_options.Filter(LogLevel.Critical, msg, ex, Array.Empty<object>()))
        {
            return;
        }

        LogInternal(LogLevel.Critical, msg, ex);
    }

    private void LogInternal(LogLevel lvl, string msg, Exception ex, params object[] args)
    {
        if (!IsEnabled(lvl))
        {
            return;
        }

        if (_options.Filter != null && !_options.Filter(lvl, msg ?? "N/A", ex, args))
        {
            return;
        }

        var entry = new LogEntry(lvl, msg ?? "N/A", ex, args ?? Array.Empty<object>());
        Enqueue(entry);
    }

    private void Enqueue(LogEntry entry)
    {
        if (_channel.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _approxQueueLength);
        }
        else
        {
            Metrics.IncrementDropped();
        }
    }

    private async Task ProcessAsync(CancellationToken token)
    {
        var reader = _channel.Reader;
        var flushInterval = _options.FlushInterval;
        var batchSize = _options.BatchSize;
        var nextFlush = DateTime.UtcNow + flushInterval;

        try
        {
            while (!token.IsCancellationRequested)
            {
                if (await reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var entry))
                    {
                        _buffer.Add(entry);
                        Interlocked.Decrement(ref _approxQueueLength);

                        if (_buffer.Count >= batchSize)
                        {
                            await FlushAsync(token).ConfigureAwait(false);
                            nextFlush = DateTime.UtcNow + flushInterval;
                        }
                    }
                }

                if (_buffer.Count > 0 && DateTime.UtcNow >= nextFlush)
                {
                    await FlushAsync(token).ConfigureAwait(false);
                    nextFlush = DateTime.UtcNow + flushInterval;
                }

                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            try
            {
                ExLogger.Log(_sink, LogLevel.Error, "BatchLogger background failure", ex);
            }
            catch
            {
                // ignore sink failure
            }

            try
            {
                _options.OnInternalError?.Invoke("BatchLogger background failure", ex);
            }
            catch
            {
                // ignore secondary
            }
        }

        if (_buffer.Count > 0)
        {
            await FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

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
                    {
                        _options.OnInternalError?.Invoke("BatchLogger flush error", ex);
                    }
                    catch
                    {
                        // ignore secondary error
                    }
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

    public async Task FlushAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();

        while (_channel.Reader.TryRead(out var entry))
        {
            if (_bufferCount >= _bufferArray.Length)
            {
                // Expand pooled array when necessary
                var newArray = _entryPool.Rent(_bufferArray.Length * 2);
                Array.Copy(_bufferArray, newArray, _bufferArray.Length);
                _entryPool.Return(_bufferArray);
                _bufferArray = newArray;
            }
            _bufferArray[_bufferCount++] = entry;
        }

        if (_bufferCount == 0)
        {
            return;
        }

        var count = _bufferCount;
        _bufferCount = 0;

        var snapshot = new BatchLogEntry[count];
        for (var i = 0; i < count; i++)
        {
            var e = _bufferArray[i];
            snapshot[i] = new BatchLogEntry(e.Level, e.Message, e.Exception, e.Args);
            _bufferArray[i] = default; // clear slot for reuse
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
                for (var i = 0; i < count; i++)
                {
                    var e = snapshot[i];
                    ExLogger.Log(_sink, e.Level, e.Message, e.Exception, e.Args);
                }
            }

            Metrics.AddFlushed(snapshot.Length);
            Metrics.SetLastFlush(DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _options.OnInternalError?.Invoke("BatchLogger flush error", ex);
        }
    }

    private async Task FlushViaCustomSinkAsync(CancellationToken token)
    {
        List<BatchLogEntry> snapshot;

        lock (_sync)
        {
            if (_buffer.Count == 0)
            {
                return;
            }

            snapshot = _buffer
                .ConvertAll(e => new BatchLogEntry(e.Level, e.Message, e.Exception, e.Args));

            _buffer.Clear();
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
                var fallbackEntries = snapshot
                    .ConvertAll(e => new LogEntry(e.Level, e.Message, e.Exception,
                        e.Args is { Count: > 0 } ? [.. e.Args] : Array.Empty<object>()))
;

                lock (_sync)
                {
                    Flush(fallbackEntries);
                }

                return;
            }

            if (_options.ForwardToILoggerSink)
            {
                foreach (var e in snapshot)
                {
                    ExLogger.Log(
                        _sink,
                        e.Level,
                        e.Message,
                        e.Exception,
                        e.Args is { Count: > 0 } ? [.. e.Args] : Array.Empty<object>());
                }
            }

            Metrics.AddFlushed(snapshot.Count);
            Metrics.SetLastFlush(DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                _options.OnInternalError?.Invoke("BatchLogger OnFlushAsync handler error", ex);
            }
            catch
            {
                // ignore secondary errors
            }

            lock (_sync)
            {
                try
                {
                    var fallbackEntries = snapshot
                        .ConvertAll(e => new LogEntry(e.Level, e.Message, e.Exception,
                            e.Args is { Count: > 0 } ? [.. e.Args] : Array.Empty<object>()));

                    Flush(fallbackEntries);
                }
                catch (Exception innerEx)
                {
                    _options.OnInternalError?.Invoke("Fallback flush failed", innerEx);
                }
            }
        }
    }

#if DEBUG
    internal Task ForceFlushAsync(CancellationToken token = default) => FlushAsync(token);
#endif

    public void Dispose()
    {
        try
        {
            if (!_cts.IsCancellationRequested)
            {
                _cts.Cancel();
            }

            _channel.Writer.TryComplete();

            if (_worker?.IsCompleted == false)
            {
                try
                { _worker.GetAwaiter().GetResult(); }
                catch (OperationCanceledException)
                {
                    // Expected when cancellation is cooperative.
                }
            }

            while (_channel.Reader.TryRead(out var e))
            {
                _buffer.Add(e);
            }

            if (_buffer.Count > 0)
            {
                try
                {
                    if (_options.OnFlushAsync is not null || _composedFlushAsync is not null)
                    {
                        FlushViaCustomSinkAsync(CancellationToken.None).GetAwaiter().GetResult();
                    }
                    else
                    {
                        Flush(_buffer);
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        _options.OnInternalError?.Invoke("BatchLogger Dispose flush error", ex);
                    }
                    catch { /* ignore secondary error */ }

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

            await _cts.CancelAsync().ConfigureAwait(false);

            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is cooperative.
            }

            await FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private readonly record struct LogEntry(LogLevel Level, string Message, Exception Exception, object[] Args);

    private static Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> BuildComposedFlushIfNeeded(BatchLoggerOptions options, ILogger sink)
    {
        if (options.OnFlushAsync is not null)
        {
            return null;
        }

        var activeSinks = new List<IBatchSink>(capacity: 4);

        if (options.File?.Enabled == true)
        {
            activeSinks.Add(new FileBatchSink(options.File, options.Format));
        }

        if (options.Database?.Enabled == true)
        {
            activeSinks.Add(new DatabaseBatchSink(options.Database, options.Format));
        }

        if (activeSinks.Count == 0)
        {
            return null;
        }

        if (options.ForwardToILoggerSink)
        {
            activeSinks.Add(new ForwardingBatchSink(sink));
        }

        return async (entries, token) =>
        {
            List<Exception> errors = [];

            foreach (var sink in activeSinks)
            {
                try
                {
                    await sink.WriteAsync(entries, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                    try
                    {
                        options.OnInternalError?.Invoke($"{sink.GetType().Name} error", ex);
                    }
                    catch
                    {
                        // Never propagate secondary failures from diagnostics
                    }
                }
            }
        };
    }

    private interface IBatchSink
    {
        Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token);
    }

    private sealed class ForwardingBatchSink : IBatchSink
    {
        private readonly ILogger _sink;

        public ForwardingBatchSink(ILogger sink) => _sink = sink;

        public Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token)
        {
            foreach (var e in entries)
            {
                ExLogger.Log(_sink, e.Level, e.Message, e.Exception,
                    e.Args is { Count: > 0 } ? [.. e.Args] : Array.Empty<object>());
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FileBatchSink : IBatchSink
    {
        private readonly BatchFileOptions _opts;
        private readonly BatchFormatOptions _fmt;

        private static readonly JsonSerializerOptions _defaultJson = new() { WriteIndented = false };

        public FileBatchSink(BatchFileOptions opts, BatchFormatOptions fmt)
        {
            _opts = opts;
            _fmt = fmt ?? new BatchFormatOptions();
            _ = Directory.CreateDirectory(Path.GetDirectoryName(GetCurrentPath())!);
        }

        public async Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token)
        {
            var path = GetCurrentPath();
            var batchTimestamp = DateTime.UtcNow;

            if (File.Exists(path) && _opts.RollingSizeBytes > 0)
            {
                var len = new FileInfo(path).Length;
                if (len >= _opts.RollingSizeBytes)
                {
                    RollFiles();
                }
            }

            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, useAsync: true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (_opts.Format == BatchFileFormat.Json)
            {
                var batchPayload = entries.Select(e => new
                {
                    tsUtc = _fmt.UsePerEntryTimestamp ? DateTime.UtcNow : batchTimestamp,
                    level = e.Level.ToString(),
                    message = e.Message,
                    exception = e.Exception?.ToString(),
                    args = e.Args
                });

                var json = JsonSerializer.Serialize(
                    batchPayload,
                    _fmt.JsonSerializerOptions ?? _defaultJson
                );

                await writer.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
            }
            else
            {
                foreach (var e in entries)
                {
                    token.ThrowIfCancellationRequested();

                    var dateTime = _fmt.UsePerEntryTimestamp ? DateTime.UtcNow : batchTimestamp;
                    var line = Format(e, _fmt, _opts.Format, dateTime);
                    await writer.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
                }
            }

            await writer.FlushAsync(token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
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
                RollingInterval.Week => $"{stamp.AddDays(-(int)stamp.DayOfWeek + (int)DayOfWeek.Monday).ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-W{ISOWeek.GetWeekOfYear(stamp):D2}",
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
            try
            {
                var path = GetCurrentPath();
                if (!File.Exists(path))
                {
                    return;
                }

                _ = Task.Run(() =>
                {
                    try
                    {
                        for (var i = _opts.RetainedFileCountLimit - 1; i >= 1; i--)
                        {
                            var older = $"{path}.{i}";
                            var newer = $"{path}.{i + 1}";

                            if (File.Exists(older))
                            {
                                TryMove(older, newer);
                            }
                        }

                        TryMove(path, $"{path}.1");
                    }
                    catch
                    {
                        // Ignore secondary failures to avoid blocking writes
                    }
                });
            }
            catch
            {
                // Ignore to keep logging resilient
            }

            static void TryMove(string src, string dst)
            {
                try
                {
                    if (File.Exists(src))
                    {
                        File.Move(src, dst, overwrite: true);
                    }
                }
                catch (IOException)
                {
                    // File might still be in use; skip silently
                }
                catch
                {
                    // Ignore unexpected errors
                }
            }
        }

        private static string Format(BatchLogEntry e, BatchFormatOptions fmt, BatchFileFormat fileFormat, DateTime now)
        {
            if (fileFormat == BatchFileFormat.Json)
            {
                var payload = new
                {
                    tsUtc = now,
                    level = e.Level.ToString(),
                    message = e.Message,
                    exception = e.Exception?.ToString(),
                    args = e.Args,
                };

                return JsonSerializer.Serialize(payload, fmt.JsonSerializerOptions ?? new JsonSerializerOptions { WriteIndented = false });
            }

            var sb = new StringBuilder(256);
            _ = sb.Append(now.ToString("O"))
                  .Append(" [").Append(e.Level).Append("] ")
                  .Append(e.Message ?? "N/A");

            if (e.Args is { Count: > 0 })
            {
                _ = sb.Append(" | args=").Append(JsonSerializer.Serialize(e.Args, fmt.JsonSerializerOptions ?? _defaultJson));
            }

            if (e.Exception is not null)
            {
                _ = sb.Append(" | ex=").Append(e.Exception);
            }

            return sb.ToString();
        }
    }

    private sealed class DatabaseBatchSink : IBatchSink
    {
        private readonly BatchDatabaseOptions _opts;
        private readonly BatchFormatOptions _fmt;
        private static readonly JsonSerializerOptions _defaultJson = new() { WriteIndented = false };

        public DatabaseBatchSink(BatchDatabaseOptions opts, BatchFormatOptions fmt)
        {
            _opts = opts ?? throw new ArgumentNullException(nameof(opts));
            _fmt = fmt ?? new BatchFormatOptions();

            if (string.IsNullOrWhiteSpace(_opts.ProviderInvariantName))
            {
                throw new ArgumentException("Database.ProviderInvariantName is required.", nameof(opts));
            }

            if (string.IsNullOrWhiteSpace(_opts.ConnectionString))
            {
                throw new ArgumentException("Database.ConnectionString is required.", nameof(opts));
            }
        }

        public async Task WriteAsync(IReadOnlyList<BatchLogEntry> entries, CancellationToken token)
        {
            if (entries.Count == 0)
            {
                return;
            }

            var factory = DbProviderFactories.GetFactory(_opts.ProviderInvariantName);
            await using var conn = factory.CreateConnection()!;
            conn.ConnectionString = _opts.ConnectionString;
            await conn.OpenAsync(token).ConfigureAwait(false);

            if (_opts.AutoCreateTable)
            {
                try
                {
                    await using var tableCommand = conn.CreateCommand();
                    tableCommand.CommandText = _opts.GetCreateTableSql();
                    _ = await tableCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                catch
                {
                    // Ignore "table already exists" race
                }
            }

            // ---------- Optimized multi-row INSERT ----------
            var jsonOpts = _fmt.JsonSerializerOptions ?? _defaultJson;
            var table = _opts.Table;

            var sb = new StringBuilder(entries.Count * 256);
            sb.Append("INSERT INTO ")
              .Append(table)
              .Append(" (")
              .Append(_opts.ColTimestamp).Append(", ")
              .Append(_opts.ColLevel).Append(", ")
              .Append(_opts.ColMessage).Append(", ")
              .Append(_opts.ColException).Append(", ")
              .Append(_opts.ColArgsJson)
              .Append(") VALUES ");

            for (var i = 0; i < entries.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                sb.AppendFormat(CultureInfo.InvariantCulture,
                    "(@pTs{0}, @pLvl{0}, @pMsg{0}, @pExc{0}, @pArgs{0})", i);
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sb.ToString();

            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var pTs = cmd.CreateParameter();
                pTs.ParameterName = $"@pTs{i}";
                pTs.Value = DateTime.UtcNow;

                var pLvl = cmd.CreateParameter();
                pLvl.ParameterName = $"@pLvl{i}";
                pLvl.Value = e.Level.ToString();

                var pMsg = cmd.CreateParameter();
                pMsg.ParameterName = $"@pMsg{i}";
                pMsg.Value = e.Message ?? string.Empty;

                var pExc = cmd.CreateParameter();
                pExc.ParameterName = $"@pExc{i}";
                pExc.Value = e.Exception?.ToString() ?? (object)DBNull.Value;

                var pArgs = cmd.CreateParameter();
                pArgs.ParameterName = $"@pArgs{i}";
                pArgs.Value = (e.Args is { Count: > 0 })
                    ? JsonSerializer.Serialize(e.Args, jsonOpts)
                    : DBNull.Value;

                _ = cmd.Parameters.Add(pTs);
                _ = cmd.Parameters.Add(pLvl);
                _ = cmd.Parameters.Add(pMsg);
                _ = cmd.Parameters.Add(pExc);
                _ = cmd.Parameters.Add(pArgs);
            }

            _ = await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }
}