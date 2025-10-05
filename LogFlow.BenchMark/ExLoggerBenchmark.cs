using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using LogFlow.Core.ExLogging;
using Microsoft.Extensions.Logging;

namespace LogFlow.BenchMark;

[CategoriesColumn, GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Orderer(SummaryOrderPolicy.Declared)]
[MemoryDiagnoser(displayGenColumns: true)]
[ThreadingDiagnoser]
[GcServer(true)]
[GcForce(true)]
public class ExLoggerBenchmark
{
    private ILogger _logger = default!;
    private string _msg = default!;

    [GlobalSetup]
    public void Setup()
    {
        _logger = DevNullLogger.Instance;
        _msg = "User {UserId} performed {Action} on {Entity}";
        ExLogger.Log(_logger, LogLevel.Debug, "Warmup");
    }

    [Benchmark(Baseline = true, Description = "LoggerMessage (no args)"), BenchmarkCategory("ExLogger.NoArgs")]
    public void LoggerMessage_NoArgs()
    {
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(1001, "Info"), "{Message}")
            (_logger, "Hello", null);
    }

    [Benchmark(Description = "ExLogger.Log(no args)"), BenchmarkCategory("ExLogger.NoArgs")]
    public void ExLogger_NoArgs() => ExLogger.Log(_logger, LogLevel.Information, "Hello world");

    [Benchmark(Baseline = true, Description = "ILogger.Log with args"), BenchmarkCategory("ExLogger.Structured")]
    public void ILogger_WithArgs()
    {
        _logger.Log(LogLevel.Information, new EventId(12, "Info"), null,
            "User {UserId} performed {Action} on {Entity}", 42, "Create", "Order");
    }

    [Benchmark(Description = "ExLogger.Log<T1,T2>(2 args)"), BenchmarkCategory("ExLogger.Structured")]
    public void ExLogger_2Args_Generic() => ExLogger.Log(_logger, LogLevel.Information, _msg, 42, "Create");

    [Benchmark(Description = "ExLogger.Log(params) 3 args"), BenchmarkCategory("ExLogger.Structured")]
    public void ExLogger_Params3() => ExLogger.Log(_logger, LogLevel.Information, _msg, Samples.Args3);

    [Benchmark(Baseline = true, Description = "ILogger.Log(exception)"), BenchmarkCategory("ExLogger.Exception")]
    public void ILogger_Exception() => _logger.Log(LogLevel.Error, Samples.ExBasic, "Operation failed: {Reason}", "timeout");

    [Benchmark(Description = "ExLogger.Log(exception)"), BenchmarkCategory("ExLogger.Exception")]
    public void ExLogger_Exception() => ExLogger.Log(_logger, LogLevel.Error, "Operation failed: {Reason}", Samples.ExBasic, "timeout");

    [Benchmark(Description = "ExLogger.ExceptionFormatter (basic)"), BenchmarkCategory("ExLogger.Exception")]
    public string ExLoggerExceptionFormattingBasic() => ExLogger.ExceptionFormatter(Samples.ExBasic, "System Error", false);

    [Benchmark(Description = "ExLogger.ExceptionFormatter (deep+details)"), BenchmarkCategory("ExLogger.Exception")]
    public string ExLoggerExceptionFormattingDeep() => ExLogger.ExceptionFormatter(Samples.ExDeep, "Critical System Error", true);

    [Benchmark(Baseline = true, Description = "ILogger.BeginScope(single)"), BenchmarkCategory("ExLogger.Scope")]
    public void ILogger_BeginScope_Single()
    {
        using var _ = _logger.BeginScope(new KeyValuePair<string, object>("RequestId", Guid.NewGuid()));
    }

    [Benchmark(Description = "ExLogger.ExBeginScope(single)"), BenchmarkCategory("ExLogger.Scope")]
    public void ExLogger_BeginScope_Single()
    {
        using var _ = _logger.ExBeginScope("RequestId", Guid.NewGuid());
    }

    [Benchmark(Description = "ExLogger.ExBeginScope(multi <=4)"), BenchmarkCategory("ExLogger.Scope")]
    public void ExLogger_BeginScope_MultiSmall()
    {
        using var _ = _logger.ExBeginScope(Samples.SmallContext);
    }
}