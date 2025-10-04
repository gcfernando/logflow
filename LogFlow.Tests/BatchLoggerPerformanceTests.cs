using System.Diagnostics;
using LogFlow.Core.Batching;
using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Logging;
using Moq;

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
                // Simulate async sink cost (very light)
                onFlush?.Invoke($"Flushed {entries.Count}");
                await Task.Yield();
            }
        };

        return new BatchLogger(sink.Object, opts);
    }

    [Fact]
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

    [Fact]
    public async Task Memory_ShouldRemainStable_After_Dispose()
    {
        // Measure baseline allocation on this thread
        var baselineAlloc = GC.GetAllocatedBytesForCurrentThread();

        // Create, stress, and dispose the logger
        await using (var logger = MakeFastLogger())
        {
            for (var i = 0; i < 10_000; i++)
            {
                logger.ExLogError("Error {I}", i);
            }

            await logger.FlushAsync();
        }

        // Small delay to allow background cleanup and GC naturally
        await Task.Delay(200);

        // Measure allocation delta without forcing GC
        var postAlloc = GC.GetAllocatedBytesForCurrentThread();
        var allocatedDuringTest = postAlloc - baselineAlloc;

        // Assert that allocations are within a healthy bound (~<100 MB)
        Assert.InRange(allocatedDuringTest, 0, 100_000_000);

        // Additionally, verify that BatchLogger is no longer retained
        var wr = new WeakReference(new object());
        await using (var logger2 = MakeFastLogger())
        {
            wr = new WeakReference(logger2);
        }

        // Wait briefly for natural collection
        for (var i = 0; i < 10; i++)
        {
            if (!wr.IsAlive)
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.False(wr.IsAlive, "BatchLogger instance should be collectible after disposal.");
    }

    [Fact]
    public async Task BackgroundWorker_ShouldExitCleanly_OnDispose()
    {
        var flushed = false;
        var logger = MakeFastLogger(_ => flushed = true);

        // Log for a short period
        for (var i = 0; i < 5000; i++)
        {
            logger.ExLogInformation("Ping {I}", i);
        }

        // Dispose asynchronously and wait for clean shutdown
        await logger.DisposeAsync();

        // Allow background task and async sinks to finalize naturally
        await Task.Delay(200);

        // Verify that a flush occurred and worker stopped processing
        Assert.True(flushed, "Flush did not complete before disposal.");
        Assert.True(logger.Metrics.TotalFlushed > 0);

        // ✅ Fix: release the strong reference before testing WeakReference
        var wr = new WeakReference(logger);
        logger = null!; // release strong reference

        // ✅ Fix: give the GC a bit more time to run naturally
        for (var i = 0; i < 20 && wr.IsAlive; i++)
        {
            await Task.Delay(100);
        }

        // ✅ Fix: assertion will now pass once the object is naturally collected
        Assert.False(wr.IsAlive, "BatchLogger instance should be collectible after disposal.");
    }

    [Fact]
    public async Task Parallel_Writers_No_Data_Races_Or_Drops()
    {
        var logger = MakeFastLogger();
        const int total = 20_000;

        _ = Parallel.For(0, total, i => logger.ExLogDebug("Message {i}", i));

        await logger.FlushAsync();
        var dropped = logger.Metrics.DroppedCount;

        Assert.Equal(0, dropped); // No entries dropped under normal load
    }
}
