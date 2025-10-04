using System.Reflection;
using LogFlow.Core.Batching;
using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Logging;
using Moq;

namespace LogFlow.Tests;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

[Collection("Non-Parallel BatchLogger Core")]
public class BatchLogger_CoreTests
{
    private static BatchLogger MakeLogger(
        Mock<ILogger> sinkMock,
        BatchLoggerOptions opts = null)
    {
        sinkMock ??= new Mock<ILogger>();
        // Let everything be enabled
        sinkMock.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        return new BatchLogger(sinkMock.Object, opts ?? new BatchLoggerOptions
        {
            // keep things snappy in tests
            BatchSize = 8,
            FlushInterval = System.TimeSpan.FromMilliseconds(50),
            ForwardToILoggerSink = true
        });
    }

    [Fact]
    public async Task ForwardingOnly_ForwardsToUnderlyingILogger()
    {
        var sink = new Mock<ILogger>();
        sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var opts = new BatchLoggerOptions
        {
            ForwardToILoggerSink = true,
            File = new BatchFileOptions { Enabled = false },
            Database = new BatchDatabaseOptions { Enabled = false }
        };

        await using var logger = MakeLogger(sink, opts);

        logger.ExLogInformation("hello {X}", 42);
        await logger.FlushAsync();

        sink.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            null,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()
        ), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Filter_BlocksEntries()
    {
        var sink = new Mock<ILogger>();
        sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var opts = new BatchLoggerOptions
        {
            Filter = (lvl, _, __, ___) => lvl >= LogLevel.Warning
        };

        await using var logger = MakeLogger(sink, opts);

        logger.ExLogInformation("will be filtered");
        logger.ExLogWarning("will pass");
        await logger.FlushAsync();

        // Only warning or above should have been forwarded
        sink.Verify(l => l.Log(LogLevel.Information,
            It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.Never);

        sink.Verify(l => l.Log(LogLevel.Warning,
            It.IsAny<EventId>(), It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.AtLeastOnce);
    }

    private sealed class KvState : List<KeyValuePair<string, object>>
    {
        public KvState(params (string, object)[] items) =>
            AddRange(items.Select(t => new KeyValuePair<string, object>(t.Item1, t.Item2)));

        public override string ToString() => "formatted-msg";
    }

    [Fact]
    public async Task Args_AreExtractedFromStructuredState_AndNullsBecomeNA()
    {
        var sink = new Mock<ILogger>();
        sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        await using var logger = MakeLogger(sink);

        // Call ILogger-style Log<TState> directly to exercise state path
        logger.Log(LogLevel.Information, new EventId(1, "e"),
            new KvState(("a", 1), ("b", null), ("c", "z")),
            null,
            (st, _) => st.ToString());

        await logger.FlushAsync();

        // Capture the forwarded structured state and confirm formatted message
        sink.Verify(l => l.Log(
            LogLevel.Information,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            null,
            It.IsAny<Func<It.IsAnyType, Exception, string>>()), Times.AtLeastOnce);

        // We also assert that our filter logic saw args values [1,"N/A","z"] by logging again and intercepting
        // (Moq can't easily pull the args list out of MEL's internal state object, so we assert by reusing the code path:)
        // Create an OnFlushAsync to capture what BatchLogger enqueued:
        var captured = new List<BatchLogEntry>();
        var opts = new BatchLoggerOptions
        {
            OnFlushAsync = (entries, _) =>
            {
                captured.AddRange(entries);
                return Task.CompletedTask;
            }
        };
        await using var logger2 = new BatchLogger(sink.Object, opts);
        logger2.Log(LogLevel.Information, new EventId(1, "e"),
            new KvState(("a", 1), ("b", null), ("c", "z")),
            null,
            (st, _) => st.ToString());

        await logger2.FlushAsync();

        Assert.Single(captured);
        Assert.Equal("formatted-msg", captured[0].Message);
        Assert.Collection(captured[0].Args,
            v => Assert.Equal(1, v),
            v => Assert.Equal("N/A", v),
            v => Assert.Equal("z", v));
    }

    [Fact]
    public async Task Worker_FlushesOnTimer_WhenNotReachingBatchSize()
    {
        var called = 0;
        var opts = new BatchLoggerOptions
        {
            BatchSize = 1000, // won't hit
            FlushInterval = System.TimeSpan.FromMilliseconds(50),
            OnFlushAsync = (entries, _) => { Interlocked.Add(ref called, entries.Count); return Task.CompletedTask; }
        };

        var sink = new Mock<ILogger>();
        sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);
        await using var logger = MakeLogger(sink, opts);

        logger.ExLogInformation("a");
        logger.ExLogInformation("b");

        // Wait until a timer-based flush happens
        await SpinWaitAsync(() => called >= 2, 2000);

        Assert.True(called >= 2);
        Assert.True(logger.Metrics.BatchCount >= 1);
    }

    [Fact]
    public async Task FlushAsync_Throws_WhenCancelled()
    {
        var sink = new Mock<ILogger>();
        sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        await using var logger = MakeLogger(sink);
        logger.ExLogInformation("x");
        logger.ExLogInformation("y");

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => logger.FlushAsync(cts.Token));
    }

