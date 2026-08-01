using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using FlowX.Postgres;
using FlowX.Runtime;
using Npgsql;

namespace FlowX.JournalBenchmarks;

/// <summary>
/// B7 — the durable step commit, measured at an offered rate rather than at whatever rate
/// the machine happens to produce.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Open loop, because B7's budget is a latency at a load.</strong> A closed-loop
/// generator — start a flow, wait for it, start the next — offers less load exactly when the
/// system is slower, so its "5 000 commits/s" is a result rather than an input and its tail
/// is missing the requests that were never made. That is coordinated omission, and
/// <c>docs/14-Performance.md</c> §1.2 requires load tests here not to have it. This arms
/// arrivals against a clock: the schedule is fixed before the run and does not consult the
/// system's health.
/// </para>
/// <para>
/// <strong>What each arrival is.</strong> A real durable flow: acquire a lease from
/// <c>PostgresLeaseStore</c>, open a <c>flow_instance</c> row, run
/// <c>FlowEngine.ExecuteAsync</c> over the plan, release the lease. The engine commits one
/// journal row per step boundary through <c>PostgresFlowJournal</c>, which is the write B7 is
/// a budget for, and <see cref="TimingJournal"/> times exactly that call. Nothing here writes
/// SQL of its own.
/// </para>
/// <para>
/// <strong>The other three writes are timed too, and are not B7.</strong> A flow arrival also
/// takes a lease, opens an instance and completes one, and those are what make an offered
/// commit rate cost more than the commits. They are recorded beside the commit so that a
/// reader can see where a saturated node's capacity actually went, and they are excluded from
/// the budget because the budget names a step commit.
/// </para>
/// </remarks>
internal static class CommitBenchmark
{
    /// <summary>Runs one arm at one offered rate and returns its result.</summary>
    /// <param name="options">The run's parameters.</param>
    /// <param name="dataSource">The node's data source, over this arm's migrated schema.</param>
    /// <param name="rate">The offered commit rate, in commits/s/node.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The arm, as the results document records it.</returns>
    public static async Task<JsonObject> RunAsync(
        BenchmarkOptions options,
        NpgsqlDataSource dataSource,
        int rate,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine(
            $"==> B7: offering {rate} commits/s/node " +
            $"({rate / (double)options.Steps:F0} flows/s x {options.Steps} steps), " +
            $"{options.Warmup.TotalSeconds:F0}s warmup + {options.Measure.TotalSeconds:F0}s");

        var journal = new TimingJournal(new PostgresFlowJournal(dataSource));
        var leases = new PostgresLeaseStore(dataSource);
        var engine = new FlowEngine(SystemClock.Instance);
        var dispatcher = new BenchmarkDispatcher(options.PayloadBytes);
        var plan = BenchmarkDispatcher.Plan(options.Steps);

        var policy = new LeasePolicy
        {
            Ttl = options.LeaseTtl,
            RenewalInterval = TimeSpan.FromSeconds(
                Math.Max(1, options.LeaseTtl.TotalSeconds / 3)),
        };

        var arm = new Arm(options, journal, leases, engine, dispatcher, plan, policy);

        await arm.GenerateAsync(rate, cancellationToken).ConfigureAwait(false);

        var drained = await arm.DrainAsync(cancellationToken).ConfigureAwait(false);
        var window = arm.WindowSeconds;
        var commits = journal.Commit.Count;

        var result = new JsonObject
        {
            ["offeredCommitsPerSecond"] = rate,
            ["achievedCommitsPerSecond"] =
                Math.Round(window > 0 ? commits / window : 0, 2, MidpointRounding.AwayFromZero),
            ["windowSeconds"] = Math.Round(window, 3, MidpointRounding.AwayFromZero),
            ["arrivalsDue"] = arm.Due,
            ["flowsStarted"] = arm.Started,
            ["flowsCompleted"] = arm.Completed,
            ["flowsFailed"] = arm.Failed,
            ["passesBlockedByInFlightCap"] = arm.Deferred,
            ["drained"] = drained,
            ["commitSamples"] = commits,
            ["commitMs"] = journal.Commit.ToJson(),
            ["instanceStartMs"] = journal.Start.ToJson(),
            ["instanceCompleteMs"] = journal.Complete.ToJson(),
            ["leaseAcquireMs"] = arm.LeaseAcquire.ToJson(),
            ["flowLatencyFromDueMs"] = arm.FlowLatency.ToJson(),
            ["refusals"] = Refusals(journal, arm),
        };

        Print(result);

        return result;
    }

