using System.ComponentModel.DataAnnotations;
using LogFlow.Core.Batching.Model.Enums;

namespace LogFlow.Core.Batching.Model;

/// <summary>
/// Represents configuration options for writing batched log entries to rolling log files.
/// </summary>
/// <remarks>
/// This class defines the file sink settings used by <see cref="BatchLogger"/>.
/// It supports plain text or JSON log formats and provides both time-based and size-based
/// rolling (rotation) to manage file growth automatically.
/// </remarks>
public sealed class BatchFileOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the file sink is enabled.
    /// </summary>
    /// <remarks>
    /// When set to <see langword="true"/>, <see cref="BatchLogger"/> writes
    /// log batches to a file according to the configured options.
    /// Defaults to <see langword="false"/>.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the file path (directory and filename) where log entries are written.
    /// </summary>
    /// <remarks>
    /// If the directory does not exist, it will be created automatically.
    /// The path can include relative or absolute locations.
    /// Example: <c>"logs/app.log"</c>
    /// </remarks>
    [Required]
    public string Path { get; set; } = "logs/log.txt";

    /// <summary>
    /// Gets or sets the output format used when writing log entries to the file.
    /// </summary>
    /// <remarks>
    /// Supports two formats:
    /// <list type="bullet">
    /// <item><description><see cref="BatchFileFormat.Text"/> — Human-readable plain text lines.</description></item>
    /// <item><description><see cref="BatchFileFormat.Json"/> — One JSON object per log entry, machine-readable.</description></item>
    /// </list>
    /// </remarks>
    public BatchFileFormat Format { get; set; } = BatchFileFormat.Text;

    /// <summary>
    /// Gets or sets the interval at which the log file rolls over (time-based rolling).
    /// </summary>
    /// <remarks>
    /// Determines how often a new file is created based on time.
    /// Common values include:
    /// <list type="bullet">
    /// <item><description><see cref="RollingInterval.None"/> — No time-based rolling.</description></item>
    /// <item><description><see cref="RollingInterval.Hour"/> — One file per hour.</description></item>
    /// <item><description><see cref="RollingInterval.Day"/> — One file per day (default).</description></item>
    /// <item><description><see cref="RollingInterval.Month"/> — One file per month.</description></item>
    /// </list>
    /// </remarks>
    public RollingInterval RollingInterval { get; set; } = RollingInterval.Day;

    /// <summary>
    /// Gets or sets the maximum file size in bytes before a size-based roll occurs.
    /// </summary>
    /// <remarks>
    /// When the current log file exceeds this size limit, it is renamed and a new file is created.
    /// A value of <c>0</c> disables size-based rolling.
    /// </remarks>
    public long RollingSizeBytes { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of retained rolled files.
    /// </summary>
    /// <remarks>
    /// Determines how many previously rolled files (e.g., <c>.1</c>, <c>.2</c>, etc.) are kept.
    /// When the limit is reached, the oldest rolled file is deleted.
    /// Applies only to size-based rolling.
    /// </remarks>
    public int RetainedFileCountLimit { get; set; } = 10;
}