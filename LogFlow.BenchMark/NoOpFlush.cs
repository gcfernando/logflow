using LogFlow.Core.Batching.Model;

namespace LogFlow.BenchMark;

internal static class NoOpFlush
{
    public static Task RunAsync(IReadOnlyList<BatchLogEntry> _, CancellationToken __) => Task.CompletedTask;
}