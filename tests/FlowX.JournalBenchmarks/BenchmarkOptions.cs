using System.Globalization;

namespace FlowX.JournalBenchmarks;

/// <summary>
/// Every parameter of a run, and the parsing of the command line that supplies them.
/// </summary>
/// <remarks>
/// <para>
/// Every value is echoed into the results document. A measurement whose configuration is not
/// recorded beside it is a measurement that cannot be repeated — and for B7 in particular
/// the configuration <em>is</em> half the budget, because "15 ms" is only a budget when the
/// rate it was measured at is written next to it.
/// </para>
/// <para>
/// The defaults are the smallest run that still offers B7's own rate at the database and
/// still rehydrates enough instances for a p99 to mean something. A default that costs
/// twenty minutes is a harness nobody runs.
/// </para>
/// </remarks>
internal sealed record BenchmarkOptions
{
    /// <summary>The offered commit rates, in commits/s/node, each measured as its own arm.</summary>
    /// <remarks>
    /// B7's own rate is the last one, and the checker takes its verdict from that arm. The
    /// smaller rates are what make the larger one readable: a p99 at one rate is a number, and
    /// a p99 at four rates is a curve that says whether the node is anywhere near saturation.
    /// </remarks>
    public IReadOnlyList<int> Rates { get; init; } = [500, 1000, 2000, 5000];

    /// <summary>How many capability steps the measured flow has.</summary>
    /// <remarks>
    /// Three, which is the shape <c>tests/FlowX.Chaos</c> runs and the shape the recovery
    /// tests use. Each step is one commit, so a flow arrival is three commits.
    /// </remarks>
    public int Steps { get; init; } = 3;

    /// <summary>How many bytes of payload each step writes into its journal row.</summary>
    /// <remarks>
    /// Written twice per commit — once as the step's result and once as the flow's state bag
    /// — because that is what a generated dispatcher does since WP-59. Zero is available and
    /// is not the default: an empty payload measures a transaction shape no real flow has.
    /// </remarks>
    public int PayloadBytes { get; init; } = 256;

    /// <summary>How long an arm is measured for, after its warmup.</summary>
    public TimeSpan Measure { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long an arm runs before its samples start being kept.</summary>
    /// <remarks>
    /// The connection pool fills, the plan JITs and the first WAL segment is written during
    /// this window. Keeping it in the sample would price the first second of a process's life
    /// as though it were the steady state.
    /// </remarks>
    public TimeSpan Warmup { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How many flows may be in flight at once before the generator stops arming new ones.</summary>
    /// <remarks>
    /// A bound on memory and on the connection pool, not a closed loop: an arrival delayed by
    /// this cap still has its latency measured from the instant it was <em>due</em>, so the
    /// backlog shows up in the flow latency rather than being hidden by it. That is the
    /// difference between an open-loop generator with a ceiling and a closed-loop one.
    /// </remarks>
    public int MaxInFlight { get; init; } = 512;

    /// <summary>The node's connection pool ceiling.</summary>
    /// <remarks>
    /// Below PostgreSQL's own <c>max_connections</c> on purpose, and recorded, because a
    /// commit that waits for a connection waits inside <c>CommitAsync</c> and therefore
    /// inside B7's number. The pool size is part of the measurement, not a detail of it.
    /// </remarks>
    public int MaxPoolSize { get; init; } = 64;

    /// <summary>The lease TTL a measured flow holds.</summary>
    public TimeSpan LeaseTtl { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How many instances each rehydration arm measures.</summary>
    public int Instances { get; init; } = 400;

    /// <summary>
    /// How many instances an arm rehydrates and throws away before it starts recording.
    /// </summary>
    /// <remarks>
    /// The rehydration equivalent of <see cref="Warmup"/>, and it was not optional: the pilot
    /// of this arm reported a p99 of 38.9 ms at one committed step and 10.1 ms at sixteen —
    /// the wrong way round for a query whose cost grows with history, and entirely the first
    /// sweep of a process that had never done this before.
    /// </remarks>
    public int WarmupInstances { get; init; } = 64;

    /// <summary>The committed-step depths B8 is measured at.</summary>
    /// <remarks>
    /// A sweep rather than a single figure because
    /// <c>PostgresFlowJournal.ReadResumeFrontierAsync</c> reads every committed row for the
    /// instance with no lower bound, so what it costs is a function of how long the instance
    /// has been running. ADR-0016 records that <c>flow_instance.state_bag_sequence</c> exists
    /// to bound exactly this read and that nothing reads it; this is the sweep that says what
    /// that gap costs.
    /// </remarks>
    public IReadOnlyList<int> Depths { get; init; } = [1, 4, 16, 64];

    /// <summary>The depth B8's verdict is taken at.</summary>
    /// <remarks>
    /// The shallowest, which is the most favourable shape a rehydration can have: one
    /// committed step to read back. A miss there is unambiguous, and a pass there is a
    /// statement about the floor and about nothing deeper — which is why the deeper arms are
    /// measured and named in the report rather than left out.
    /// </remarks>
    public int BudgetDepth { get; init; } = 1;

    /// <summary>How many instances one recovery sweep takes over at once.</summary>
    public int RecoveryConcurrency { get; init; } = 8;

    /// <summary>How many instances are prepared concurrently before a rehydration arm runs.</summary>
    public int SetupConcurrency { get; init; } = 16;

    /// <summary>Which halves of the run to execute.</summary>
    public bool RunCommit { get; init; } = true;

    /// <summary>Whether to run the rehydration half.</summary>
    public bool RunRehydration { get; init; } = true;

    /// <summary>The schema this run's tables live in.</summary>
    public string Schema { get; init; } = string.Empty;

    /// <summary>This node's identity, as an operator would read it.</summary>
    public string NodeName { get; init; } = "bench-node-1";

    /// <summary>Where the machine-readable results go.</summary>
    public string JsonPath { get; init; } = "b7-b8-journal.json";

    /// <summary>Whether to leave the schema behind for inspection.</summary>
    public bool KeepSchema { get; init; }

    /// <summary>The connection string, from the environment.</summary>
    public string ConnectionString { get; init; } = string.Empty;

    /// <summary>The rate B7's verdict is taken at: the largest offered.</summary>
    public int BudgetRate => Rates[^1];

    /// <summary>Parses a command line, leaving unset values at their defaults.</summary>
    /// <param name="args">The arguments, as <c>--key value</c> pairs.</param>
    /// <param name="connectionString">The connection string the environment supplied.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ArgumentException">An argument was not understood.</exception>
    public static BenchmarkOptions Parse(string[] args, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = new BenchmarkOptions
        {
            ConnectionString = connectionString,
            Schema = "flowx_bench_" + Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture),
        };

        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"'{args[i]}' has no value.", nameof(args));
            }

            options = Apply(options, args[i], args[i + 1]);
        }

