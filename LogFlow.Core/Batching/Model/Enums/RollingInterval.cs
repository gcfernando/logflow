namespace LogFlow.Core.Batching.Model.Enums;

/// <summary>
/// Specifies the time-based rolling policy for batch log files.
/// </summary>
/// <remarks>
/// Determines how often the log file is rolled (archived and a new one created)
/// when using the <see cref="BatchFileOptions"/> file sink.
/// Rolling helps manage file size, organize logs by time periods,
/// and prevent unlimited file growth.
/// </remarks>
public enum RollingInterval
{
    /// <summary>
    /// No time-based rolling is performed.
    /// </summary>
    /// <remarks>
    /// All log entries are written to a single file.
    /// This is suitable for short-lived applications or when rolling
    /// is managed externally (e.g., by a log rotation tool).
    /// </remarks>
    None,

    /// <summary>
    /// Creates a new log file every hour.
    /// </summary>
    /// <remarks>
    /// Useful for high-volume logging scenarios where logs grow rapidly
    /// and you want fine-grained hourly files for analysis or cleanup.
    /// </remarks>
    Hour,

    /// <summary>
    /// Creates a new log file every day.
    /// </summary>
    /// <remarks>
    /// The most common rolling interval.
    /// Generates one log file per calendar day, named by date (e.g., app-20251004.log).
    /// </remarks>
    Day,

    /// <summary>
    /// Creates a new log file every week.
    /// </summary>
    /// <remarks>
    /// Each week’s logs are stored in a single file, typically starting on Monday.
    /// Useful for moderate logging volumes or weekly audit analysis.
    /// </remarks>
    Week,

    /// <summary>
    /// Creates a new log file every month.
    /// </summary>
    /// <remarks>
    /// Suitable for low-volume or long-running services where logs
    /// are compact and can be rotated monthly (e.g., app-202510.log).
    /// </remarks>
    Month,

    /// <summary>
    /// Creates a new log file every calendar year.
    /// </summary>
    /// <remarks>
    /// Used for archival or compliance scenarios where yearly log files
    /// are required. Typically combined with external retention policies.
    /// </remarks>
    Year
}