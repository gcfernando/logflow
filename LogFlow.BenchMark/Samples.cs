namespace LogFlow.BenchMark;

internal static class Samples
{
    public static readonly object[] Args3 = [123, "alpha", true];
    public static readonly Exception ExBasic = new InvalidOperationException("Something went wrong!");

    public static readonly Exception ExDeep = new AggregateException("Top",
    [
        new InvalidOperationException("Inner A"),
        new ApplicationException("Inner B", new NullReferenceException("Inner.B.1"))
    ]);

    public static readonly IDictionary<string, object> SmallContext = new Dictionary<string, object>
    {
        ["RequestId"] = Guid.NewGuid(),
        ["UserId"] = 42,
        ["Region"] = "eu-north-1"
    };
}