        if (options.Rates.Count == 0)
        {
            throw new ArgumentException("'--rates' named no rate at all.", nameof(args));
        }

        if (!options.Depths.Contains(options.BudgetDepth))
        {
            throw new ArgumentException(
                $"'--budget-depth' is {options.BudgetDepth}, which '--depths' does not " +
                $"measure, so B8's verdict would be taken at a depth this run never ran.",
                nameof(args));
        }

        return options;
    }

    private static BenchmarkOptions Apply(BenchmarkOptions options, string key, string value) =>
        key switch
        {
            "--rates" => options with { Rates = List(key, value) },
            "--steps" => options with { Steps = Positive(key, value) },
            "--payload-bytes" => options with { PayloadBytes = NonNegative(key, value) },
            "--seconds" => options with { Measure = Seconds(key, value) },
            "--warmup-seconds" => options with { Warmup = TimeSpan.FromSeconds(NonNegative(key, value)) },
            "--max-in-flight" => options with { MaxInFlight = Positive(key, value) },
            "--max-pool-size" => options with { MaxPoolSize = Positive(key, value) },
            "--lease-ttl" => options with { LeaseTtl = Seconds(key, value) },
            "--instances" => options with { Instances = Positive(key, value) },
            "--warmup-instances" => options with { WarmupInstances = NonNegative(key, value) },
            "--depths" => options with { Depths = List(key, value) },
            "--budget-depth" => options with { BudgetDepth = Positive(key, value) },
            "--recovery-concurrency" => options with { RecoveryConcurrency = Positive(key, value) },
            "--setup-concurrency" => options with { SetupConcurrency = Positive(key, value) },
            "--only" => Only(options, value),
            "--schema" => options with { Schema = value },
            "--node" => options with { NodeName = value },
            "--json" => options with { JsonPath = value },
            "--keep-schema" => options with { KeepSchema = Flag(key, value) },
            _ => throw new ArgumentException($"Unknown argument '{key}'.", nameof(key)),
        };

    private static BenchmarkOptions Only(BenchmarkOptions options, string value) => value switch
    {
        "b7" => options with { RunCommit = true, RunRehydration = false },
        "b8" => options with { RunCommit = false, RunRehydration = true },
        "both" => options with { RunCommit = true, RunRehydration = true },
        _ => throw new ArgumentException($"'--only' takes b7, b8 or both, not '{value}'.", nameof(value)),
    };

    private static IReadOnlyList<int> List(string key, string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => Positive(key, part))];

    private static TimeSpan Seconds(string key, string value) =>
        TimeSpan.FromSeconds(Positive(key, value));

    private static bool Flag(string key, string value) =>
        bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"'{key}' takes true or false.", nameof(key));

    private static int Positive(string key, string value)
    {
        var parsed = NonNegative(key, value);

        return parsed > 0
            ? parsed
            : throw new ArgumentException($"'{key}' must be greater than zero.", nameof(key));
    }

    private static int NonNegative(string key, string value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
        && parsed >= 0
            ? parsed
            : throw new ArgumentException($"'{key}' must be a non-negative integer.", nameof(key));
}
