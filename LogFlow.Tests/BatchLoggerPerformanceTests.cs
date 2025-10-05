using System.Diagnostics;
using LogFlow.Core.Batching;
using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Logging;
using Moq;
using xRetry;

namespace LogFlow.Tests;

/*
 * Developer ::> Gehan Fernando
 * Date      ::> 2025-10-04
 * Contact   ::> f.gehan@gmail.com / +46 73 701 40 25
*/

public class BatchLoggerPerformanceTests
{
    private static BatchLogger MakeFastLogger(Action<string> onFlush = null)
    {
        var sink = new Mock<ILogger>();
        _ = sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var opts = new BatchLoggerOptions
        {
            BatchSize = 500,
            Capacity = 50_000,
            FlushInterval = TimeSpan.FromMilliseconds(100),
            ForwardToILoggerSink = false,
            OnFlushAsync = async (entries, _) =>
            {
                onFlush?.Invoke($"Flushed {entries.Count}");
                await Task.Yield();
            }
        };

        return new BatchLogger(sink.Object, opts);
    }

    [RetryFact(10, 1000)]
    public async Task HighVolume_Throughput_ShouldRemainStable()
    {
        const int total = 100_000;
        var logger = MakeFastLogger();
        var sw = Stopwatch.StartNew();

        _ = Parallel.For(0, total, i => logger.ExLogInformation("Message {Index}", i));

        sw.Stop();
        await logger.FlushAsync();
        await logger.DisposeAsync();

        var elapsed = sw.ElapsedMilliseconds;
        var throughput = total / (elapsed / 1000.0);

        Assert.True(throughput > 50_000, $"Throughput too low: {throughput:N0} logs/sec");
    }

    [RetryFact(10, 1000)]
    public async Task Memory_ShouldRemainStable_After_Dispose()
    {
        var baselineAlloc = GC.GetAllocatedBytesForCurrentThread();

        await using (var logger = MakeFastLogger())
        {
            for (var i = 0; i < 10_000; i++)
            {
                logger.ExLogError("Error {I}", i);
            }

            await logger.FlushAsync();
        }

        await Task.Delay(300);

        var postAlloc = GC.GetAllocatedBytesForCurrentThread();
        var allocatedDuringTest = postAlloc - baselineAlloc;

        Assert.InRange(allocatedDuringTest, -10_000_000, 100_000_000);
        var wr = await CreateWeakLoggerReferenceAsync();

        for (var i = 0; i < 20 && wr.IsAlive; i++)
        {
            await Task.Delay(100);
        }

        Assert.False(wr.IsAlive, "BatchLogger instance should be collectible after disposal.");
    }

    [RetryFact(10, 1000)]
    public async Task BackgroundWorker_ShouldExitCleanly_OnDispose()
    {
        var before = GC.GetTotalMemory(forceFullCollection: false);

        await using (var logger = MakeFastLogger())
        {
            for (var i = 0; i < 10_000; i++)
            {
                logger.ExLogError("Error {I}", i);
            }

            await logger.FlushAsync();
        }

        await Task.Delay(300);

        var after = GC.GetTotalMemory(forceFullCollection: false);
        var delta = after - before;

        Assert.InRange(delta, -50_000_000, 50_000_000);

        var wr = new WeakReference(new object());
        await using (var logger2 = MakeFastLogger())
        {
            wr = new WeakReference(logger2);
        }

        for (var i = 0; i < 20 && wr.IsAlive; i++)
        {
            await Task.Delay(100);
        }

        Assert.False(wr.IsAlive, "BatchLogger instance should be collectible after disposal.");
    }

    [RetryFact(10, 1000)]
    public async Task Parallel_Writers_No_Data_Races_Or_Drops()
    {
        var logger = MakeFastLogger();
        const int total = 20_000;

        _ = Parallel.For(0, total, i => logger.ExLogDebug("Message {i}", i));

        await logger.FlushAsync();
        var dropped = logger.Metrics.DroppedCount;

        Assert.Equal(0, dropped);
    }

    private static async Task<WeakReference> CreateWeakLoggerReferenceAsync()
    {
        var logger = MakeFastLogger();
        var wr = new WeakReference(logger);

        await logger.DisposeAsync();

        return wr;
    }
}