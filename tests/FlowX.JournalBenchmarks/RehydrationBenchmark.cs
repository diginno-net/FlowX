using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FlowX.Hosting;
using FlowX.Postgres;
using FlowX.Runtime;

namespace FlowX.JournalBenchmarks;

/// <summary>
/// B8 — reading a flow instance back out of the journal, through the path a recovery sweep
/// takes and no other.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The measured call chain is the shipped one, end to end.</strong>
/// <c>FlowRecoveryScan.RunOnceAsync</c> queries <c>PostgresRecoveryIndex</c>, hands each
/// candidate to <c>FlowHost.ResumeAsync</c>, which acquires a lease and calls
/// <c>DurableLease.ResumeAsync</c> — which is <c>DurableExecution.ResumeAsync</c>, which
/// raises the fence and reads the frontier. Every one of those is production code; the only
/// thing this file adds is a stopwatch inside the journal it hands the host
/// (<see cref="TimingJournal"/>). A harness that read the instance back its own way would be
/// measuring its own SELECT, which is the objection <c>docs/benchmarks/QR2-chaos.md</c> §1
/// makes about every earlier test of this subsystem.
/// </para>
/// <para>
/// <strong>Rehydration is fence plus frontier read, and stops there.</strong> It is not the
/// whole takeover: the lease acquisition before it is the lease store's cost, and the steps
/// that run after it are the flow's. B8's row says "flow instance rehydration from journal",
/// and that is the span from the first journal call of a resume to the last one before the
/// step loop re-enters. The lease acquisition is measured in B7's arms, on the same server,
/// and is quoted there instead of being folded in here.
/// </para>
/// <para>
/// <strong>Why the sweep is over depths.</strong>
/// <c>PostgresFlowJournal.ReadResumeFrontierAsync</c> reads <em>every</em> committed row for
/// the instance — <c>WHERE instance_id = @instance ORDER BY sequence</c>, with no lower bound
/// — so what a rehydration costs depends on how long the instance has been running.
/// <c>flow_instance.state_bag_sequence</c> exists to bound that read and, as ADR-0016 and
/// PLAN.md's WP-53 entry both record, is written on every commit and read by nothing. This
/// sweep is what that standing gap costs, as a number.
/// </para>
/// </remarks>
internal static class RehydrationBenchmark
{
    /// <summary>Runs one arm per committed-step depth and returns the results.</summary>
    /// <param name="options">The run's parameters.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The B8 section of the results document.</returns>
    public static async Task<JsonObject> RunAsync(
        BenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        var arms = new JsonArray();

        foreach (var depth in options.Depths)
        {
            arms.Add(await RunArmAsync(options, depth, cancellationToken).ConfigureAwait(false));
        }

        return new JsonObject
        {
            ["budgetDepth"] = options.BudgetDepth,
            ["arms"] = arms,
        };
    }

    private static async Task<JsonObject> RunArmAsync(
        BenchmarkOptions options,
        int depth,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"==> B8: {options.Instances} instance(s) carrying {depth} committed step(s), " +
            $"after {options.WarmupInstances} discarded");

        await using var schema = await BenchmarkSchema
            .CreateAsync(options, "b8d" + depth.ToString(CultureInfo.InvariantCulture), cancellationToken)
            .ConfigureAwait(false);

        // The plan has one more step than the instance has committed, so a resumed instance
        // has exactly one step left to run. Enough for the resume to be a real execution and
        // not enough for the flow's own work to be what the arm is timing.
        var plan = BenchmarkDispatcher.Plan(depth + 1);
        var prepared = await PrepareAsync(
                options,
                schema,
                plan,
                depth,
                options.Instances + options.WarmupInstances,
                cancellationToken)
            .ConfigureAwait(false);

        // Only now is the journal wrapped: the setup writes above are the arm's arrangement,
        // not its measurement, and timing them would put a few thousand commits into a
        // rehydration sample.
        var journal = new TimingJournal(new PostgresFlowJournal(schema.DataSource));

        var durability = new FlowDurability(
            journal,
            new PostgresLeaseStore(schema.DataSource),
            new PostgresRecoveryIndex(schema.DataSource));

        var hostOptions = new FlowXOptions
        {
            ApplicationName = "FlowX.JournalBenchmarks",
            NodeName = options.NodeName,
            LeaseTtl = options.LeaseTtl,
            LeaseRenewalInterval = TimeSpan.FromSeconds(
                Math.Max(1, options.LeaseTtl.TotalSeconds / 3)),
            RecoveryScanBatchSize = Math.Max(64, options.RecoveryConcurrency * 4),
            MaxConcurrentRecoveries = options.RecoveryConcurrency,
            ShutdownDrainTimeout = TimeSpan.FromSeconds(30),
        };

