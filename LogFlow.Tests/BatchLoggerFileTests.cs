using System.Text.Json;
using LogFlow.Core.Batching;
using LogFlow.Core.Batching.Model;
using LogFlow.Core.Batching.Model.Enums;
using Microsoft.Extensions.Logging;
using Moq;

namespace LogFlow.Tests;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/

[Collection("Non-Parallel BatchLogger File")]
public class BatchLoggerFileSinkTests : IDisposable
{
    private readonly string _root;

    public BatchLoggerFileSinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "logflow-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            { Directory.Delete(_root, recursive: true); }
            catch { /* ignore */ }
        }
    }

    private string FilePath(string name = "app.log") => Path.Combine(_root, name);

    private static BatchLogger MakeFileLogger(string path, BatchFileFormat format, long rollingSize = 0,
        RollingInterval interval = RollingInterval.None, int retain = 3)
    {
        var sink = new Mock<ILogger>();
        sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var opts = new BatchLoggerOptions
        {
            ForwardToILoggerSink = false,
            File = new BatchFileOptions
            {
                Enabled = true,
                Path = path,
                Format = format,
                RollingSizeBytes = rollingSize,
                RollingInterval = interval,
                RetainedFileCountLimit = retain
            },
            // compose file sink
            OnFlushAsync = null
        };

        return new BatchLogger(sink.Object, opts);
    }

    [Fact]
    public async Task Text_WritesOneLinePerEntry_AndCreatesDirectory()
    {
        var path = FilePath("subdir/app.txt");
        await using var logger = MakeFileLogger(path, BatchFileFormat.Text);

        logger.ExLogInformation("hello");
        await logger.FlushAsync();

        Assert.True(File.Exists(path));
        var lines = await File.ReadAllLinesAsync(path);
        Assert.Single(lines);
        Assert.Contains("[Information] hello", lines[0]);
    }

    [Fact]
    public async Task Json_WritesNdjson()
    {
        var path = FilePath("out.jsonl");
        await using var logger = MakeFileLogger(path, BatchFileFormat.Json);

        logger.ExLogError("failure {code}", 500);
        await logger.FlushAsync();

        var line = Assert.Single(await File.ReadAllLinesAsync(path));
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("Error", doc.RootElement.GetProperty("level").GetString());
        Assert.Equal("failure {code}", doc.RootElement.GetProperty("message").GetString());
        Assert.True(doc.RootElement.TryGetProperty("tsUtc", out _));
    }

    [Fact]
    public async Task RollingBySize_Rotates_WithRetention()
    {
        var path = FilePath("roll.log");
        await using var logger = MakeFileLogger(path, BatchFileFormat.Text, rollingSize: 1_000, interval: RollingInterval.None, retain: 2);

        // Write logs in small batches to trigger multiple rolls
        for (var i = 0; i < 5; i++)
        {
            for (var j = 0; j < 50; j++)
            {
                logger.ExLogInformation(new string('x', 40));
            }

            await logger.FlushAsync();
            await Task.Delay(20); // let file system catch up
        }

        // Assert rotations
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.True(File.Exists(path + ".2"));
        Assert.False(File.Exists(path + ".3"));
    }

    [Fact]
    public async Task RollingByWeek_AppendsWeekSuffix()
    {
        var basePath = FilePath("weekly.log");
        await using var logger = MakeFileLogger(basePath, BatchFileFormat.Text, interval: RollingInterval.Week);

        logger.ExLogInformation("hi");
        await logger.FlushAsync();

        var dir = Path.GetDirectoryName(basePath)!;
        var file = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);

        // Find created weekly file "<file>-YYYYMMDD-W<week><ext>"
        var created = Directory.GetFiles(dir, $"{file}-*{ext}").Single();
        Assert.Contains("-W", Path.GetFileNameWithoutExtension(created));
    }
}