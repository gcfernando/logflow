using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching.Model;

/// <summary>
/// Represents a single structured log entry within a batched logging operation.
/// </summary>
/// <remarks>
/// Each <see cref="BatchLogEntry"/> encapsulates the information needed to render or persist
/// a single log event during batch flushing. It is the lightweight data model used internally
/// by <see cref="BatchLogger"/> and externally by any custom flush or sink implementations.
/// </remarks>
/// <example>
/// Example usage inside a custom flush handler:
/// <code language="csharp">
/// options.OnFlushAsync = async (entries, token) =>
/// {
///     foreach (var e in entries)
///     {
///         Console.WriteLine($"[{e.Level}] {e.Message}");
///     }
/// };
/// </code>
/// </example>
public readonly record struct BatchLogEntry(
    /// <summary>
    /// The severity level of the log entry.
    /// </summary>
    /// <remarks>
    /// Corresponds to the <see cref="LogLevel"/> enum used by
    /// <see cref="Microsoft.Extensions.Logging"/>.
    /// Examples: <see cref="LogLevel.Information"/>, <see cref="LogLevel.Error"/>.
    /// </remarks>
    LogLevel Level,

    /// <summary>
    /// The main message text associated with the log entry.
    /// </summary>
    /// <remarks>
    /// This message is typically formatted by the logger at enqueue time
    /// and may include structured parameters expanded into readable text.
    /// </remarks>
    string Message,

    /// <summary>
    /// The exception associated with this log entry, if any.
    /// </summary>
    /// <remarks>
    /// May be <see langword="null"/> if the log entry was not created from an exception.
    /// </remarks>
    Exception Exception,

    /// <summary>
    /// The collection of structured arguments captured with the log entry.
    /// </summary>
    /// <remarks>
    /// This is typically an immutable list of values passed to <c>logger.LogInformation()</c>
    /// or other log methods.
    /// May be empty but never <see langword="null"/>.
    /// </remarks>
    IReadOnlyList<object> Args
);
