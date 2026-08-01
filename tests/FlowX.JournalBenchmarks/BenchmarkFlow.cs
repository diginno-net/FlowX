using System.Text.Json.Serialization;
using FlowX.Runtime;

namespace FlowX.JournalBenchmarks;

/// <summary>The contract a measured step writes into its journal row.</summary>
/// <param name="Blob">
/// Filler, sized by <c>--payload-bytes</c>. A journal row's cost is partly the size of the
/// document going into it, so the size is a parameter and is recorded beside the result
/// rather than left at whatever an empty payload happens to be.
/// </param>
internal sealed record BenchmarkPayload(string Blob);

/// <summary>The generated metadata for <see cref="BenchmarkPayload"/>.</summary>
/// <remarks>
/// <c>JournalPayload.Of</c> requires a <c>JsonTypeInfo&lt;T&gt;</c> and offers no overload
/// that reflects over a type, which is the control ADR-0015 commitment 5 rests on. A
/// source-generated context is therefore not a convenience here — it is the only way to put
/// a payload in a journal row at all.
/// </remarks>
[JsonSerializable(typeof(BenchmarkPayload))]
internal sealed partial class BenchmarkPayloadContext : JsonSerializerContext;

/// <summary>
/// The flow B7 and B8 are measured on, and the dispatcher whose steps do nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The steps are deliberately empty, and that is what makes the number B7's.</strong>
/// <c>docs/14-Performance.md</c> §1.1 defines the platform budgets as "overhead attributable
/// to FlowX, excluding user code and I/O". A step that called a database would put its own
/// latency inside the flow and outside the budget. What is left when the step does nothing is
/// the step loop and the journal write, and the journal write is four orders of magnitude
/// larger — which is §2's whole point about a durable flow.
/// </para>
/// <para>
/// <strong>What the steps do write is a payload.</strong> Since WP-59 a generated dispatcher
/// describes every step boundary with a result and a state-bag snapshot, so a commit carries
/// two JSON documents rather than two nulls. Measuring the null shape would price a
/// transaction no shipped flow performs.
/// </para>
/// </remarks>
internal sealed class BenchmarkDispatcher : IStepDispatcher
{
    /// <summary>The key a generated state bag would hold this flow's one contract under.</summary>
    private const string StateMember = "state";

    private readonly JournalPayload _result;
    private readonly JournalPayload _stateBag;

    /// <summary>Builds a dispatcher whose steps write a payload of a given size.</summary>
    /// <param name="payloadBytes">How many bytes of filler each payload carries.</param>
    /// <remarks>
    /// The result is an <c>Of&lt;T&gt;</c> and the state bag an <c>OfState</c>, which is the
    /// shape the generator emits: a step result is one contract, and a state bag is a
    /// document of named contracts. The distinction matters here because
    /// <see cref="RestoreState"/> has to read back what was written, and what was written is
    /// the named-member document.
    /// </remarks>
    public BenchmarkDispatcher(int payloadBytes)
    {
        if (payloadBytes == 0)
        {
            _result = JournalPayload.Empty;
            _stateBag = JournalPayload.Empty;

            return;
        }

        var value = new BenchmarkPayload(new string('x', payloadBytes));

        _result = JournalPayload.Of(value, BenchmarkPayloadContext.Default.BenchmarkPayload);
        _stateBag = JournalPayload.OfState(
            [JournalMember.Of(StateMember, value, BenchmarkPayloadContext.Default.BenchmarkPayload)]);
    }

    /// <summary>The result every step of this dispatcher records.</summary>
    /// <remarks>
    /// Exposed because B8's arrangement writes the same committed rows the engine would, and
    /// it has to write the same payloads into them — an arrangement whose rows are smaller
    /// than the engine's would make the frontier read cheaper than the one being budgeted.
    /// </remarks>
    public JournalPayload Result => _result;

    /// <summary>The state-bag snapshot every step of this dispatcher records.</summary>
    public JournalPayload StateBag => _stateBag;

    /// <summary>The flow, at a given number of steps.</summary>
    /// <param name="steps">How many capability steps it has.</param>
    /// <returns>The plan.</returns>
    /// <remarks>
    /// The flow id carries the step count so that a catalogue can hold every shape a run
    /// measures at once: a resumed instance is pinned to the flow it started as, and a
    /// recovery node that does not carry that exact version leaves it alone.
    /// </remarks>
    public static ExecutionPlan Plan(int steps)
    {
        var nodes = new StepNode[steps];

        for (var index = 0; index < steps; index++)
        {
            nodes[index] = StepNode.ForCapability(
                index,
                CapabilityDescriptor.Create("journal.step", "1.0.0", isIdempotent: true));
        }

        return ExecutionPlan.Create(
            FlowDescriptor.Create(
                FlowId(steps), "1.0.0", ExecutionProfile.Durable, TimeSpan.FromMinutes(30)),
            StepGraph.Create(nodes));
    }

    /// <summary>The flow id for a step count.</summary>
    /// <param name="steps">How many steps the flow has.</param>
    /// <returns>The id.</returns>
    public static string FlowId(int steps) => $"journal.bench.s{steps}";

    /// <inheritdoc />
    public ValueTask<StepOutcome> ExecuteAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(StepOutcome.Success);

    /// <inheritdoc />
    public ValueTask<StepOutcome> CompensateAsync(int stepIndex, FlowContext ctx, CancellationToken ct) =>
        ValueTask.FromResult(StepOutcome.Success);

    /// <inheritdoc />
    public StepJournalEntry DescribeStep(int stepIndex, FlowContext ctx) =>
        StepJournalEntry.Of(_result, _stateBag);

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <strong>Present because the engine will call it, and a dispatcher that writes a state
    /// bag it cannot read back fails the resume outright.</strong> That is not a hypothetical:
    /// the first version of this harness described a state bag and inherited the interface's
    /// default <c>RestoreState</c>, which throws — so every rehydrated instance was refused
    /// with <c>flow.state_restore_failed</c>, ran no step, and stayed <c>Running</c>, while
    /// <c>FlowRecoveryScan</c> counted all of them as resumed. The instances-left-unfinished
    /// count in <see cref="RehydrationBenchmark"/> exists because of it.
    /// </para>
    /// <para>
    /// It does the same work the generated version does — parse the document, switch on the
    /// member name, deserialise through the generated context, ignore anything this build does
    /// not know — because that work is inside the resume the budget covers.
    /// </para>
    /// </remarks>
    public void RestoreState(FlowContext ctx, string stateBagJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(stateBagJson);

        foreach (var member in document.RootElement.EnumerateObject())
        {
            switch (member.Name)
            {
                case StateMember:
                    ctx.Set(JournalState.Read<BenchmarkPayload>(
                        member.Value, BenchmarkPayloadContext.Default));
                    break;

                default:
                    // The schemaVersion stamp, or a contract an older build wrote. Both are
                    // rows that must still resume, so neither is an error.
                    break;
            }
        }
    }

    /// <inheritdoc />
    public bool Evaluate(int stepIndex, FlowContext ctx) =>
        throw new NotSupportedException("The measured plan has no branch step.");

    /// <inheritdoc />
    public int Select(int stepIndex, FlowContext ctx) =>
        throw new NotSupportedException("The measured plan has no switch step.");

    /// <inheritdoc />
    public IterationSource BeginIteration(int stepIndex, FlowContext ctx) =>
        throw new NotSupportedException("The measured plan has no iteration.");

    /// <inheritdoc />
    public FlowContext EnterIteration(
        int stepIndex, in IterationSource source, int iteration, FlowContext ctx) =>
        throw new NotSupportedException("The measured plan has no iteration.");
}
