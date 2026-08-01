using Shouldly;
using Xunit;

namespace FlowX.Core.Tests;

/// <summary>
/// Budget B2 — <strong>zero allocations per step</strong> — enforced as a unit test
/// rather than a benchmark.
/// </summary>
/// <remarks>
/// <para>
/// These are the parts of the hot path that exist today. The step loop itself
/// arrives at WP-4; when it does, its allocation assertion belongs here beside
/// these, not in the benchmark project. A budget checked once a night is a budget
/// that regresses for a day before anyone notices.
/// </para>
/// <para>
/// "Zero" means zero. Not "small", not "acceptable" — B2 is a hard zero in
/// <a href="../../docs/14-Performance.md">14-Performance</a>, and a hard zero is
/// the only allocation budget that cannot be quietly eroded one field at a time.
/// </para>
/// </remarks>
public sealed class AllocationBudgetTests
{
    /// <summary>
    /// Bytes one <c>CompensationStack.Unwind</c> enumeration allocates on a 64-bit
    /// runtime, and the figure <c>docs/benchmarks/baseline.json</c> gates
    /// <c>EngineBenchmarks.SagaFailure</c> on.
    /// </summary>
    /// <remarks>
    /// See <see cref="UnwindingAllocatesOneIteratorPerFailedFlow"/> for where the bytes
    /// go and for what has to be restated alongside this constant when it changes.
    /// </remarks>
    private const long UnwindIteratorBytes = 56;

    /// <summary>
    /// Bytes one <see cref="ExecutionPlan.Create"/> call allocates on a 64-bit runtime,
    /// and the figure <c>docs/benchmarks/baseline.json</c> gates
    /// <c>StepLoopBenchmarks.BuildPlan</c> on.
    /// </summary>
    /// <remarks>
    /// See <see cref="BuildingAPlanAllocatesOncePerFlowAtStartup"/> for where the bytes go
    /// and for what has to be restated alongside this constant when it changes.
    /// </remarks>
    private const long PlanConstructionBytes = 528;

    private static readonly ExecutionPlan Plan = ExecutionPlan.Create(
        Fixtures.PlaceOrder,
        StepGraph.Create([
            StepNode.ForCapability(0, Fixtures.ValidateOrder),
            StepNode.ForCapability(1, Fixtures.ReserveInventory, Fixtures.ReleaseInventory),
            StepNode.ForCapability(2, Fixtures.CapturePayment),
            StepNode.ForEmit(3, "order.placed"),
        ]));

    /// <summary>
    /// The positive control. Without it, every zero below could be a broken
    /// measurement rather than a fast code path — and a green gate that cannot fail
    /// is worse than no gate, because people trust it.
    /// </summary>
    [Fact]
    public void TheMeasurementCanDetectAnAllocationItShouldSee()
    {
        var allocated = Allocation.Measure(static () => _ = new object());

        allocated.ShouldBeGreaterThan(0,
            "If this reports zero, the harness is broken and every other assertion " +
            "in this class is meaningless.");
    }

    [Fact]
    public void WalkingTheStepGraphByIndexAllocatesNothing()
    {
        var allocated = Allocation.Measure(static () =>
        {
            var steps = Plan.Graph.Steps;
            var count = 0;

            for (var i = 0; i < steps.Length; i++)
            {
                if (steps[i].IsCompensable)
                {
                    count++;
                }
            }

            _ = count;
        });

        allocated.ShouldBe(0, "The engine's step loop walks the graph once per execution.");
    }

    [Fact]
    public void ForeachOverTheStepArrayAllocatesNothing()
    {
        // ImmutableArray<T> exposes a struct enumerator, so foreach over the concrete
        // type does not box. Asserted because a refactor to IEnumerable<StepNode>
        // would silently start allocating on every execution.
        var allocated = Allocation.Measure(static () =>
        {
            foreach (var step in Plan.Graph.Steps)
            {
                _ = step.Index;
            }
        });

        allocated.ShouldBe(0);
    }

