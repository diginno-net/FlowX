using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;

namespace FlowX.JournalBenchmarks;

/// <summary>
/// The results document: the budgets, the configuration they were measured under, the
/// machine, and the arms.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The budgets are written into the document rather than only into the checker.</strong>
/// <c>scripts/check-journal-benchmarks.py</c> reads them from here and can override them from
/// the command line, so a committed result carries the figures it was judged against and a
/// later reader does not have to trust that the budget was the same one. If
/// <c>docs/14-Performance.md</c> ever moves a budget, an old document still says what it was
/// held to.
/// </para>
/// <para>
/// <strong>The machine is recorded because two of these numbers are properties of it.</strong>
/// <c>docs/benchmarks/README.md</c> §5 has two runs of an identical commit on this container
/// disagreeing by 159 % on absolute time. A latency measured here without the core count, the
/// load average and PostgreSQL's own durability settings beside it is not a measurement
/// anybody can repeat or discount.
/// </para>
/// </remarks>
internal static class Report
{
    /// <summary>The budgets this run is judged against, from docs/14-Performance.md §1.1.</summary>
    public const double CommitBudgetMs = 15.0;

    /// <summary>B7's other half: the rate the commit budget is stated at.</summary>
    public const int CommitBudgetRate = 5000;

    /// <summary>B8's budget.</summary>
    public const double RehydrationBudgetMs = 8.0;

    /// <summary>Builds the document.</summary>
    /// <param name="options">The run's parameters.</param>
    /// <param name="startedAt">When the run began.</param>
    /// <param name="commit">The B7 section, or null when the run skipped it.</param>
    /// <param name="rehydration">The B8 section, or null when the run skipped it.</param>
    /// <param name="cancellationToken">Cancels the server-version query.</param>
    /// <returns>The document.</returns>
    public static async Task<JsonObject> BuildAsync(
        BenchmarkOptions options,
        DateTimeOffset startedAt,
        JsonObject? commit,
        JsonObject? rehydration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var document = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["recordedAt"] = startedAt.ToString("O", CultureInfo.InvariantCulture),
            ["elapsedSeconds"] = Math.Round(
                (DateTimeOffset.UtcNow - startedAt).TotalSeconds, 1, MidpointRounding.AwayFromZero),
            ["budgets"] = new JsonObject
            {
                ["b7"] = new JsonObject
                {
                    ["p99Ms"] = CommitBudgetMs,
                    ["commitsPerSecond"] = CommitBudgetRate,
                    ["source"] = "docs/14-Performance.md §1.1",
                },
                ["b8"] = new JsonObject
                {
                    ["p99Ms"] = RehydrationBudgetMs,
                    ["source"] = "docs/14-Performance.md §1.1",
                },
            },
            ["parameters"] = new JsonObject
            {
                ["rates"] = new JsonArray([.. options.Rates.Select(rate => (JsonNode)rate)]),
                ["steps"] = options.Steps,
                ["payloadBytes"] = options.PayloadBytes,
                ["measureSeconds"] = (int)options.Measure.TotalSeconds,
                ["warmupSeconds"] = (int)options.Warmup.TotalSeconds,
                ["maxInFlight"] = options.MaxInFlight,
                ["maxPoolSize"] = options.MaxPoolSize,
                ["leaseTtlSeconds"] = (int)options.LeaseTtl.TotalSeconds,
                ["instances"] = options.Instances,
                ["depths"] = new JsonArray([.. options.Depths.Select(depth => (JsonNode)depth)]),
                ["recoveryConcurrency"] = options.RecoveryConcurrency,
            },
            ["machine"] = await MachineAsync(options, cancellationToken).ConfigureAwait(false),
        };

        if (commit is not null)
        {
            document["b7"] = commit;
        }

        if (rehydration is not null)
        {
            document["b8"] = rehydration;
        }

        return document;
    }

    /// <summary>Writes the document where the run was told to put it.</summary>
    /// <param name="document">The document.</param>
    /// <param name="path">Where it goes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the file is on disk.</returns>
    public static async Task WriteAsync(
        JsonObject document,
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        var json = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonObject> MachineAsync(
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var machine = new JsonObject
        {
            ["processorCount"] = Environment.ProcessorCount,
            ["loadAverage"] = LoadAverage(),
            ["runtime"] = Environment.Version.ToString(),
            ["serverGarbageCollection"] = System.Runtime.GCSettings.IsServerGC,
        };

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(options.ConnectionString);

            machine["postgres"] = await ScalarAsync(dataSource, "SELECT version()", cancellationToken)
                .ConfigureAwait(false);

            // The two settings that decide what a commit costs. A p99 measured with
            // synchronous_commit off is a measurement of a different guarantee, and quoting
            // one against B7 without saying so would be quoting the wrong number.
            machine["synchronousCommit"] =
                await ScalarAsync(dataSource, "SHOW synchronous_commit", cancellationToken)
                    .ConfigureAwait(false);
            machine["fsync"] =
                await ScalarAsync(dataSource, "SHOW fsync", cancellationToken).ConfigureAwait(false);
            machine["walSyncMethod"] =
                await ScalarAsync(dataSource, "SHOW wal_sync_method", cancellationToken)
                    .ConfigureAwait(false);
            machine["maxConnections"] =
                await ScalarAsync(dataSource, "SHOW max_connections", cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is NpgsqlException or ArgumentException)
        {
            machine["postgres"] = "unknown: " + failure.Message;
        }

        return machine;
    }

    private static async Task<string> ScalarAsync(
        NpgsqlDataSource dataSource,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value?.ToString() ?? "unknown";
    }

    /// <summary>
    /// The one-minute load average, read as the report is written.
    /// </summary>
    /// <remarks>
    /// The load at the end of the run rather than an average over it, which is the same
    /// caveat <c>docs/benchmarks/QR2-chaos.md</c> §4.4 records about its own figure. It is
    /// here because on a shared four-core container the other tenants are a term in every
    /// wall-clock number this document contains.
    /// </remarks>
    private static double LoadAverage()
    {
        try
        {
            var parts = File.ReadAllText("/proc/loadavg").Split(' ');

            return double.TryParse(parts[0], CultureInfo.InvariantCulture, out var value)
                ? value
                : -1;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }
}