    [Fact]
    public async Task ChannelDrops_IncreaseMetrics()
    {
        var sink = new Mock<ILogger>();
        sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var opts = new BatchLoggerOptions
        {
            Capacity = 5,
            BatchSize = 10_000,
            FlushInterval = TimeSpan.FromSeconds(10)
        };

        await using var logger = MakeLogger(sink, opts);

        // Stop worker to simulate backpressure
        var ctsField = typeof(BatchLogger)
            .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance);
        var cts = (CancellationTokenSource)ctsField.GetValue(logger)!;
        await cts.CancelAsync();

        // Flood the channel
        for (var i = 0; i < 200; i++)
        {
            logger.ExLogInformation("msg {i}", i);
        }

        Assert.True(logger.Metrics.DroppedCount > 0);
    }

    [Fact]
    public async Task Dispose_And_DisposeAsync_FlushRemaining()
    {
        var count = 0;
        var opts = new BatchLoggerOptions
        {
            BatchSize = 1000,
            OnFlushAsync = (entries, _) =>
            {
                Interlocked.Add(ref count, entries.Count);
                return Task.CompletedTask;
            }
        };

        var sink = new Mock<ILogger>();
        sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        // Dispose()
        var logger1 = MakeLogger(sink, opts);
        for (var i = 0; i < 10; i++)
        {
            logger1.ExLogInformation("x");
        }

        await logger1.DisposeAsync();

        // DisposeAsync()
        var logger2 = MakeLogger(sink, opts);
        for (var i = 0; i < 10; i++)
        {
            logger2.ExLogInformation("y");
        }

        await logger2.DisposeAsync();

        Assert.True(count >= 20);
    }

    [Fact]
    public void BeginScope_UsesUnderlying_OrNullScope()
    {
        var sink = new Mock<ILogger>();
        sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        // Return a real scope
        var disposable = Mock.Of<System.IDisposable>();
        sink.Setup(l => l.BeginScope(It.IsAny<object>())).Returns(disposable);
        using var logger = MakeLogger(sink);

        using var s1 = logger.BeginScope(("k", "v"));
        Assert.Same(disposable, s1);

        // Return null -> expect non-null placeholder (no throw)
        sink.Setup(l => l.BeginScope(It.IsAny<object>())).Returns((System.IDisposable)null);
        using var s2 = logger.BeginScope(("k2", "v2"));
        Assert.NotNull(s2);
    }

    private static async Task SpinWaitAsync(System.Func<bool> predicate, int timeoutMs)
    {
        var start = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (start.ElapsedMilliseconds > timeoutMs)
            {
                break;
            }

            await Task.Delay(10);
        }
    }
}