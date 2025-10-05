using System.Runtime.CompilerServices;

namespace LogFlow.Core.Batching;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

public sealed class BatchLoggerMetrics
{
    private long _dropped;
    private long _batches;
    private long _flushed;
    private long _lastFlushTicks;

    public long DroppedCount => Interlocked.Read(ref _dropped);
    public long BatchCount => Interlocked.Read(ref _batches);
    public long TotalFlushed => Interlocked.Read(ref _flushed);
    public DateTime LastFlushUtc => new(Interlocked.Read(ref _lastFlushTicks), DateTimeKind.Utc);
    public double AverageBatchSize => _batches == 0 ? 0 : (double)_flushed / _batches;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void IncrementDropped() => Interlocked.Increment(ref _dropped);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void AddFlushed(int count)
    {
        _ = Interlocked.Add(ref _flushed, count);
        _ = Interlocked.Increment(ref _batches);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void SetLastFlush(DateTime utc) =>
        Interlocked.Exchange(ref _lastFlushTicks, utc.Ticks);
}