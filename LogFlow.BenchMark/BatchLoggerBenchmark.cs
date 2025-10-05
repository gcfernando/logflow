using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using LogFlow.Core.Batching;
using LogFlow.Core.Batching.Model;
using LogFlow.Core.Batching.Model.Enums;
using LogFlow.Core.ExLogging;
using Microsoft.Extensions.Logging;

namespace LogFlow.BenchMark;

[CategoriesColumn, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.Declared)]
[MemoryDiagnoser(displayGenColumns: true)]
[ThreadingDiagnoser]
[GcServer(true)]
[GcForce(true)]
public class BatchLoggerBenchmarks
{
    private ILogger _downstream = default!;
    private BatchLogger _batch = default!;
    private BatchLogger _batchNoForward = default!;

    private string _templ = default!;
    private object[] _templArgs = default!;
    private CancellationTokenSource _cts = default!;
    private TempDir _tempDir = default!;

    [Params(100, 1_000, 10_000)]
    public int N;

    [GlobalSetup]
    public void Setup()
    {
        _downstream = DevNullLogger.Instance;
        _templ = "Payment {PaymentId} for {UserId} amount {Amount}";
        _templArgs = [98765, 42, 123.45];

        _cts = new CancellationTokenSource();

        _tempDir = new TempDir();

        _batch = new BatchLogger(_downstream, new BatchLoggerOptions
        {
            Capacity = 100_000,
            BatchSize = 512,
            FlushInterval = TimeSpan.FromMilliseconds(250),
            ForwardToILoggerSink = true,
            File = new BatchFileOptions
            {
                Enabled = false
            },
            Database = new BatchDatabaseOptions { Enabled = false }
        });

        _batchNoForward = new BatchLogger(_downstream, new BatchLoggerOptions
        {
            Capacity = 100_000,
            BatchSize = 512,
            FlushInterval = TimeSpan.FromMilliseconds(250),
            ForwardToILoggerSink = false,
            File = new BatchFileOptions
            {
                Enabled = true,
                Path = _tempDir.Combine("app.log"),
                Format = BatchFileFormat.Text,
                RollingInterval = RollingInterval.None,
                RollingSizeBytes = 0
            },
            Database = new BatchDatabaseOptions { Enabled = false }
        });

        _batch.ExLogInformation("warmup");
        _batchNoForward.ExLogInformation("warmup");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _cts.Cancel();
        _batch.Dispose();
        _batchNoForward.Dispose();
        _cts.Dispose();
        _tempDir.Dispose();
    }

    [Benchmark(Baseline = true, Description = "ILogger direct (no batch) N logs"), BenchmarkCategory("Batch.Enqueue")]
    public void ILogger_Direct_NoBatch()
    {
        for (var i = 0; i < N; i++)
        {
            ExLogger.Log(_downstream, LogLevel.Information, _templ, _templArgs);
        }
    }

    [Benchmark(Description = "BatchLogger.Enqueue N logs"), BenchmarkCategory("Batch.Enqueue")]
    public void BatchLogger_Enqueue_N()
    {
        for (var i = 0; i < N; i++)
        {
            _batch.Log(LogLevel.Information, new EventId(0), _templArgs, null, (_, __) => _templ);
        }
    }

    [Benchmark(Description = "BatchLogger.Enqueue N logs (filter Info+)"), BenchmarkCategory("Batch.Enqueue")]
    public void BatchLogger_Enqueue_WithFilter()
    {
        var batch = new BatchLogger(_downstream, new BatchLoggerOptions
        {
            Capacity = 100_000,
            BatchSize = 512,
            FlushInterval = TimeSpan.FromSeconds(5),
            Filter = (lvl, _, __, ___) => lvl >= LogLevel.Information
        });

        for (var i = 0; i < N; i++)
        {
            batch.Log(LogLevel.Information, new EventId(0), _templArgs, null, (_, __) => _templ);
        }

        batch.Dispose();
    }

    [Benchmark(Description = "Flush -> default (forward to ILogger)"), BenchmarkCategory("Batch.Flush")]
    public Task Flush_Default_ForwardILogger()
    {
        for (var i = 0; i < N; i++)
        {
            _batch.Log(LogLevel.Information, new EventId(0), _templArgs, null, (_, __) => _templ);
        }

        return _batch.FlushAsync();
    }

