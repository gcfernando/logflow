using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogFlow.Core.Batching.Model;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

/// <summary>
/// Provides advanced formatting options for serialized or text-based log output.
/// </summary>
/// <remarks>
/// These options control how log entries are formatted before being written
/// to file, database, or any other sink. You can customize JSON serialization
/// or override the text output format entirely.
/// </remarks>
public sealed class BatchFormatOptions
{
    /// <summary>
    /// Gets or sets custom options for JSON serialization.
    /// </summary>
    /// <remarks>
    /// If not specified, default <see cref="JsonSerializerOptions"/> are used.
    /// You can use this property to configure indentation, property naming,
    /// or any <see cref="System.Text.Json"/> behavior.
    /// <para>Example:</para>
    /// <code language="csharp">
    /// options.Format.JsonSerializerOptions = new JsonSerializerOptions
    /// {
    ///     WriteIndented = true,
    ///     PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    /// };
    /// </code>
    /// </remarks>
    [JsonIgnore]
    public JsonSerializerOptions JsonSerializerOptions { get; set; }

    /// <summary>
    /// Gets or sets a custom formatter delegate for rendering text-based log entries.
    /// </summary>
    /// <remarks>
    /// This function is used only when <see cref="BatchFileOptions.Format"/> is
    /// set to <see cref="Enums.BatchFileFormat.Text"/>.
    /// It receives a <see cref="BatchLogEntry"/> and must return a formatted log line.
    /// <para>
    /// If this property is <see langword="null"/>, the default format is used:
    /// <c>{TimestampUtc:O} [Level] Message | args | ex</c>
    /// </para>
    /// <para>Example:</para>
    /// <code language="csharp">
    /// options.Format.TextLineFormatter = entry =>
    ///     $"{DateTime.UtcNow:O} [{entry.Level}] {entry.Message}";
    /// </code>
    /// </remarks>
    [JsonIgnore]
    public Func<BatchLogEntry, string> TextLineFormatter { get; set; }
}