    private static JsonObject Refusals(TimingJournal journal, Arm arm)
    {
        var refusals = new JsonObject();

        foreach (var (code, count) in journal.Refusals.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            refusals[code] = count;
        }

        foreach (var (code, count) in arm.Failures.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            refusals["flow:" + code] = count;
        }

        return refusals;
    }

    private static void Print(JsonObject result)
    {
        var commit = result["commitMs"];

        if (commit is null)
        {
            Console.WriteLine("    nothing was committed.");

            return;
        }

        Console.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"    achieved {result["achievedCommitsPerSecond"]!.GetValue<double>():F1}/s | " +
                $"commit p50 {commit["p50"]!.GetValue<double>():F3} " +
                $"p95 {commit["p95"]!.GetValue<double>():F3} " +
                $"p99 {commit["p99"]!.GetValue<double>():F3} " +
                $"max {commit["max"]!.GetValue<double>():F3} ms | " +
                $"{result["flowsCompleted"]} flows, {result["flowsFailed"]} failed"));
    }

    /// <summary>One arm's generator, and everything it counts.</summary>
    private sealed class Arm(
        BenchmarkOptions options,
        TimingJournal journal,
        PostgresLeaseStore leases,
        FlowEngine engine,
        BenchmarkDispatcher dispatcher,
        ExecutionPlan plan,
        LeasePolicy policy)
    {
        private long _inFlight;
        private long _started;
        private long _completed;
        private long _failed;
        private long _deferred;
        private long _due;
        private long _measuringFrom;

        public Latencies LeaseAcquire { get; } = new();

        public Latencies FlowLatency { get; } = new();

        public System.Collections.Concurrent.ConcurrentDictionary<string, int> Failures { get; } =
            new(StringComparer.Ordinal);

        public long Due => Interlocked.Read(ref _due);

        public long Started => Interlocked.Read(ref _started);

        public long Completed => Interlocked.Read(ref _completed);

        public long Failed => Interlocked.Read(ref _failed);

        public long Deferred => Interlocked.Read(ref _deferred);

        /// <summary>Seconds from the end of the warmup to the last sample taken.</summary>
        public double WindowSeconds { get; private set; }

        /// <summary>
        /// Arms arrivals against a fixed schedule for the warmup and then the measured window.
        /// </summary>
        /// <param name="rate">The offered commit rate.</param>
        /// <param name="cancellationToken">Cancels the run.</param>
        /// <returns>A task that completes when the schedule is exhausted.</returns>
        /// <remarks>
        /// The schedule is "how many arrivals should have happened by now", recomputed from the
        /// clock on every pass rather than accumulated from a sleep. A sleep that overshoots by
        /// a millisecond a thousand times is a rate that is quietly 5 % low; this one catches
        /// up instead, which is what makes the offered rate an input.
        /// </remarks>
        public async Task GenerateAsync(int rate, CancellationToken cancellationToken)
        {
            var flowsPerSecond = rate / (double)options.Steps;
            var start = Stopwatch.GetTimestamp();
            var total = options.Warmup + options.Measure;
            var warmedUp = false;
            long issued = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var elapsed = Stopwatch.GetElapsedTime(start);

                if (!warmedUp && elapsed >= options.Warmup)
                {
                    // Everything before this instant priced a cold pool, a cold plan and an
                    // empty table. The counters restart here and the window starts here.
                    journal.Reset();
                    LeaseAcquire.Clear();
                    FlowLatency.Clear();
                    Failures.Clear();
                    Interlocked.Exchange(ref _started, 0);
                    Interlocked.Exchange(ref _completed, 0);
                    Interlocked.Exchange(ref _failed, 0);
                    Interlocked.Exchange(ref _deferred, 0);
                    Interlocked.Exchange(ref _due, 0);
                    Interlocked.Exchange(ref _measuringFrom, Stopwatch.GetTimestamp());

                    warmedUp = true;
                }

                if (elapsed >= total)
                {
                    return;
                }

                var owed = (long)(elapsed.TotalSeconds * flowsPerSecond) - issued;

                for (var i = 0; i < owed; i++)
                {
                    if (Interlocked.Read(ref _inFlight) >= options.MaxInFlight)
                    {
                        // The cap bounds memory and the connection pool. It does not slow the
                        // schedule: the arrivals it holds back stay owed and are issued on a
                        // later pass, still carrying the instant they were *due*, so a backlog
                        // shows up in flowLatencyFromDueMs rather than being hidden by a
                        // generator that politely waited.
                        if (warmedUp)
                        {
                            _ = Interlocked.Increment(ref _deferred);
                        }

                        break;
                    }

                    issued++;

                    var dueAt = start + (long)(issued / flowsPerSecond * Stopwatch.Frequency);

                    _ = Interlocked.Increment(ref _inFlight);
                    _ = Task.Run(() => RunOneAsync(dueAt, cancellationToken), cancellationToken);
                }

                if (warmedUp)
                {
                    // What the schedule called for since the window opened, whether or not the
                    // node kept up with it. The gap against flowsStarted is the deficit.
                    Interlocked.Exchange(
                        ref _due,
                        (long)((elapsed - options.Warmup).TotalSeconds * flowsPerSecond));
                }

                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Waits for the flows still running, and closes the measurement window.</summary>
        /// <param name="cancellationToken">Cancels the wait.</param>
        /// <returns>Whether everything finished inside the budget.</returns>
        /// <remarks>
        /// The window includes the drain, in both the numerator and the denominator of the
        /// achieved rate. A saturated node's honest throughput is what it finished divided by
        /// how long it took to finish it, and cutting the sample off at the generator's last
        /// arrival would delete precisely the slowest commits.
        /// </remarks>
        public async Task<bool> DrainAsync(CancellationToken cancellationToken)
        {
            var deadline = Stopwatch.GetTimestamp() + (300 * Stopwatch.Frequency);
            var drained = true;

            while (Interlocked.Read(ref _inFlight) > 0)
            {
                if (Stopwatch.GetTimestamp() > deadline || cancellationToken.IsCancellationRequested)
                {
                    drained = false;

                    break;
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }

            var from = Interlocked.Read(ref _measuringFrom);

            WindowSeconds = from == 0
                ? 0
                : (Stopwatch.GetTimestamp() - from) / (double)Stopwatch.Frequency;

            return drained;
        }

        private async Task RunOneAsync(long dueAt, CancellationToken cancellationToken)
        {
            try
            {
                _ = Interlocked.Increment(ref _started);

                var instance = Guid.CreateVersion7();
                var acquiring = Stopwatch.GetTimestamp();

                var acquired = await DurableLease
                    .AcquireAsync(leases, instance, options.NodeName, policy, cancellationToken)
                    .ConfigureAwait(false);

                LeaseAcquire.Add(Milliseconds(acquiring));

                if (acquired.IsFailure)
                {
                    Fail(acquired.Error.Code);

                    return;
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
                        Fail(begun.Error.Code);

                        return;
                    }

                    var result = await engine
                        .ExecuteAsync(plan, dispatcher, invocation, begun.Value, cancellationToken)
                        .ConfigureAwait(false);

                    if (result.IsSuccess)
                    {
                        _ = Interlocked.Increment(ref _completed);
                        FlowLatency.Add(Milliseconds(dueAt));
                    }
                    else
                    {
                        Fail(result.Error!.Code);
                    }
                }
                finally
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Fail("cancelled");
            }
            catch (Exception failure)
            {
                // A harness that stopped on the first broken socket would report a partial run
                // as a whole one. Every exception is counted by type and printed beside the
                // result; none is swallowed.
                Fail(failure.GetType().Name);
            }
            finally
            {
                _ = Interlocked.Decrement(ref _inFlight);
            }
        }

        private void Fail(string code)
        {
            _ = Interlocked.Increment(ref _failed);
            _ = Failures.AddOrUpdate(code, 1, static (_, count) => count + 1);
        }

        private static double Milliseconds(long from) =>
            (Stopwatch.GetTimestamp() - from) * 1000.0 / Stopwatch.Frequency;
    }
}
