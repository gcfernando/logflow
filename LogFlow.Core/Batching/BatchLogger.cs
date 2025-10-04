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

    // NOTE: _buffer is only touched by the background worker and flush methods
    // after the worker has been cancelled/awaited. It is NOT thread-safe by design.
    private readonly List<LogEntry> _buffer;
    private readonly BatchLoggerOptions _options;

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
                if (_options.OnFlushAsync is not null)
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

        // Use custom sink if provided, else original sink
        if (_options.OnFlushAsync is not null)
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
        if (_buffer.Count == 0 || _options.OnFlushAsync is null)
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
            await _options.OnFlushAsync(snapshot, token).ConfigureAwait(false);

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
                if (_options.OnFlushAsync is not null)
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
}