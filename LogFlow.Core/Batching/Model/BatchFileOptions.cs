using System.ComponentModel.DataAnnotations;
using LogFlow.Core.Batching.Model.Enums;

namespace LogFlow.Core.Batching.Model;

/*
 * Developer ::> Gehan Fernando
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

public sealed class BatchFileOptions
{
    public bool Enabled { get; set; }

    [Required]
    public string Path { get; set; } = "logs/log.txt";

    public BatchFileFormat Format { get; set; } = BatchFileFormat.Text;
    public RollingInterval RollingInterval { get; set; } = RollingInterval.Day;
    public long RollingSizeBytes { get; set; }
    public int RetainedFileCountLimit { get; set; } = 10;
}