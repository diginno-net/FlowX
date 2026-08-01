using FlowX.Postgres;
using Npgsql;

namespace FlowX.JournalBenchmarks;

/// <summary>
/// One migrated schema and the data source over it, created per arm and dropped after.
/// </summary>
/// <remarks>
/// <para>
/// <strong>A schema per arm, for the reason <c>PostgresTestSchema</c> gives and one more.</strong>
/// Arms must not inherit each other's rows: a rehydration arm that swept an earlier arm's
/// unfinished instances would be measuring a population it did not prepare, and a commit arm
/// writing into a table the previous arm already filled would be measuring index depth as
/// well as commit latency. Both are real effects and neither is the one being reported.
/// </para>
/// <para>
/// <strong>The migrator is the shipped one.</strong> The tables B7 writes into and B8 reads
/// back are exactly the tables <c>PostgresMigrator</c> produces for a deployment, including
/// migration 0003's index — which is the index the recovery query in B8's setup depends on.
/// </para>
/// </remarks>
internal sealed class BenchmarkSchema : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly bool _keep;

    private BenchmarkSchema(
        NpgsqlDataSource dataSource,
        string schema,
        string connectionString,
        bool keep)
    {
        DataSource = dataSource;
        Schema = schema;
        _connectionString = connectionString;
        _keep = keep;
    }

    /// <summary>The data source, with <c>search_path</c> already pointing at the schema.</summary>
    public NpgsqlDataSource DataSource { get; }

    /// <summary>Where the tables live.</summary>
    public string Schema { get; }

    /// <summary>Creates a schema, migrates it to the current version, and opens a pool over it.</summary>
    /// <param name="options">The run's parameters.</param>
    /// <param name="suffix">What distinguishes this arm's schema from the others'.</param>
    /// <param name="cancellationToken">Cancels the setup.</param>
    /// <returns>The prepared schema.</returns>
    public static async Task<BenchmarkSchema> CreateAsync(
        BenchmarkOptions options,
        string suffix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var schema = options.Schema + "_" + suffix;

        // The pool ceiling is part of the measurement — a commit waiting for a connection
        // waits inside CommitAsync — so it is set here, recorded in the results, and left to
        // the shipped BuildDataSource to apply along with the search path.
        var connectionString = new NpgsqlConnectionStringBuilder(options.ConnectionString)
        {
            MaxPoolSize = options.MaxPoolSize,
        }.ConnectionString;

        var journalOptions = new PostgresJournalOptions { Schema = schema };
        var dataSource = ServiceCollectionExtensions.BuildDataSource(connectionString, journalOptions);

        _ = await new PostgresMigrator(dataSource, journalOptions)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);

        return new BenchmarkSchema(dataSource, schema, options.ConnectionString, options.KeepSchema);
    }

    /// <summary>Drops the schema and everything in it, unless the run asked to keep it.</summary>
    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync().ConfigureAwait(false);

        if (_keep)
        {
            Console.WriteLine($"    schema {Schema} kept.");

            return;
        }

        await using var dataSource = NpgsqlDataSource.Create(_connectionString);
        await using var command = dataSource.CreateCommand(
            """
            SELECT set_config('flowx.drop_schema', @schema, false);
            DO $$
            BEGIN
                EXECUTE format('DROP SCHEMA IF EXISTS %I CASCADE', current_setting('flowx.drop_schema'));
            END
            $$;
            """);

        _ = command.Parameters.AddWithValue("schema", Schema);

        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
