namespace LogFlow.Core.Batching.Model;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / + 46 73 701 40 25
*/
public sealed class BatchDatabaseOptions
{
    public bool Enabled { get; set; }
    public string ProviderInvariantName { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string Table { get; set; } = "Logs";
    public bool AutoCreateTable { get; set; } = true;
    public string ColTimestamp { get; set; } = "TimestampUtc";
    public string ColLevel { get; set; } = "Level";
    public string ColMessage { get; set; } = "Message";
    public string ColException { get; set; } = "Exception";
    public string ColArgsJson { get; set; } = "ArgsJson";
    internal string GetCreateTableSql()
    {
        // Generic SQL; providers may tweak types with implicit conversions.
        // For portability we keep it simple (NVARCHAR / TEXT).
        return $@"
            CREATE TABLE IF NOT EXISTS {Table} (
                {ColTimestamp} TIMESTAMP NOT NULL,
                {ColLevel}     VARCHAR(16) NOT NULL,
                {ColMessage}   TEXT NOT NULL,
                {ColException} TEXT NULL,
                {ColArgsJson}  TEXT NULL
            );";
    }

    internal string GetInsertSql()
        => $"INSERT INTO {Table} ({ColTimestamp},{ColLevel},{ColMessage},{ColException},{ColArgsJson}) VALUES (@{ColTimestamp},@{ColLevel},@{ColMessage},@{ColException},@{ColArgsJson});";
}