    [Fact]
    public void ExposingTheStepsAsIEnumerableWouldAllocate()
    {
        // The counter-example, kept so the rule above has a visible reason. This is
        // what the previous test is protecting against.
        var allocated = Allocation.Measure(static () =>
        {
            IEnumerable<StepNode> boxed = Plan.Graph.Steps;

            foreach (var step in boxed)
            {
                _ = step.Index;
            }
        });

        allocated.ShouldBeGreaterThan(0,
            "Boxing the struct enumerator allocates. This is why StepGraph.Steps is " +
            "typed as ImmutableArray<StepNode> and not as IEnumerable<StepNode>.");
    }

    [Fact]
    public void ReadingPrecomputedPlanFactsAllocatesNothing()
    {
        var allocated = Allocation.Measure(static () =>
        {
            _ = Plan.HasCompensation;
            _ = Plan.CompensableStepIndices.Length;
            _ = Plan.SideEffects.Length;
            _ = Plan.Flow.Profile;
            _ = Plan.Graph.Count;
        });

        allocated.ShouldBe(0,
            "These are read on the failure path, where an incident is already under way.");
    }

    [Fact]
    public void TheSuccessPathOfResultAllocatesNothing()
    {
        var allocated = Allocation.Measure(static () =>
        {
            var result = Result.Ok(42);
            _ = result.IsSuccess;
            _ = result.Value;
            _ = result.Map(static x => x + 1).Value;
        });

        allocated.ShouldBe(0);
    }

    [Fact]
    public void TheFailurePathOfResultAllocatesNothing()
    {
        var error = new Error("test.failed", "boom", ErrorCategory.Unavailable);

        var allocated = Allocation.Measure(() =>
        {
            var result = Result.Fail<int>(error);
            _ = result.IsFailure;
            _ = result.Map(static x => x + 1).Error;
            _ = result.TryGetValue(out _, out _);
        });

        allocated.ShouldBe(0,
            "ADR-0007: errors are values precisely so the failure path costs nothing. " +
            "Latency matters most when things are already going wrong.");
    }

    /// <summary>
    /// Documents a real cost rather than asserting a zero that is not true today.
    /// </summary>
    /// <remarks>
    /// Constructing a <see cref="CompensationStack"/> allocates: <c>Stack&lt;T&gt;</c>
    /// grows its backing array, <c>HashSet&lt;T&gt;</c> builds buckets, and
    /// <c>Unwind</c> is an iterator.
    /// <para>
    /// WP-4 resolved this, and not the way this tripwire predicted. The prediction was
    /// that the type would change; what actually happened is that the type gained a
    /// <c>Reset</c> and the engine's pooled context now owns one instance for its whole
    /// life. Constructing one still allocates — that is what this test measures — but
    /// the engine never does. See
    /// <c>CompensationStackAllocatesNothingWhenReused</c> below for the path the
    /// runtime actually takes.
    /// </para>
    /// </remarks>
    [Fact]
    public void CompensationStackAllocatesTodayAndWP4MustPoolIt()
    {
        var step = StepNode.ForCapability(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);

        var allocated = Allocation.Measure(() =>
        {
            var stack = new CompensationStack();
            stack.RecordCompleted(step);

            foreach (var pending in stack.Unwind())
            {
                _ = pending.Index;
            }
        });

        allocated.ShouldBeGreaterThan(0,
            "Recorded, not hidden. Constructing a stack costs; the engine avoids the " +
            "cost by never constructing one per execution.");
    }

    /// <summary>
    /// The path the runtime actually takes on a <em>successful</em> flow: record
    /// completed steps, reset, reuse. No unwind, because nothing failed.
    /// </summary>
    /// <remarks>
    /// This is the assertion that made WP-4's exit criterion reachable. Measurement
    /// found the 288 B; review had signed off on the code that contained it.
    /// </remarks>
    [Fact]
    public void RecordingAndResettingAReusedStackAllocatesNothing()
    {
        var step = StepNode.ForCapability(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);
        var stack = new CompensationStack();

        var allocated = Allocation.Measure(() =>
        {
            stack.RecordCompleted(step);
            stack.Reset();
        });

        allocated.ShouldBe(0,
            "Clear keeps the backing arrays, so a pooled owner pays the construction " +
            "cost once per pooled context rather than once per flow. This is the " +
            "success path, which is the one budget B2 governs.");
    }

