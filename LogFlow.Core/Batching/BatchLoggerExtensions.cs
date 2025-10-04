using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LogFlow.Core.Batching;

/// <summary>
/// Provides extension method for adding <see cref="BatchLogger"/> to the logging pipeline.
/// </summary>
public static class BatchLoggerExtensions
{
    /// <summary>
    /// Adds the <see cref="BatchLogger"/> to the <see cref="ILoggingBuilder"/>.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="configure">Optional configuration delegate for <see cref="BatchLoggerOptions"/>.</param>
    /// <returns>The same <see cref="ILoggingBuilder"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="builder"/> is null.</exception>
    public static ILoggingBuilder AddBatchLogger(this ILoggingBuilder builder, Action<BatchLoggerOptions> configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var opts = new BatchLoggerOptions();
        configure?.Invoke(opts);

        _ = builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new BatchLoggerProvider(sp.GetRequiredService<ILoggerFactory>(), opts));

        return builder;
    }
}