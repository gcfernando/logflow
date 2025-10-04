using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/// <summary>
/// Represents a single log entry in a batch.
/// Used by <see cref="BatchLogger"/> custom flush handlers.
/// </summary>
public readonly record struct BatchLogEntry(
    LogLevel Level,
    string Message,
    Exception Exception,
    IReadOnlyList<object> Args);