namespace LogFlow.Core.ExLogging;

/// <summary>
/// Represents a no-op logging scope.
/// Prevents null reference exceptions when BeginScope() returns null.
/// </summary>
internal sealed class NullScope : IDisposable
{
    public static readonly NullScope Instance = new();

    private NullScope()
    { }

    public void Dispose()
    { }
}