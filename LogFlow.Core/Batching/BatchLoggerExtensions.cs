using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/*
 * Developer ::> Gehan Fernando
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

public static class BatchLoggerExtensions
{
    public static ILoggingBuilder AddBatchLogger(this ILoggingBuilder builder, Action<BatchLoggerOptions> configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var opts = new BatchLoggerOptions();
        configure?.Invoke(opts);

        _ = builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new BatchLoggerProvider(sp.GetRequiredService<ILoggerFactory>(), opts));

        return builder;
    }

    public static ILoggingBuilder AddBatchLogger(this ILoggingBuilder builder, IConfiguration configSection)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configSection);

        var opts = new BatchLoggerOptions();
        configSection.Bind(opts);

        var fileSection = configSection.GetSection("File");
        if (fileSection.Exists())
        {
            opts.File ??= new BatchFileOptions();
            fileSection.Bind(opts.File);
        }

        var dbSection = configSection.GetSection("Database");
        if (dbSection.Exists())
        {
            opts.Database ??= new BatchDatabaseOptions();
            dbSection.Bind(opts.Database);
        }

        var fmtSection = configSection.GetSection("Format");
        if (fmtSection.Exists())
        {
            opts.Format ??= new BatchFormatOptions();
            fmtSection.Bind(opts.Format);
        }

        return builder.AddBatchLogger(o =>
        {
            o.Capacity = opts.Capacity;
            o.BatchSize = opts.BatchSize;
            o.FlushInterval = opts.FlushInterval;
            o.Filter = opts.Filter;
            o.OnFlushAsync = opts.OnFlushAsync;
            o.OnInternalError = opts.OnInternalError;
            o.ForwardToILoggerSink = opts.ForwardToILoggerSink;
            o.File = opts.File;
            o.Database = opts.Database;
            o.Format = opts.Format;
        });
    }
}