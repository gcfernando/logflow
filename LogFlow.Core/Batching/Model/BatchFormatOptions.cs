using System.Text.Json;
using System.Text.Json.Serialization;
using LogFlow.Core.Batching.Model.Enums;

namespace LogFlow.Core.Batching.Model;

/*
 * Developer ::> Gehan Fernando
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

public sealed class BatchFormatOptions
{
    public BatchFileFormat Format { get; set; } = BatchFileFormat.Text;
    public bool UsePerEntryTimestamp { get; set; }

    [JsonIgnore]
    public JsonSerializerOptions JsonSerializerOptions { get; set; }

    [JsonIgnore]
    public Func<BatchLogEntry, string> TextLineFormatter { get; set; }
}