    [Benchmark(Description = "Flush -> composed FileSink (Text)"), BenchmarkCategory("Batch.Flush")]
    public Task Flush_FileSink_Text()
    {
        for (var i = 0; i < N; i++)
        {
            _batchNoForward.Log(LogLevel.Information, new EventId(0), _templArgs, null, (_, __) => _templ);
        }

        return _batchNoForward.FlushAsync();
    }

    [Benchmark(Description = "Flush -> composed FileSink (JSON)"), BenchmarkCategory("Batch.Flush")]
    public async Task Flush_FileSink_Json()
    {
        using var td = new TempDir();
        var batch = new BatchLogger(_downstream, new BatchLoggerOptions
        {
            Capacity = 100_000,
            BatchSize = 512,
            FlushInterval = TimeSpan.FromSeconds(5),
            ForwardToILoggerSink = false,
            File = new BatchFileOptions
            {
                Enabled = true,
                Path = td.Combine("app.jsonl"),
                Format = BatchFileFormat.Json,
                RollingInterval = RollingInterval.None
            }
        });

        for (var i = 0; i < N; i++)
        {
            batch.Log(LogLevel.Information, new EventId(0), _templArgs, null, (_, __) => _templ);
        }

        await batch.FlushAsync();
        await batch.DisposeAsync();
    }

    [Benchmark(Description = "Flush -> custom OnFlushAsync(no-op)"), BenchmarkCategory("Batch.Flush")]
    public async Task Flush_Custom_OnFlushAsync()
    {
        var batch = new BatchLogger(_downstream, new BatchLoggerOptions
        {
            Capacity = 100_000,
            BatchSize = 512,
            FlushInterval = TimeSpan.FromSeconds(5),
            OnFlushAsync = NoOpFlush.RunAsync,
            ForwardToILoggerSink = false
        });

        for (var i = 0; i < N; i++)
        {
            batch.Log(LogLevel.Information, new EventId(0), _templArgs, null, (_, __) => _templ);
        }

        await batch.FlushAsync();
        await batch.DisposeAsync();
    }

    [Params(1, 4, 16)]
    public int Writers;

    [Benchmark(Description = "Parallel writers -> Enqueue + final Flush"), BenchmarkCategory("Batch.Parallel")]
    public async Task Parallel_Writers_Enqueue_Then_Flush()
    {
        var batch = new BatchLogger(_downstream, new BatchLoggerOptions
        {
            Capacity = 200_000,
            BatchSize = 1024,
            FlushInterval = TimeSpan.FromSeconds(10),
            ForwardToILoggerSink = false,
            OnFlushAsync = NoOpFlush.RunAsync
        });

        var perWriter = N / Writers;
        var tasks = new Task[Writers];

        for (var w = 0; w < Writers; w++)
        {
            tasks[w] = Task.Run(() =>
            {
                for (var i = 0; i < perWriter; i++)
                {
                    batch.Log(LogLevel.Information, new EventId(0), _templArgs, null, (_, __) => _templ);
                }
            });
        }

        await Task.WhenAll(tasks);
        await batch.FlushAsync();
        await batch.DisposeAsync();
    }

    [Benchmark(Description = "FileSink size rolling (small limit)"), BenchmarkCategory("Batch.Rolling")]
    public async Task FileSink_Size_Rolling()
    {
        using var td = new TempDir();
        var opts = new BatchLoggerOptions
        {
            Capacity = 200_000,
            BatchSize = 1024,
            ForwardToILoggerSink = false,
            File = new BatchFileOptions
            {
                Enabled = true,
                Path = td.Combine("roll.log"),
                Format = BatchFileFormat.Text,
                RollingInterval = RollingInterval.None,
                RollingSizeBytes = 8 * 1024,
                RetainedFileCountLimit = 3
            }
        };

        var batch = new BatchLogger(_downstream, opts);

        for (var i = 0; i < N; i++)
        {
            batch.Log(LogLevel.Information, new EventId(0), _templArgs, null, (_, __) => _templ);
        }

        await batch.FlushAsync();
        await batch.DisposeAsync();
    }
}