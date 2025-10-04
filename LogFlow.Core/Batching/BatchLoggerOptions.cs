namespace LogFlow.Core.Batching;

public sealed class BatchLoggerOptions
{
    public int Capacity { get; set; } = 10_000;
    public int BatchSize { get; set; } = 100;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(200);
}