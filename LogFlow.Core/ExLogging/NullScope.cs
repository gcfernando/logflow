namespace LogFlow.Core.ExLogging;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

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