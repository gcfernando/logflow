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
        AddLogger(ConsoleLogger.Default);
        AddExporter(MarkdownExporter.Default);
        AddExporter(HtmlExporter.Default);
        AddExporter(CsvExporter.Default);

        AddColumnProvider(DefaultColumnProviders.Statistics);

        AddColumn(StatisticColumn.P95);
        AddColumn(StatisticColumn.P90);
        AddColumn(StatisticColumn.P80);

        AddColumn(TargetMethodColumn.Method, CategoriesColumn.Default);

        WithOption(ConfigOptions.DisableOptimizationsValidator, true);

        AddJob(Job
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