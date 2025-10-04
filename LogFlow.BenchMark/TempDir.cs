namespace LogFlow.BenchMark;

internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "logflow-bench-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(Path);
    }

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        { Directory.Delete(Path, recursive: true); }
        catch { /* ignore */ }
    }
}