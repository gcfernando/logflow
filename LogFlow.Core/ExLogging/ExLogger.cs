using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;

namespace LogFlow.Core.ExLogging;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

/// <summary>
/// ExLogger: A high-performance static logging helper for .NET applications.
/// <para>
/// ⚡ Optimized for throughput and low allocations, built on top of <see cref="ILogger"/>.
/// </para>
/// Features:
/// - Predefined logging delegates for each <see cref="LogLevel"/> (avoids allocations from string formatting).
/// - Cached <see cref="EventId"/> values to avoid runtime allocation.
/// - Structured logging support with {Placeholders}.
/// - Exception logging with configurable formatting (stack trace, inner exceptions).
/// - Scope support for contextual logging (single or multiple key-value pairs).
/// - Uses <see cref="ObjectPool{T}"/> to reuse <see cref="StringBuilder"/> for exception messages.
/// - Cached, allocation-free UTC timestamp formatting for exception logs.
/// - Shutdown() to dispose timer safely in dynamic environments (tests/plugins).
/// </summary>
public static class ExLogger
{
    #region Predefined Delegates for Performance

    // NOTE: Delegates now use Exception? to reflect that null is valid, avoiding null-suppression warnings.
    // These delegates are allocation-free and extremely fast when no args are provided (fast-path).

    private static readonly Action<ILogger, string, Exception> _trace =
        LoggerMessage.Define<string>(LogLevel.Trace, new EventId(0, "TraceEvent"), "{Message}");