        var host = new FlowHost(new FlowEngine(SystemClock.Instance), hostOptions, durability);
        var catalog = new FlowCatalog().Add(plan, new BenchmarkDispatcher(options.PayloadBytes));
        var scan = new FlowRecoveryScan(host, catalog, durability, hostOptions, SystemClock.Instance);

        if (!scan.IsEnabled)
        {
            throw new InvalidOperationException(
                "The recovery scan is disabled, so this arm would sweep for nothing and " +
                "report a rehydration latency for zero rehydrations.");
        }

        var started = Stopwatch.GetTimestamp();
        var resumed = 0;
        var contended = 0;
        var failed = 0;
        var sweeps = 0;
        var discarded = 0;
        var warmedUp = options.WarmupInstances == 0;

        while (resumed < prepared && !cancellationToken.IsCancellationRequested)
        {
            if (!warmedUp && resumed >= options.WarmupInstances)
            {
                // Everything so far priced a cold pool, a cold plan and a process that had
                // never rehydrated anything. Discarded rather than averaged in: the first
                // pilot of this arm reported a p99 four times the steady state's, and the
                // whole of the difference was its first sweep.
                journal.Reset();
                discarded = resumed;
                warmedUp = true;
            }

            var report = await scan.RunOnceAsync(cancellationToken).ConfigureAwait(false);

            sweeps++;
            resumed += report.Resumed;
            contended += report.Contended;
            failed += report.Failed;

            if (report.Error is not null)
            {
                await Console.Error
                    .WriteLineAsync($"    sweep failed — {report.Error}")
                    .ConfigureAwait(false);

                break;
            }

            if (report.Resumed == 0 && report.Contended == 0)
            {
                // Nothing left that this node can take. Reported rather than retried: a loop
                // that kept sweeping an empty candidate set would turn a short population into
                // an infinite arm.
                break;
            }
        }

        var elapsed = Stopwatch.GetElapsedTime(started);

        var unfinished = await UnfinishedAsync(schema, cancellationToken).ConfigureAwait(false);

        var result = new JsonObject
        {
            ["committedSteps"] = depth,
            ["prepared"] = prepared,
            ["warmupInstances"] = discarded,
            ["instances"] = prepared - discarded,
            ["resumed"] = resumed,
            ["contended"] = contended,
            ["failed"] = failed,
            ["instancesLeftUnfinished"] = unfinished,
            ["sweeps"] = sweeps,
            ["elapsedSeconds"] = Math.Round(elapsed.TotalSeconds, 3, MidpointRounding.AwayFromZero),
            ["rehydrationSamples"] = journal.Rehydration.Count,
            ["rehydrationMs"] = journal.Rehydration.ToJson(),
            ["fenceMs"] = journal.Fence.ToJson(),
            ["frontierReadMs"] = journal.Frontier.ToJson(),
            ["resumedStepCommitMs"] = journal.Commit.ToJson(),
            ["refusals"] = Refusals(journal),
        };

        Print(result);