    /// <summary>
    /// The exact size of the one object a failed flow allocates:
    /// <strong>56 B</strong>, x64, and the same figure
    /// <c>docs/benchmarks/baseline.json</c> gates <c>EngineBenchmarks.SagaFailure</c> on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Unwind</c> is an iterator, so enumerating it allocates a state machine. That is
    /// deliberately not fixed: one small object per <em>failed</em> flow, on a path where a
    /// compensation is about to make a network call anyway, and hand-rolling a struct
    /// enumerator would trade real readability for an allocation nobody will ever profile.
    /// Recorded so the trade is a decision rather than an oversight.
    /// </para>
    /// <para>
    /// <strong>What is asserted is the number, not a band, and the band is why.</strong>
    /// This test used to say <c>&gt; 0</c> and <c>&lt; 256</c>. Under that pair the figure
    /// walked 40 B → 48 B → 56 B across two working packages without a single test
    /// objecting, because a range cannot see a value move inside it. The only thing that
    /// did object was the benchmark gate — on a job that was already red for unrelated
    /// reasons, which is how sixteen bytes crossed <c>dev</c> unnoticed.
    /// </para>
    /// <para>
    /// <strong>Where the 56 B goes.</strong> 16 B of object header and method table, 4 B of
    /// iterator state, 4 B of captured thread id, 8 B for the <see cref="CompensationStack"/>
    /// it drains, and 24 B for the <see cref="CompensationEntry"/> it yields — an iterator's
    /// state machine carries the value it yields, so this number is a fact about that record.
    /// The record holds three references: the <see cref="StepNode"/>, the
    /// <c>FlowContext?</c> scope the step completed under, and the <c>StepScope</c> the
    /// journal keys its undo by. Both of the latter two are correctness — without the scope a
    /// compensation inside a <c>ForEach</c> undoes whichever element the loop ended on, and
    /// without the journal scope the row for the third element collides with the first — so
    /// there is no way back to 40 B that is not a regression.
    /// </para>
    /// <para>
    /// <strong>Change it and three things must change together</strong>: this literal, the
    /// table in <c>docs/benchmarks/README.md</c> §4, and <c>allocatedBytes</c> for
    /// <c>EngineBenchmarks.SagaFailure</c> in <c>docs/benchmarks/baseline.json</c>. Failing
    /// here rather than only in a nightly benchmark is the point: this runs in the
    /// <em>Allocation budget (B2)</em> job, on every pull request.
    /// </para>
    /// </remarks>
    [Fact]
    public void UnwindingAllocatesOneIteratorPerFailedFlow()
    {
        var step = StepNode.ForCapability(0, Fixtures.ReserveInventory, Fixtures.ReleaseInventory);
        var stack = new CompensationStack();

        var allocated = Allocation.Measure(() =>
        {
            stack.RecordCompleted(step);

            foreach (var pending in stack.Unwind())
            {
                _ = pending.Index;
            }
        });

        allocated.ShouldBe(
            UnwindIteratorBytes,
            $"Measured {allocated} B for one Unwind enumeration, against {UnwindIteratorBytes} B " +
            "committed. Either the iterator gained state, or CompensationEntry gained a " +
            "field — and EngineBenchmarks.SagaFailure in docs/benchmarks/baseline.json now " +
            "disagrees with this measurement. Restate both, in this commit, with the reason.");
    }

