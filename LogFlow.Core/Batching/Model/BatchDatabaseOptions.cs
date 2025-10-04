namespace LogFlow.Core.Batching.Model;

/// <summary>
/// Represents configuration options for writing batched log entries to a database.
/// </summary>
/// <remarks>
/// This class configures the database sink used by <see cref="BatchLogger"/>.
/// It supports any ADO.NET provider via <see cref="System.Data.Common.DbProviderFactories"/>.
/// When enabled, the logger inserts log entries into a specified table using
/// parameterized SQL commands for portability and safety.
/// </remarks>
public sealed class BatchDatabaseOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the database sink is enabled.
    /// </summary>
    /// <remarks>
    /// When set to <see langword="true"/>, <see cref="BatchLogger"/> writes logs
    /// to the configured database table using ADO.NET.
    /// Defaults to <see langword="false"/>.
    /// </remarks>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the invariant name of the database provider.
    /// </summary>
    /// <remarks>
    /// The invariant name identifies the ADO.NET provider used for database access.
    /// Common examples include:
    /// <list type="bullet">
    /// <item><description><c>System.Data.SqlClient</c> — Microsoft SQL Server</description></item>
    /// <item><description><c>Npgsql</c> — PostgreSQL</description></item>
    /// <item><description><c>MySqlConnector</c> — MySQL / MariaDB</description></item>
    /// </list>
    /// </remarks>
    public string ProviderInvariantName { get; set; } = "";

    /// <summary>
    /// Gets or sets the database connection string.
    /// </summary>
    /// <remarks>
    /// Must be valid for the selected provider.
    /// The connection string is used to open a new connection for each batch flush.
    /// </remarks>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// Gets or sets the name of the destination table that receives log entries.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>"Logs"</c>.
    /// The table is created automatically (best-effort) if <see cref="AutoCreateTable"/> is enabled.
    /// </remarks>
    public string Table { get; set; } = "Logs";

    /// <summary>
    /// Gets or sets a value indicating whether to automatically create
    /// the table if it does not already exist.
    /// </summary>
    /// <remarks>
    /// This operation is performed best-effort at startup or first write.
    /// It may fail silently due to insufficient permissions.
    /// </remarks>
    public bool AutoCreateTable { get; set; } = true;

    /// <summary>
    /// Gets or sets the column name used to store the log timestamp (UTC).
    /// </summary>
    public string ColTimestamp { get; set; } = "TimestampUtc";

    /// <summary>
    /// Gets or sets the column name used to store the log level.
    /// </summary>
    public string ColLevel { get; set; } = "Level";

    /// <summary>
    /// Gets or sets the column name used to store the log message text.
    /// </summary>
    public string ColMessage { get; set; } = "Message";

    /// <summary>
    /// Gets or sets the column name used to store exception details, if any.
    /// </summary>
    public string ColException { get; set; } = "Exception";

    /// <summary>
    /// Gets or sets the column name used to store structured argument data
    /// serialized as JSON.
    /// </summary>
    public string ColArgsJson { get; set; } = "ArgsJson";

    /// <summary>
    /// Generates a provider-agnostic SQL statement for creating the log table.
    /// </summary>
    /// <remarks>
    /// This method is used internally when <see cref="AutoCreateTable"/> is <see langword="true"/>.
    /// The statement uses portable SQL syntax with text-based columns for compatibility.
    /// </remarks>
    /// <returns>A SQL <c>CREATE TABLE</c> statement string.</returns>
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

    /// <summary>
    /// Generates a provider-agnostic SQL statement for inserting a single log entry.
    /// </summary>
    /// <remarks>
    /// Used internally during batch flush operations.
    /// Parameters are prefixed with '@' for safe value binding via ADO.NET.
    /// </remarks>
    /// <returns>A SQL <c>INSERT</c> statement string with parameter placeholders.</returns>
    internal string GetInsertSql()
        => $"INSERT INTO {Table} ({ColTimestamp},{ColLevel},{ColMessage},{ColException},{ColArgsJson}) VALUES (@{ColTimestamp},@{ColLevel},@{ColMessage},@{ColException},@{ColArgsJson});";
}