using System.Text.Json.Nodes;
using FlowX.JournalBenchmarks;

// The B7 and B8 harness. See docs/benchmarks/B7-B8-journal.md for what it measures, what it
// deliberately does not, and the figures it produced; scripts/run-journal-benchmarks.sh for
// how to run it; scripts/check-journal-benchmarks.py for the verdict.
//
// It is not part of the ordinary test suite and must not become one: it offers thousands of
// transactions a second at a real PostgreSQL and takes minutes. It is gated on
// FLOWX_JOURNAL_BENCH the way tests/FlowX.Chaos is gated on FLOWX_CHAOS and
// tests/FlowX.Postgres.Tests on FLOWX_POSTGRES_CONNECTION — skipping with a reason when
// nobody asked for it, and failing rather than skipping when somebody did and the database
// is not there.
var gate = BenchmarkGate.Check();

if (gate is { } refusal)
{
    await (refusal.Skipped ? Console.Out : Console.Error)
        .WriteLineAsync(refusal.Reason)
        .ConfigureAwait(false);

    return refusal.Skipped ? BenchmarkGate.SkippedExitCode : 1;
}

BenchmarkOptions options;

try
{
    options = BenchmarkOptions.Parse(args, BenchmarkGate.ConnectionString!);
}
catch (ArgumentException failure)
{
    await Console.Error.WriteLineAsync(failure.Message).ConfigureAwait(false);

    return 2;
}

using var lifetime = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    lifetime.Cancel();
};

var startedAt = DateTimeOffset.UtcNow;

try
{
    JsonObject? commit = null;
    JsonObject? rehydration = null;

    if (options.RunCommit)
    {
        // One schema per arm, so no arm inherits another's rows. Created inside the loop
        // rather than here because the arms are independent measurements and a shared table
        // would make the later ones measure index depth as well as commit latency.
        var arms = new JsonArray();

        foreach (var rate in options.Rates)
        {
            await using var schema = await BenchmarkSchema
                .CreateAsync(options, "b7r" + rate, lifetime.Token)
                .ConfigureAwait(false);

            arms.Add(await CommitBenchmark
                .RunAsync(options, schema.DataSource, rate, lifetime.Token)
                .ConfigureAwait(false));
        }

        commit = new JsonObject
        {
            ["budgetArmRate"] = options.BudgetRate,
            ["arms"] = arms,
        };
    }

    if (options.RunRehydration)
    {
        rehydration = await RehydrationBenchmark.RunAsync(options, lifetime.Token)
            .ConfigureAwait(false);
    }

    var document = await Report
        .BuildAsync(options, startedAt, commit, rehydration, lifetime.Token)
        .ConfigureAwait(false);

    await Report.WriteAsync(document, options.JsonPath, lifetime.Token).ConfigureAwait(false);

    Console.WriteLine();
    Console.WriteLine($"Results written to {options.JsonPath}.");

    return 0;
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);

    return 130;
}
catch (Exception failure)
{
    await Console.Error.WriteLineAsync($"The run failed: {failure}").ConfigureAwait(false);

    return 4;
}
