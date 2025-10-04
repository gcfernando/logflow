using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Exporters.Csv;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Loggers;

namespace LogFlow.BenchMark;

public class Config : ManualConfig
{
    public Config()
    {
        _ = AddLogger(ConsoleLogger.Default);
        _ = AddExporter(MarkdownExporter.Default);
        _ = AddExporter(HtmlExporter.Default);
        _ = AddExporter(CsvExporter.Default);

        _ = AddColumnProvider(DefaultColumnProviders.Statistics);

        _ = AddColumn(StatisticColumn.P95);
        _ = AddColumn(StatisticColumn.P90);
        _ = AddColumn(StatisticColumn.P80);

        _ = AddColumn(TargetMethodColumn.Method, CategoriesColumn.Default);

        _ = WithOption(ConfigOptions.DisableOptimizationsValidator, true);

        _ = AddJob(Job
            .Default
            .WithRuntime(CoreRuntime.Core80)
            .WithGcForce(true)
            .WithGcServer(true)
            .WithWarmupCount(3)
            .WithIterationCount(12)
            .WithMinIterationCount(8)
            .WithMaxIterationCount(16)
        );
    }
}