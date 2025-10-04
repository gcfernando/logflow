using System.Threading.Channels;
using LogFlow.Core.ExLogging;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

public sealed class BatchLogger : ILogger, IDisposable, IAsyncDisposable
{
    private readonly ILogger _sink;
    private readonly Channel<LogEntry> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly List<LogEntry> _buffer;
    private readonly BatchLoggerOptions _options;

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
            FullMode = BoundedChannelFullMode.DropOldest
        });

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
        LogInternal(LogLevel.Error, msg, ex);
    }

    public void ExLogCriticalException(Exception ex, string title = "Critical System Error", bool details = false)
    {
        if (!IsEnabled(LogLevel.Critical))
        {
            return;
        }

        var msg = (ExceptionFormatter ?? ExLogger.ExceptionFormatter)(ex, title, details);
        LogInternal(LogLevel.Critical, msg, ex);
    }

    // ---------------- Internal Core ----------------
    private void LogInternal(LogLevel lvl, string msg, Exception ex, params object[] args)
    {
        if (!IsEnabled(lvl))
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
                var readAvailable = await reader.WaitToReadAsync(token).ConfigureAwait(false);

                if (!readAvailable && _buffer.Count == 0)
                {
                    await delayTask.ConfigureAwait(false);
                    continue;
                }

                // Drain queue into buffer
                while (reader.TryRead(out var entry))
                {
                    _buffer.Add(entry);
                    if (_buffer.Count >= batchSize)
                    {
                        await FlushAsync(token).ConfigureAwait(false);
                    }
                }

                if (_buffer.Count > 0)
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
            ExLogger.Log(_sink, LogLevel.Error, "BatchLogger background failure", ex);
        }

        // Final flush
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
        while (_channel.Reader.TryRead(out var e))
        {
            token.ThrowIfCancellationRequested();
            _buffer.Add(e);
        }

        if (_buffer.Count > 0)
        {
            Flush(_buffer);
        }

        await Task.Yield();
    }

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
                Flush(_buffer);
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
}