    private static readonly Action<ILogger, string, Exception> _debug =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "DebugEvent"), "{Message}");

    private static readonly Action<ILogger, string, Exception> _info =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "InformationEvent"), "{Message}");

    private static readonly Action<ILogger, string, Exception> _warn =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "WarningEvent"), "{Message}");

    private static readonly Action<ILogger, string, Exception> _error =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4, "ErrorEvent"), "{Message}");

    private static readonly Action<ILogger, string, Exception> _critical =
        LoggerMessage.Define<string>(LogLevel.Critical, new EventId(5, "CriticalEvent"), "{Message}");

    // No-op for LogLevel.None (index = 6)
    private static readonly Action<ILogger, string, Exception> _noop = static (_, __, ___) => { };

    // 🔒 Fixed array lookup (cheaper than Dictionary)
    // LogLevel enum numeric values: Trace=0..Critical=5, None=6
    private static readonly Action<ILogger, string, Exception>[] _byLevel =
    [
        _trace,    // 0
        _debug,    // 1
        _info,     // 2
        _warn,     // 3
        _error,    // 4
        _critical, // 5
        _noop      // 6 (None)
    ];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Action<ILogger, string, Exception> Resolve(LogLevel level)
    {
        var idx = (int)level;
        return (uint)idx < (uint)_byLevel.Length ? _byLevel[idx] : _info;
    }

    #endregion Predefined Delegates for Performance

    #region Cached EventIds

    // 🔒 Cached EventIds for each log level to avoid allocating new instances at runtime
    private static readonly EventId _traceId = new((int)LogLevel.Trace, "TraceEvent");

    private static readonly EventId _debugId = new((int)LogLevel.Debug, "DebugEvent");
    private static readonly EventId _infoId = new((int)LogLevel.Information, "InformationEvent");
    private static readonly EventId _warnId = new((int)LogLevel.Warning, "WarningEvent");
    private static readonly EventId _errorId = new((int)LogLevel.Error, "ErrorEvent");
    private static readonly EventId _criticalId = new((int)LogLevel.Critical, "CriticalEvent");

    // ✅ Cached instead of creating a new EventId for unknown levels each time
    private static readonly EventId _unknownId = new(9999, "UnknownEvent");

    /// <summary>
    /// Returns a cached <see cref="EventId"/> for the given log level.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EventId GetEventId(LogLevel level) => level switch
    {
        LogLevel.Trace => _traceId,
        LogLevel.Debug => _debugId,
        LogLevel.Information => _infoId,
        LogLevel.Warning => _warnId,
        LogLevel.Error => _errorId,
        LogLevel.Critical => _criticalId,
        _ => _unknownId
    };

    #endregion Cached EventIds

    #region Timestamp Cache (Allocation-Free UTC)

    // Cached ISO-8601 UTC timestamp refreshed every 1 ms via a static timer.
    //  • Per-log cost: zero (atomic reference read only)
    //  • Per-ms allocation: a single new string for the cache (not per log event)
    //  • Completely thread-safe and lock-free.

    private static readonly Timer _utcCacheTimer = new(UpdateUtc, null, 0, 1);

    // Always points to the latest formatted UTC string; updated every 1 ms.
    private static volatile string _cachedUtc = FormatUtc();

    // Per-thread reusable buffer for TryFormat to avoid transient char[] allocations.
    // The "O" (round-trip) format length is 33 characters.
    private static readonly ThreadLocal<char[]> _utcBuffer = new(() => new char[33]);

    // --- Timer callback (static to avoid closure allocations) --------------------
    private static void UpdateUtc(object _) =>
        // Interlocked ensures atomic visibility even if timer callbacks overlap.
        Interlocked.Exchange(ref _cachedUtc, FormatUtc());

    // --- UTC formatting helper ---------------------------------------------------
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string FormatUtc()
    {
        var buf = _utcBuffer.Value!;
        _ = DateTime.UtcNow.TryFormat(buf, out var len, "O", CultureInfo.InvariantCulture);
        return new string(buf, 0, len);
    }

    // --- Safe timer shutdown -----------------------------------------------------
    public static void ShutdownUtcTimer()
    {
        try
        {
            // Final atomic update before disposing the timer.
            _ = Interlocked.Exchange(ref _cachedUtc, FormatUtc());

            _ = _utcCacheTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _utcCacheTimer.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Timer already disposed — ignore safely.
        }
    }

    #endregion Timestamp Cache (Allocation-Free UTC)

    #region Object Pool & Exception Formatter

    // ♻️ Pool of StringBuilders to reduce allocations when formatting exceptions
    // Shared provider allows expansion to other pooled objects later without extra providers.
    private static readonly ObjectPoolProvider _poolProvider = new DefaultObjectPoolProvider { MaximumRetained = 128 };

    private static readonly ObjectPool<StringBuilder> _sbPool = _poolProvider.CreateStringBuilderPool();

    // 🔁 Volatile to make runtime updates thread-safe without locks
    private static volatile Func<Exception, string, bool, string> _exceptionFormatter = FormatExceptionMessageInternal;

    /// <summary>
    /// Global formatter for exception logs.
    /// Can be replaced with a custom formatter (e.g., JSON or structured format).
    /// </summary>
    public static Func<Exception, string, bool, string> ExceptionFormatter
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _exceptionFormatter;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _exceptionFormatter = value ?? FormatExceptionMessageInternal;
    }

    #endregion Object Pool & Exception Formatter

    #region Generic Log Methods

    // ✅ Added non-allocating hot-path overloads (do not remove any existing methods)
    // These avoid the 'params object[]' allocation when 0, 1, or 2 args are used.

    /// <summary>Hot path: no args, no exception.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log(ILogger logger, LogLevel level, string message)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!logger.IsEnabled(level))
        {
            return;
        }

        Resolve(level)(logger, message ?? "N/A", null);
    }

    /// <summary>Hot path: 1 structured arg (avoids params[] allocation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log<T1>(ILogger logger, LogLevel level, string message, T1 arg1)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!logger.IsEnabled(level))
        {
            return;
        }

        logger.Log(level, GetEventId(level), null, message ?? "N/A", arg1);
    }

    /// <summary>Hot path: 2 structured args (avoids params[] allocation).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log<T1, T2>(ILogger logger, LogLevel level, string message, T1 arg1, T2 arg2)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (!logger.IsEnabled(level))
        {
            return;
        }

        logger.Log(level, GetEventId(level), null, message ?? "N/A", arg1, arg2);
    }

    /// <summary>
    /// Generic log method.
    /// ⚡ Uses precompiled delegates for fast-path logging when there are no arguments.
    /// Falls back to structured logging when arguments are provided.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log(ILogger logger, LogLevel level, string message, Exception exception, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (!logger.IsEnabled(level))
        {
            return;
        }

        message ??= "N/A";

        // ⚡ Fast-path: No arguments, use delegate
        if (args is null || args.Length == 0)
        {
            Resolve(level)(logger, message, exception);
            return;
        }

        // Fallback: structured logging with placeholders
        logger.Log(level, GetEventId(level), exception, message, args);
    }

    /// <summary>
    /// Overload: log without an exception object.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log(ILogger logger, LogLevel level, string message, params object[] args) =>
        Log(logger, level, message, null, args);

    /// <summary>
    /// Overload: log with structured message template and exception.
    /// Preserves named placeholders ({UserId}, {OrderId}).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Log(ILogger logger, LogLevel level, Exception exception, string messageTemplate, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(exception);

        if (!logger.IsEnabled(level))
        {
            return;
        }

        // Ensure messageTemplate is never null for structured logging
        messageTemplate ??= "N/A";

        // Fallback: structured logging with placeholders
        logger.Log(level, GetEventId(level), exception, messageTemplate, args ?? Array.Empty<object>());
    }

    // ✅ Non-alloc fast-path helpers (kept internal to avoid breaking API surface but used by convenience methods)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LogNoArgs(ILogger logger, LogLevel level, string message)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        Resolve(level)(logger, message ?? "N/A", null);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LogNoArgs(ILogger logger, LogLevel level, string message, Exception exception)
    {
        if (!logger.IsEnabled(level))
        {
            return;
        }

        Resolve(level)(logger, message ?? "N/A", exception);
    }

    #endregion Generic Log Methods

    #region Convenience Methods

    /// <summary>
    /// Logs a message at <see cref="LogLevel.Trace"/>.
    /// Use this for very detailed diagnostic information (typically only enabled during development).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExLogTrace(this ILogger logger, string message, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(message);

        if (args is null || args.Length == 0)
        {
            LogNoArgs(logger, LogLevel.Trace, message);
            return;
        }

        Log(logger, LogLevel.Trace, message, args);
    }

    /// <summary>
    /// Logs a message at <see cref="LogLevel.Debug"/>.
    /// Useful for debugging and tracing application flow without being as verbose as <see cref="LogTrace"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExLogDebug(this ILogger logger, string message, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(message);

        if (args is null || args.Length == 0)
        {
            LogNoArgs(logger, LogLevel.Debug, message);
            return;
        }

        Log(logger, LogLevel.Debug, message, args);
    }

    /// <summary>
    /// Logs a message at <see cref="LogLevel.Information"/>.
    /// Intended for general application flow, user actions, or significant lifecycle events.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExLogInformation(this ILogger logger, string message, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(message);

        if (args is null || args.Length == 0)
        {
            LogNoArgs(logger, LogLevel.Information, message);
            return;
        }

        Log(logger, LogLevel.Information, message, args);
    }

    /// <summary>
    /// Logs a message at <see cref="LogLevel.Warning"/>.
    /// Use this when something unexpected occurred or a non-critical issue needs attention.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExLogWarning(this ILogger logger, string message, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(message);

        if (args is null || args.Length == 0)
        {
            LogNoArgs(logger, LogLevel.Warning, message);
            return;
        }

        Log(logger, LogLevel.Warning, message, args);
    }

    /// <summary>
    /// Logs a message and an exception at <see cref="LogLevel.Error"/>.
    /// Use this when an operation fails but the application can continue running.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExLogError(this ILogger logger, string message, Exception exception, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(exception);

        if (args is null || args.Length == 0)
        {
            LogNoArgs(logger, LogLevel.Error, message, exception);
            return;
        }

        Log(logger, LogLevel.Error, message, exception, args);
    }

    /// <summary>
    /// Logs a message at <see cref="LogLevel.Error"/>.
    /// Use this overload when you want to log an error without an exception object.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExLogError(this ILogger logger, string message, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(message);

        if (args is null || args.Length == 0)
        {
            LogNoArgs(logger, LogLevel.Error, message);
            return;
        }

        Log(logger, LogLevel.Error, message, args);
    }

    /// <summary>
    /// Logs a message and an exception at <see cref="LogLevel.Critical"/>.
    /// Use this for unrecoverable failures or conditions that require immediate attention.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExLogCritical(this ILogger logger, string message, Exception exception, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(exception);

        if (args is null || args.Length == 0)
        {
            LogNoArgs(logger, LogLevel.Critical, message, exception);
            return;
        }

        Log(logger, LogLevel.Critical, message, exception, args);
    }

    /// <summary>
    /// Logs a message at <see cref="LogLevel.Critical"/> without an exception.
    /// Use this overload when you want to log critical error without an exception object.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ExLogCritical(this ILogger logger, string message, params object[] args)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(message);

        if (args is null || args.Length == 0)
        {
            LogNoArgs(logger, LogLevel.Critical, message);
            return;
        }

        Log(logger, LogLevel.Critical, message, args);
    }

    #endregion Convenience Methods

    #region Exception Logging

    /// <summary>
    /// Logs an exception at Error level with formatted details.
    /// </summary>
    public static void ExLogErrorException(this ILogger logger, Exception ex, string title = "System Error", bool moreDetailsEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ex);

        if (!logger.IsEnabled(LogLevel.Error))
        {
            return;
        }

        var msg = _exceptionFormatter(ex, title, moreDetailsEnabled);
        LogNoArgs(logger, LogLevel.Error, msg, ex);
    }

    /// <summary>
    /// Logs an exception at Critical level with formatted details.
    /// </summary>
    public static void ExLogCriticalException(this ILogger logger, Exception ex, string title = "Critical System Error", bool moreDetailsEnabled = false)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ex);

        if (!logger.IsEnabled(LogLevel.Critical))
        {
            return;
        }

        var msg = _exceptionFormatter(ex, title, moreDetailsEnabled);
        LogNoArgs(logger, LogLevel.Critical, msg, ex);
    }

    /// <summary>
    /// Default exception formatter.
    /// Includes timestamp, type, message, HRESULT, stack trace, and inner exceptions.
    /// Uses pooled <see cref="StringBuilder"/> to reduce allocations.
    /// </summary>
    private static string FormatExceptionMessageInternal(Exception ex, string title, bool moreDetailsEnabled)
    {
        // Heuristic pre-size to reduce growth copies
        var sb = _sbPool.Get();
        try
        {
            _ = sb.Clear();
            _ = sb.EnsureCapacity(1024);

            // 📋 Basic exception info
            _ = sb.Append("Timestamp      : ").AppendLine(_cachedUtc)
              .Append("Title          : ").AppendLine(title ?? "N/A")
              .Append("Exception Type : ").AppendLine(ex.GetType().FullName)
              .Append("Message        : ").AppendLine(ex.Message?.Trim() ?? "N/A")
              .Append("HResult        : ").Append(ex.HResult).AppendLine()
              .Append("Source         : ").AppendLine(ex.Source ?? "N/A")
              .Append("Target Site    : ").AppendLine(ex.TargetSite?.Name ?? "N/A");

            // 📋 Detailed info (optional)
            if (moreDetailsEnabled)
            {
                var st = ex.StackTrace;
                if (!string.IsNullOrWhiteSpace(st))
                {
                    _ = sb.AppendLine().AppendLine("Stack Trace    :").AppendLine(st.Trim());
                }

                if (ex.InnerException is not null)
                {
                    _ = sb.AppendLine().AppendLine("---- Inner Exceptions ----");
                    AppendInnerExceptionDetails(sb, ex.InnerException, 1, maxDepth: 3);
                }
            }

            return sb.ToString();
        }
        finally
        {
            _sbPool.Return(sb);
        }
    }

    /// <summary>
    /// Recursively appends details of inner exceptions with indentation.
    /// Stops at maxDepth to avoid excessive logging in recursive exception chains.
    /// </summary>
    private static void AppendInnerExceptionDetails(StringBuilder sb, Exception inner, int depth, int maxDepth = 5)
    {
        if (inner is null || depth > maxDepth)
        {
            return;
        }

        // indent is a few '>' chars; avoid string.Join/LINQ
        var indent = new string('>', depth);

        _ = sb.Append(indent).Append(" Exception Type : ").AppendLine(inner.GetType().FullName)
          .Append(indent).Append(" Message        : ").AppendLine(inner.Message?.Trim() ?? "N/A")
          .Append(indent).Append(" HResult        : ").Append(inner.HResult).AppendLine()
          .Append(indent).Append(" Source         : ").AppendLine(inner.Source ?? "N/A")
          .Append(indent).Append(" Target Site    : ").AppendLine(inner.TargetSite?.Name ?? "N/A");

        var st = inner.StackTrace;
        if (!string.IsNullOrWhiteSpace(st))
        {
            _ = sb.Append(indent).AppendLine(" Stack Trace    :")
              .AppendLine(st.Trim());
        }

        // Special case: AggregateException contains multiple inner exceptions
        if (inner is AggregateException agg && agg.InnerExceptions.Count > 0)
        {
            for (var i = 0; i < agg.InnerExceptions.Count; i++)
            {
                _ = sb.AppendLine();
                AppendInnerExceptionDetails(sb, agg.InnerExceptions[i], depth + 1, maxDepth);
            }
        }
        else if (inner.InnerException is not null)
        {
            _ = sb.AppendLine();
            AppendInnerExceptionDetails(sb, inner.InnerException, depth + 1, maxDepth);
        }
    }

    #endregion Exception Logging

    #region Log Scope Helper

    /// <summary>
    /// Begins a structured logging scope with a single key-value pair.
    /// Useful for correlating logs (e.g., RequestId, UserId).
    /// </summary>
    public static IDisposable ExBeginScope(this ILogger logger, string key, object value)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return string.IsNullOrWhiteSpace(key)
            ? throw new ArgumentException("Key cannot be null or whitespace.", nameof(key))
            : logger.BeginScope(new SingleScope(key, value)) ?? NullScope.Instance;
    }

    /// <summary>
    /// Begins a structured logging scope with multiple key-value pairs.
    /// Optimized for small dictionaries (≤4 items) to reduce allocations.
    /// </summary>
    public static IDisposable ExBeginScope(this ILogger logger, IDictionary<string, object> context)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (context is null || context.Count == 0)
        {
            return NullScope.Instance;
        }

        // ⚡ Optimization: use array wrapper for small contexts
        if (context.Count <= 4)
        {
            var items = new KeyValuePair<string, object>[context.Count];
            var i = 0;
            foreach (var kv in context)
            {
                items[i++] = new KeyValuePair<string, object>(kv.Key, kv.Value ?? "N/A");
            }
            return logger.BeginScope(new SmallScopeWrapper(items)) ?? NullScope.Instance;
        }
        else
        {
            // Fallback: use List wrapper for larger contexts
            var safe = new List<KeyValuePair<string, object>>(context.Count);
            foreach (var kv in context)
            {
                safe.Add(new KeyValuePair<string, object>(kv.Key, kv.Value ?? "N/A"));
            }
            return logger.BeginScope(new ScopeWrapper(safe)) ?? NullScope.Instance;
        }
    }

    /// <summary>
    /// Represents a scope with a single key-value pair.
    /// Implements IReadOnlyList to avoid iterator allocations.
    /// </summary>
    private readonly struct SingleScope : IReadOnlyList<KeyValuePair<string, object>>
    {
        private readonly KeyValuePair<string, object> _pair;

        public SingleScope(string key, object value) =>
            _pair = new KeyValuePair<string, object>(key, value ?? "N/A");

        public int Count => 1;

        public KeyValuePair<string, object> this[int index] =>
            index == 0
                ? _pair
                : throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be 0 for this collection.");

        public Enumerator GetEnumerator() => new(_pair);

        IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => $"{_pair.Key}={_pair.Value}";

        public struct Enumerator : IEnumerator<KeyValuePair<string, object>>
        {
            private bool _moved;

            public Enumerator(KeyValuePair<string, object> value) => (Current, _moved) = (value, false);

            public bool MoveNext()
            {
                if (_moved)
                {
                    return false;
                }

                _moved = true;
                return true;
            }

            public KeyValuePair<string, object> Current { get; }
            readonly object IEnumerator.Current => Current;

            public void Reset() => _moved = false;

            public readonly void Dispose()
            { }
        }
    }

    /// <summary>
    /// Represents a scope with multiple key-value pairs using a List.
    /// </summary>
    private sealed class ScopeWrapper : IReadOnlyList<KeyValuePair<string, object>>
    {
        private readonly List<KeyValuePair<string, object>> _items;

        public ScopeWrapper(List<KeyValuePair<string, object>> items) => _items = items;

        public int Count => _items.Count;
        public KeyValuePair<string, object> this[int index] => _items[index];

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

        public override string ToString()
        {
            // Avoid LINQ/Join to minimize allocs in rare ToString paths
            if (_items.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(_items.Count * 16);
            for (var i = 0; i < _items.Count; i++)
            {
                if (i > 0)
                {
                    _ = sb.Append(' ');
                }

                var kv = _items[i];
                _ = sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// Represents a scope optimized for small dictionaries (≤4 items).
    /// Uses an array instead of a List to minimize allocations.
    /// </summary>
    private sealed class SmallScopeWrapper : IReadOnlyList<KeyValuePair<string, object>>
    {
        private readonly KeyValuePair<string, object>[] _items;

        public SmallScopeWrapper(KeyValuePair<string, object>[] items) => _items = items;

        public int Count => _items.Length;
        public KeyValuePair<string, object> this[int index] => _items[index];

        public Enumerator GetEnumerator() => new(_items);

        IEnumerator<KeyValuePair<string, object>> IEnumerable<KeyValuePair<string, object>>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<string, object>>
        {
            private readonly KeyValuePair<string, object>[] _arr;
            private int _index;

            public Enumerator(KeyValuePair<string, object>[] arr) => (_arr, _index) = (arr, -1);

            public bool MoveNext() => ++_index < _arr.Length;

            public readonly KeyValuePair<string, object> Current => _arr[_index];

            readonly object IEnumerator.Current => _arr[_index];

            public void Reset() => _index = -1;

            public readonly void Dispose()
            { }
        }

        public override string ToString()
        {
            if (_items.Length == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(_items.Length * 16);
            for (var i = 0; i < _items.Length; i++)
            {
                if (i > 0)
                {
                    _ = sb.Append(' ');
                }

                var kv = _items[i];
                _ = sb.Append(kv.Key).Append('=').Append(kv.Value);
            }
            return sb.ToString();
        }
    }

    #endregion Log Scope Helper

    #region Extensibility Hooks (Async/Batch Ready)

    /// <summary>
    /// Optional hook for future async sink filtering (no behavior change now).
    /// </summary>
    public static Func<LogLevel, string, Exception, bool> AsyncSinkFilter { get; private set; } = static (_, _, _) => true;

    /// <summary>
    /// Configure a custom filter intended for a future async/batching sink (placeholder).
    /// </summary>
    public static void UseAsyncSinkProvider(Func<LogLevel, string, Exception, bool> filter)
        => AsyncSinkFilter = filter ?? AsyncSinkFilter;

    /// <summary>
    /// Flushes any pending log batches asynchronously.
    /// Currently, this is a lightweight placeholder that ensures compatibility
    /// with future async batching or off-thread sink dispatch implementations.
    /// </summary>
    public static async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Yield();

            // Future expansion placeholder safely wrapped.
        }
        catch (OperationCanceledException)
        {
            // Graceful exit for cooperative cancellation
        }
        catch (ObjectDisposedException)
        {
            // Future async sink might be disposed already
        }
        catch (Exception ex)
        {
            // Defensive fail-safe (log or swallow)
            Debug.WriteLine($"ExLogger.FlushAsync encountered error: {ex}");
        }
    }

    #endregion Extensibility Hooks (Async/Batch Ready)
}