    /// <summary>
    /// Building a plan allocates, once per flow at start-up, and the figure is pinned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is not budget B2.</strong> B2 is per <em>step</em>, on the execution
    /// path, and it is a hard zero. <see cref="ExecutionPlan.Create"/> runs once per flow
    /// while the host is composing itself, and the whole point of it allocating is that
    /// the engine then does not: every precomputed field here — the compensable indices,
    /// the sorted side effects, <see cref="ExecutionPlan.HasParallel"/> and its three
    /// siblings — is work moved off the hot path. Recorded so that a validation rule added
    /// later cannot quietly turn a fast start-up into a slow one.
    /// </para>
    /// <para>
    /// <strong>Why an exact figure and not a band, and why this test exists at all.</strong>
    /// <c>docs/benchmarks/README.md</c> §4 said both recorded costs were "asserted by tests
    /// that require them to be greater than zero". For the unwind iterator that was true and
    /// insufficient — see <see cref="UnwindingAllocatesOneIteratorPerFailedFlow"/>, where a
    /// band let 16 B through. For this row it was not true at all: no test measured plan
    /// construction, and the only thing that did was <c>StepLoopBenchmarks.BuildPlan</c> on
    /// the <em>Benchmark budgets</em> job, which was red for unrelated reasons for two days.
    /// Under that arrangement the plan grew from 520 B to 528 B at <c>60de884</c> and the
    /// only report of it was one error line among five that nobody was reading.
    /// </para>
    /// <para>
    /// <strong>The figure is exact, deterministic and machine-independent, and that was
    /// checked rather than assumed.</strong> <c>ExecutionPlan.Create</c> reads nothing from
    /// the environment: no <c>Environment.ProcessorCount</c>, no pooled buffer sized from
    /// the machine, no Roslyn. Measured directly, one call allocates the same number of
    /// bytes at every repetition count from 1 to 200 000, and the container and the GitHub
    /// hosted runner report the same 528 B for the benchmark that gates it.
    /// </para>
    /// <para>
    /// <strong>Where the 528 B goes,</strong> for the four-step plan below: the
    /// <see cref="ExecutionPlan"/> instance itself — 16 B of header and method table, four
    /// references, and four <c>bool</c> flags that push it over an 8-byte boundary — plus
    /// the <c>ImmutableArray&lt;int&gt;</c> builder and its two arrays for the one
    /// compensable index, and the <c>SortedSet&lt;string&gt;</c> with a node per distinct
    /// side effect that collects them in a deterministic order. The 8 B step from 520 B is
    /// <see cref="ExecutionPlan.HasParallel"/>: it was the <em>first</em> <c>bool</c> on a
    /// type whose fields until then were exactly four references, so it cost a padding word,
    /// and <see cref="ExecutionPlan.HasSubFlow"/>,
    /// <see cref="ExecutionPlan.HasCompensationPolicies"/> and
    /// <see cref="ExecutionPlan.HasEmit"/> then cost nothing at all.
    /// </para>
    /// <para>
    /// <strong>Change it and three things must change together</strong>: this literal,
    /// the table in <c>docs/benchmarks/README.md</c> §4, and <c>allocatedBytes</c> for
    /// <c>StepLoopBenchmarks.BuildPlan</c> in <c>docs/benchmarks/baseline.json</c>. The
    /// graph below is the one <c>StepLoopBenchmarks.BuildPlan</c> builds, deliberately and
    /// not by reusing <see cref="Fixtures"/>: <c>Fixtures.CapturePayment</c> declares a
    /// second side effect, which would make this a different measurement from the one the
    /// baseline gates and quietly break the link between the two.
    /// </para>
    /// </remarks>
    [Fact]
    public void BuildingAPlanAllocatesOncePerFlowAtStartup()
    {
        var validate = CapabilityDescriptor.Create("order.validate", "1.0.0", true);
        var reserve = CapabilityDescriptor.Create("inventory.reserve", "1.0.0", true, "inventory-ledger");
        var release = CapabilityDescriptor.Create("inventory.release", "1.0.0", true, "inventory-ledger");
        var capture = CapabilityDescriptor.Create("payment.capture", "2.1.0", false, "payment-gateway");

        var flow = FlowDescriptor.Create(
            "order.place", "1.0.0", ExecutionProfile.Ephemeral, TimeSpan.FromSeconds(30));

        var graph = StepGraph.Create([
            StepNode.ForCapability(0, validate),
            StepNode.ForCapability(1, reserve, release),
            StepNode.ForCapability(2, capture),
            StepNode.ForEmit(3, "order.placed"),
        ]);

        var allocated = Allocation.Measure(() => _ = ExecutionPlan.Create(flow, graph));

        allocated.ShouldBe(
            PlanConstructionBytes,
            $"Measured {allocated} B to build a four-step plan, against {PlanConstructionBytes} B " +
            "committed. Something in ExecutionPlan.Create started allocating, or the plan " +
            "gained a field — and StepLoopBenchmarks.BuildPlan in docs/benchmarks/baseline.json " +
            "now disagrees with this measurement. Restate both, in this commit, with the reason.");
    }
}