        return result;
    }

    /// <summary>
    /// Leaves <paramref name="depth"/> committed steps on each of the arm's instances, and
    /// leaves every one of them exactly as a node that died would have.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every row is written by the shipped adapter — a lease acquisition, a
    /// <c>StartAsync</c>, then <paramref name="depth"/> <c>CommitAsync</c> calls — so nothing
    /// measured afterwards can pass against a row shape <c>PostgresFlowJournal</c> does not
    /// produce.
    /// </para>
    /// <para>
    /// The one thing raw SQL does is move <c>updated_at</c> into the past, because staleness
    /// is the premise of the recovery query and the alternative is an arm that sleeps for a
    /// lease TTL. <c>PostgresTestSchema.AbandonAsync</c> takes the same exception for the same
    /// reason.
    /// </para>
    /// </remarks>
    private static async Task<int> PrepareAsync(
        BenchmarkOptions options,
        BenchmarkSchema schema,
        ExecutionPlan plan,
        int depth,
        int wanted,
        CancellationToken cancellationToken)
    {
        var journal = new PostgresFlowJournal(schema.DataSource);
        var leases = new PostgresLeaseStore(schema.DataSource);
        var payload = new BenchmarkDispatcher(options.PayloadBytes);
        var opened = 0;

        var lanes = new Task[options.SetupConcurrency];
        var next = 0;

        for (var lane = 0; lane < lanes.Length; lane++)
        {
            lanes[lane] = Task.Run(
                async () =>
                {
                    while (Interlocked.Increment(ref next) <= wanted)
                    {
                        if (await OpenOneAsync().ConfigureAwait(false))
                        {
                            _ = Interlocked.Increment(ref opened);
                        }
                    }
                },
                cancellationToken);
        }

        await Task.WhenAll(lanes).ConfigureAwait(false);

        // A lease TTL into the past, and then some: the sweep's candidate filter is
        // `updated_at < now - LeaseTtl`, and an hour is far enough that no arm's duration can
        // move an instance back out of the candidate set.
        await using (var command = schema.DataSource.CreateCommand(
            "UPDATE flow_instance SET updated_at = now() - interval '1 hour'"))
        {
            _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        Console.WriteLine(
            $"    prepared {opened} instance(s) x {depth} committed step(s) " +
            $"= {opened * depth} journal rows");

        return opened;

        async Task<bool> OpenOneAsync()
        {
            var instance = Guid.CreateVersion7();

            var acquired = await DurableLease
                .AcquireAsync(
                    leases,
                    instance,
                    "dead-node",
                    new LeasePolicy
                    {
                        // The run's own policy. The lease is released on the way out of this
                        // method, so nothing here waits for a TTL and nothing renews — what
                        // stands in for the dead node is the released lease plus the
                        // updated_at below, exactly as PostgresTestSchema.AbandonAsync
                        // arranges it.
                        Ttl = options.LeaseTtl,
                        RenewalInterval = TimeSpan.FromSeconds(
                            Math.Max(1, options.LeaseTtl.TotalSeconds / 3)),
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (acquired.IsFailure)
            {
                return false;
            }

            var lease = acquired.Value;

            try
            {
                var invocation = new FlowInvocation(
                    "bench-" + instance.ToString("n", CultureInfo.InvariantCulture),
                    instance.ToString("d", CultureInfo.InvariantCulture),
                    TenantId: null,
                    Deadline: DateTimeOffset.UtcNow + plan.Flow.Deadline);

                var begun = await lease
                    .BeginAsync(journal, plan, invocation, input: null, cancellationToken)
                    .ConfigureAwait(false);

                if (begun.IsFailure)
                {
                    return false;
                }

                for (var step = 0; step < depth; step++)
                {
                    var committed = await journal.CommitAsync(
                        new StepCommit
                        {
                            Key = StepKey.First(instance, step),
                            Token = lease.Token,
                            CapabilityId = "journal.step",
                            CapabilityVersion = "1.0.0",
                            Outcome = JournalOutcome.Success,
                            Result = payload.Result,
                            StateBag = payload.StateBag,
                            State = FlowInstanceState.Running,
                            ResumeHint = step,
                        },
                        cancellationToken).ConfigureAwait(false);

                    if (committed.IsFailure)
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                // The lease goes; the instance does not. A crash reaches the same place a TTL
                // later, and this arm is not about how long a TTL is.
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// How many of the arm's instances are still in a non-terminal state after the sweeps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The guard against the arm grading its own homework, and it caught something on
    /// its first run.</strong> <c>FlowRecoveryScan.TakeOverAsync</c> classifies a takeover by
    /// whether this node became the writer, not by how the resumed flow ended — an error code
    /// it does not recognise counts as <c>Resumed</c>, which is deliberate and documented, and
    /// it means <c>report.Resumed</c> cannot be read as "the instance finished". The first
    /// pilot of this harness reported 100 of 100 resumed while every one of them had failed at
    /// <c>RestoreState</c>, run no step and stayed <c>Running</c>. This query is what says so,
    /// and the checker refuses a run that leaves any instance behind.
    /// </para>
    /// <para>
    /// The state list is <c>PostgresRecoveryIndex</c>'s own — a sweep looks for
    /// <c>Pending</c>, <c>Running</c> and <c>Compensating</c> — so what this counts is exactly
    /// what a second sweep would find.
    /// </para>
    /// </remarks>
    private static async Task<long> UnfinishedAsync(
        BenchmarkSchema schema,
        CancellationToken cancellationToken)
    {
        await using var command = schema.DataSource.CreateCommand(
            "SELECT count(*) FROM flow_instance WHERE state IN ('Pending', 'Running', 'Compensating')");

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static JsonObject Refusals(TimingJournal journal)
    {
        var refusals = new JsonObject();

        foreach (var (code, count) in journal.Refusals.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            refusals[code] = count;
        }

        return refusals;
    }

    private static void Print(JsonObject result)
    {
        var rehydration = result["rehydrationMs"];

        if (rehydration is null)
        {
            Console.WriteLine("    nothing was rehydrated.");

            return;
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"    rehydrate p50 {rehydration["p50"]!.GetValue<double>():F3} " +
                $"p95 {rehydration["p95"]!.GetValue<double>():F3} " +
                $"p99 {rehydration["p99"]!.GetValue<double>():F3} " +
                $"max {rehydration["max"]!.GetValue<double>():F3} ms | " +
                $"{result["rehydrationSamples"]} sample(s) of {result["prepared"]} prepared " +
                $"in {result["sweeps"]} sweep(s), " +
                $"{result["instancesLeftUnfinished"]} left unfinished"));
    }
}
