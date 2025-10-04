namespace LogFlow.Core.Batching.Model.Enums;

/// <summary>
/// Specifies the output format for batch log files.
/// </summary>
/// <remarks>
/// Determines how log entries are written to the file system when using
/// the <see cref="BatchFileOptions"/> sink. The format affects readability,
/// storage efficiency, and integration with external tools.
/// </remarks>
public enum BatchFileFormat
{
    /// <summary>
    /// Writes logs as plain text lines.
    /// </summary>
    /// <remarks>
    /// Each log entry is rendered as a human-readable string.
    /// This format is easy to inspect manually but less structured
    /// for programmatic parsing.
    /// </remarks>
    Text,

    /// <summary>
    /// Writes logs as JSON objects.
    /// </summary>
    /// <remarks>
    /// Each log entry is serialized as a JSON object on a separate line.
    /// This format is ideal for machine processing, log aggregation,
    /// or ingestion by tools such as Elasticsearch, Loki, or Splunk.
    /// </remarks>
    Json
}