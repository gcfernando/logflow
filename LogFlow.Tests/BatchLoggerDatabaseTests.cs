using System.Data;
using System.Data.Common;
using LogFlow.Core.Batching;
using LogFlow.Core.Batching.Model;
using Microsoft.Extensions.Logging;
using Moq;

namespace LogFlow.Tests;

/*
 * Developer ::> Gehan Fernando 
 * Date      ::> 2025-10-01
 * Contact   ::> f.gehan@gmail.com / +46 73 701 40 25
*/

[Collection("Non-Parallel BatchLogger DB")]
public class BatchLogger_DatabaseSinkTests
{
    // --- Minimal in-memory fake provider ---

    private sealed class FakeDbFactory : DbProviderFactory
    {
        public readonly List<FakeConnection> Connections = [];
        public override DbConnection CreateConnection() => new FakeConnection(this);
    }

    private sealed class FakeConnection : DbConnection
    {
        private readonly FakeDbFactory _factory;
        public bool Opened { get; private set; }
        public readonly List<FakeCommand> Commands = [];

        public FakeConnection(FakeDbFactory factory) => _factory = factory;

        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => Opened ? ConnectionState.Open : ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => Opened = false;
        public override void Open() => Opened = true;
        public override Task OpenAsync(CancellationToken cancellationToken) { Opened = true; return Task.CompletedTask; }

        // ✅ Added implementation required by abstract DbConnection
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => new FakeTransaction(this);

        protected override DbCommand CreateDbCommand()
        {
            var cmd = new FakeCommand(this);
            Commands.Add(cmd);
            return cmd;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            _factory.Connections.Add(this);
        }
    }

    private sealed class FakeTransaction : DbTransaction
    {
        public FakeTransaction(DbConnection conn) => DbConnection = conn;

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection { get; }
        public override void Commit() { }
        public override void Rollback() { }
    }

    private sealed class FakeCommand : DbCommand
    {
        public FakeCommand(FakeConnection conn) => DbConnection = conn;

        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }

        // ✅ Implemented full getter/setter for DbConnection
        protected override DbConnection DbConnection { get; set; }

        protected override DbParameterCollection DbParameterCollection { get; } = new FakeParamCollection();
        protected override DbTransaction DbTransaction { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }

        public override void Cancel() { }
        protected override DbParameter CreateDbParameter() => new FakeParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotImplementedException();
        public override int ExecuteNonQuery() => 1;
        public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => Task.FromResult(1);
        public override object ExecuteScalar() => 1;
        public override Task<object> ExecuteScalarAsync(CancellationToken cancellationToken) => Task.FromResult<object>(1);
        public override void Prepare() { }
    }

    private sealed class FakeParameter : DbParameter
    {
        public override DbType DbType { get; set; }
        public override ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        public override string ParameterName { get; set; } = string.Empty;
        public override string SourceColumn { get; set; } = string.Empty;
        public override object Value { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override int Size { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class FakeParamCollection : DbParameterCollection
    {
        private readonly List<DbParameter> _list = [];
        public override int Add(object value) { _list.Add((DbParameter)value); return _list.Count - 1; }
        public override void AddRange(Array values)
        {
            foreach (var v in values)
            {
                _ = Add(v);
            }
        }
        public override void Clear() => _list.Clear();
        public override bool Contains(object value) => _list.Contains((DbParameter)value);
        public override bool Contains(string value) => _list.Any(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => _list.ToArray().CopyTo(array, index);
        public override int Count => _list.Count;
        public override System.Collections.IEnumerator GetEnumerator() => _list.GetEnumerator();
        protected override DbParameter GetParameter(int index) => _list[index];
        protected override DbParameter GetParameter(string parameterName) => _list.FirstOrDefault(p => p.ParameterName == parameterName);
        public override int IndexOf(object value) => _list.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => _list.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => _list.Insert(index, (DbParameter)value);
        public override bool IsFixedSize => false;
        public override bool IsReadOnly => false;
        public override bool IsSynchronized => false;
        public override void Remove(object value) => _list.Remove((DbParameter)value);
        public override void RemoveAt(int index) => _list.RemoveAt(index);
        public override void RemoveAt(string parameterName)
        {
            var i = IndexOf(parameterName);
            if (i >= 0)
            {
                _list.RemoveAt(i);
            }
        }
        protected override void SetParameter(int index, DbParameter value) => _list[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value)
        {
            var i = IndexOf(parameterName);
            if (i >= 0)
            {
                _list[i] = value;
            }
            else
            {
                _ = Add(value);
            }
        }
        public override object SyncRoot => this;
    }

    private const string _providerName = "LogFlow.Tests.FakeDb";

    static BatchLogger_DatabaseSinkTests() =>
        // Register our fake factory once
        DbProviderFactories.RegisterFactory(_providerName, new FakeDbFactory());

    private static BatchLogger MakeDbLogger(out FakeDbFactory factory)
    {
        factory = (FakeDbFactory)DbProviderFactories.GetFactory(_providerName);

        var sink = new Mock<ILogger>();
        _ = sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var opts = new BatchLoggerOptions
        {
            ForwardToILoggerSink = false,
            Database = new BatchDatabaseOptions
            {
                Enabled = true,
                ProviderInvariantName = _providerName,
                ConnectionString = "ignored",
                AutoCreateTable = true,
                Table = "Logs"
            }
        };

        return new BatchLogger(sink.Object, opts);
    }

    [Fact]
    public async Task Writes_CreateTable_And_Inserts()
    {
        await using var logger = MakeDbLogger(out var factory);

        logger.ExLogInformation("hi");
        logger.ExLogError("oops", new InvalidOperationException("boom"));
        await logger.FlushAsync();

        // One connection created, several commands executed
        Assert.NotEmpty(factory.Connections);
        var conn = factory.Connections[^1];
        Assert.True(conn.Opened); // got opened at some point

        // Expect at least one CREATE TABLE and two INSERTS
        var createCmd = conn.Commands.FirstOrDefault(c => (c.CommandText ?? "").Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(createCmd);

        var inserts = conn.Commands.Count(c => (c.CommandText ?? "").StartsWith("INSERT", StringComparison.OrdinalIgnoreCase));
        Assert.True(inserts >= 2);
    }

    [Fact]
    public async Task DbWrite_RespectsCancellation()
    {
        await using var logger = MakeDbLogger(out _);

        logger.ExLogInformation("x");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(() => logger.FlushAsync(cts.Token));
    }

    [Fact]
    public async Task Skips_TableCreation_When_AutoCreateTable_Disabled()
    {
        var sink = new Mock<ILogger>();
        _ = sink.Setup(l => l.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        var opts = new BatchLoggerOptions
        {
            ForwardToILoggerSink = false,
            Database = new BatchDatabaseOptions
            {
                Enabled = true,
                ProviderInvariantName = _providerName,
                ConnectionString = "ignored",
                AutoCreateTable = false
            }
        };

        await using var logger = new BatchLogger(sink.Object, opts);
        logger.ExLogInformation("hi");
        await logger.FlushAsync();

        // Should still insert, but without CREATE TABLE
        var factory = (FakeDbFactory)DbProviderFactories.GetFactory(_providerName);
        var conn = factory.Connections[^1];
        Assert.DoesNotContain(conn.Commands, c => (c.CommandText ?? "").Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase));
    }
}
