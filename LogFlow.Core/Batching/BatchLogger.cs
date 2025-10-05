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
    private readonly Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> _composedFlushAsync;
    private List<LogEntry> _buffer = [];
    private int _approxQueueLength;

    public BatchLoggerMetrics Metrics { get; } = new();
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
            FullMode = _options.ChannelFullMode
        });

        _ = _cts.Token.Register(() => _channel.Writer.TryComplete());

        _composedFlushAsync = BuildComposedFlushIfNeeded(_options, _sink);

        _worker = Task.Run(() => ProcessAsync(_cts.Token), _cts.Token);
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
            var list = new List<object>(8);
            foreach (var kv in kvPairs)
            {
                list.Add(kv.Value ?? "N/A");
            }

            if (list.Count > 0)
            {
                args = [.. list];
            }
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
        if (_options.ChannelFullMode == BoundedChannelFullMode.DropOldest &&
            _approxQueueLength >= _options.Capacity)
        {
            Metrics.IncrementDropped();
        }

        if (_channel.Writer.TryWrite(entry))
        {
            var len = Interlocked.Increment(ref _approxQueueLength);
            if (len > _options.Capacity)
            {
                _ = Interlocked.Exchange(ref _approxQueueLength, _options.Capacity);
            }
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

        try
        {
            while (!token.IsCancellationRequested)
            {
                var delayTask = Task.Delay(flushInterval, token);
                var readTask = reader.WaitToReadAsync(token).AsTask();
                var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);

                while (reader.TryRead(out var entry))
                {
                    _ = Interlocked.Decrement(ref _approxQueueLength);
                    _buffer.Add(entry);

                    if (_buffer.Count >= batchSize)
                    {
                        await FlushAsync(token).ConfigureAwait(false);
                    }
                }

                if (_buffer.Count > 0 && completed == delayTask)
                {
                    await FlushAsync(token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown; no action needed.
        }
        catch (Exception ex)
        {
            try
            {
                ExLogger.Log(_sink, LogLevel.Error, "BatchLogger background failure", ex);
            }
            catch
            {
                // Swallow if the sink itself is failing.
            }

            try
            {
                _options.OnInternalError?.Invoke("BatchLogger background failure", ex);
            }
            catch
            {
                // Never allow diagnostics to crash the worker.
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
            _buffer.Add(entry);
        }

        var toFlush = Interlocked.Exchange(ref _buffer, new List<LogEntry>(_options.BatchSize));

        if (toFlush.Count == 0)
        {
            return;
        }

        var snapshot = toFlush.ConvertAll(e => new BatchLogEntry(e.Level, e.Message, e.Exception, e.Args));

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
                foreach (var e in toFlush)
                {
                    ExLogger.Log(_sink, e.Level, e.Message, e.Exception, e.Args);
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
            _ = _channel.Writer.TryComplete();
            _cts.Cancel();

            try
            {
                _worker.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is cooperative.
            }

            while (_channel.Reader.TryRead(out var e))
            {
                _buffer.Add(e);
            }

            if (_buffer.Count > 0)
            {
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

            foreach (var e in entries)
            {
                token.ThrowIfCancellationRequested();

                var dateTime = _fmt.UsePerEntryTimestamp ? DateTime.UtcNow : batchTimestamp;

                var line = Format(e, _fmt, _opts.Format, dateTime);
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
                catch
                {
                    // Ignore rolling failures to avoid blocking logging.
                }
            }

            for (var i = _opts.RetainedFileCountLimit - 1; i >= 1; i--)
            {
                var older = $"{path}.{i}";
                var newer = $"{path}.{i + 1}";
                SafeMove(older, newer);
            }

            SafeMove(path, $"{path}.1");
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
                    // Ignore table creation race conditions (e.g., table already exists)
                }
            }

            await using var dataCommand = conn.CreateCommand();
            dataCommand.CommandText = _opts.GetInsertSql();

            static string P(string name) => name.Length > 0 && name[0] == '@' ? name : $"@{name}";

            var pTs = dataCommand.CreateParameter();
            pTs.ParameterName = P(_opts.ColTimestamp);
            _ = dataCommand.Parameters.Add(pTs);

            var pLvl = dataCommand.CreateParameter();
            pLvl.ParameterName = P(_opts.ColLevel);
            _ = dataCommand.Parameters.Add(pLvl);

            var pMsg = dataCommand.CreateParameter();
            pMsg.ParameterName = P(_opts.ColMessage);
            _ = dataCommand.Parameters.Add(pMsg);

            var pExc = dataCommand.CreateParameter();
            pExc.ParameterName = P(_opts.ColException);
            _ = dataCommand.Parameters.Add(pExc);

            var pArgs = dataCommand.CreateParameter();
            pArgs.ParameterName = P(_opts.ColArgsJson);
            _ = dataCommand.Parameters.Add(pArgs);

            var jsonOpts = _fmt.JsonSerializerOptions ?? _defaultJson;

            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();

                pTs.Value = DateTime.UtcNow;
                pLvl.Value = entry.Level.ToString();
                pMsg.Value = entry.Message ?? string.Empty;
                pExc.Value = entry.Exception?.ToString() ?? (object)DBNull.Value;
                pArgs.Value = (entry.Args is { Count: > 0 })
                    ? JsonSerializer.Serialize(entry.Args, jsonOpts)
                    : DBNull.Value;

                _ = await dataCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
    }
}