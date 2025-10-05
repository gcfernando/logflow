using System.Threading.Channels;

namespace LogFlow.Core.Batching.Model;

/*
 * Developer ::> Gehan Fernando
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

public sealed class BatchLoggerOptions
{
    public int Capacity { get; set; } = 10_000;
    public int BatchSize { get; set; } = 100;
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(200);

    public Func<Microsoft.Extensions.Logging.LogLevel, string, Exception, object[], bool> Filter { get; set; }
        = static (_, _, _, _) => true;

    public Func<IReadOnlyList<BatchLogEntry>, CancellationToken, Task> OnFlushAsync { get; set; }
    public Action<string, Exception> OnInternalError { get; set; }
    public bool ForwardToILoggerSink { get; set; } = true;
    public BatchFileOptions File { get; set; } = new();
    public BatchDatabaseOptions Database { get; set; } = new();
    public BatchFormatOptions Format { get; set; } = new();
    public BoundedChannelFullMode ChannelFullMode { get; set; } = BoundedChannelFullMode.DropOldest;
}