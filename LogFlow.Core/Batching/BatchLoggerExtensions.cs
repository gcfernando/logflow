using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/// <summary>
/// Provides extension methods for registering the <see cref="BatchLogger"/> in an application's logging pipeline.
/// </summary>
/// <remarks>
/// These extensions integrate <see cref="BatchLogger"/> into the
/// <see cref="ILoggingBuilder"/> configuration used by ASP.NET Core or generic host applications.
/// You can configure batching behavior either programmatically or via <c>appsettings.json</c>.
/// </remarks>
/// <example>
/// <para><b>Programmatic registration:</b></para>
/// <code language="csharp">
/// builder.Logging.AddBatchLogger(options =>
/// {
///     options.BatchSize = 100;
///     options.FlushInterval = TimeSpan.FromMilliseconds(250);
///     options.File.Enabled = true;
///     options.File.Path = "logs/app.log";
/// });
/// </code>
///
/// <para><b>Configuration-based registration (appsettings.json):</b></para>
/// <code language="json">
/// {
///   "Logging": {
///     "Batch": {
///       "BatchSize": 256,
///       "FlushInterval": "00:00:00.200",
///       "File": {
///         "Enabled": true,
///         "Path": "logs/app.log",
///         "Format": "Json"
///       }
///     }
///   }
/// }
/// </code>
/// <code language="csharp">
/// builder.Logging.AddBatchLogger(builder.Configuration.GetSection("Logging:Batch"));
/// </code>
/// </example>
public static class BatchLoggerExtensions
{
    /// <summary>
    /// Adds a <see cref="BatchLogger"/> to the logging system using a configuration delegate.
    /// </summary>
    /// <param name="builder">The logging builder to which the batch logger will be added.</param>
    /// <param name="configure">An optional delegate used to configure <see cref="BatchLoggerOptions"/>.</param>
    /// <returns>The same <see cref="ILoggingBuilder"/> instance for method chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="builder"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// This overload allows programmatic configuration of the batch logger directly in code.
    /// It registers a singleton <see cref="ILoggerProvider"/> that creates <see cref="BatchLogger"/> instances
    /// with shared <see cref="BatchLoggerOptions"/>.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.AddBatchLogger(o =>
    /// {
    ///     o.Capacity = 10_000;
    ///     o.BatchSize = 100;
    ///     o.File.Enabled = false;
    /// });
    /// </code>
    /// </example>
    public static ILoggingBuilder AddBatchLogger(this ILoggingBuilder builder, Action<BatchLoggerOptions> configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var opts = new BatchLoggerOptions();
        configure?.Invoke(opts);

        _ = builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new BatchLoggerProvider(sp.GetRequiredService<ILoggerFactory>(), opts));

        return builder;
    }

    /// <summary>
    /// Adds a <see cref="BatchLogger"/> to the logging system using configuration binding.
    /// </summary>
    /// <param name="builder">The logging builder to which the batch logger will be added.</param>
    /// <param name="configSection">The configuration section containing <see cref="BatchLoggerOptions"/> values.</param>
    /// <returns>The same <see cref="ILoggingBuilder"/> instance for method chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if either <paramref name="builder"/> or <paramref name="configSection"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// This overload binds <see cref="BatchLoggerOptions"/> from an <see cref="IConfiguration"/> section,
    /// enabling flexible configuration through <c>appsettings.json</c> or environment variables.
    /// <para>
    /// Nested sections for <see cref="BatchFileOptions"/>, <see cref="BatchDatabaseOptions"/>, and
    /// <see cref="BatchFormatOptions"/> are automatically bound when present.
    /// </para>
    /// </remarks>
    /// <example>
    /// Example configuration in <c>appsettings.json</c>:
    /// <code language="json">
    /// {
    ///   "Logging": {
    ///     "Batch": {
    ///       "Capacity": 20000,
    ///       "BatchSize": 256,
    ///       "FlushInterval": "00:00:00.200",
    ///       "ForwardToILoggerSink": true,
    ///       "File": {
    ///         "Enabled": true,
    ///         "Path": "logs/app.log",
    ///         "Format": "Text",
    ///         "RollingInterval": "Day",
    ///         "RollingSizeBytes": 10485760,
    ///         "RetainedFileCountLimit": 7
    ///       },
    ///       "Database": {
    ///         "Enabled": false,
    ///         "ProviderInvariantName": "System.Data.SqlClient",
    ///         "ConnectionString": "...",
    ///         "Table": "Logs",
    ///         "AutoCreateTable": true
    ///       }
    ///     }
    ///   }
    /// }
    /// </code>
    /// <code language="csharp">
    /// builder.AddBatchLogger(builder.Configuration.GetSection("Logging:Batch"));
    /// </code>
    /// </example>
    public static ILoggingBuilder AddBatchLogger(this ILoggingBuilder builder, IConfiguration configSection)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configSection);

        var opts = new BatchLoggerOptions();
        configSection.Bind(opts);

        // Bind nested options if present (in case the default binder doesn't instantiate them)
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
            // Copy bound values
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