using System.Collections.Concurrent;
using System.Diagnostics;

namespace FlowX.JournalBenchmarks;

/// <summary>
/// The real journal, with a stopwatch either side of the calls B7 and B8 are budgets for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is a decorator so that the thing being measured is the thing that ships.</strong>
/// B7 is the latency of a durable step commit, and the only honest way to time one is to time
/// the call the engine makes at a step boundary — <c>FlowEngine.CommitStepAsync</c> awaits
/// <c>IFlowJournal.CommitAsync</c> and does nothing else with the result. Reimplementing that
/// transaction in the harness would measure the harness's SQL, which is what
/// <c>docs/benchmarks/QR2-chaos.md</c> §1 says about every prior test of this subsystem.
/// </para>
/// <para>
/// <strong>What the instrument costs.</strong> Two <see cref="Stopwatch.GetTimestamp"/> calls
/// and one list append per commit, around an operation that opens a pooled connection, takes
/// a row lock, writes three statements and waits for <c>fsync</c>. That is nanoseconds around
/// milliseconds. It is not zero and it is not worth correcting for; it is stated instead.
/// </para>
/// <para>
/// <strong>B8's span is two calls, not one.</strong> <c>DurableExecution.ResumeAsync</c>
/// raises the fence and then reads the frontier, in that order and for the reason its own
/// remarks give. Rehydration is both, so this records the interval from entering
/// <see cref="FenceAsync"/> to leaving <see cref="ReadResumeFrontierAsync"/> for the same
/// instance — which is the wall clock of <c>ResumeAsync</c> itself, measured from inside the
/// only two calls it makes. The two halves are also kept separately, because knowing which of
/// them a miss belongs to is the difference between a fixable read and a fixable write.
/// </para>
/// </remarks>
internal sealed class TimingJournal : IFlowJournal
{
    private readonly IFlowJournal _inner;
    private readonly ConcurrentDictionary<Guid, long> _fenceEntered = new();

    /// <summary>Wraps a journal.</summary>
    /// <param name="inner">The journal doing the work — the real adapter.</param>
    public TimingJournal(IFlowJournal inner)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
    }

    /// <summary>B7: how long <c>IFlowJournal.CommitAsync</c> took, per step boundary.</summary>
    public Latencies Commit { get; } = new();

    /// <summary>How long opening a fresh instance's row took.</summary>
    public Latencies Start { get; } = new();

    /// <summary>How long finishing an instance took.</summary>
    public Latencies Complete { get; } = new();

    /// <summary>B8: fence plus frontier read, which is what rehydration is.</summary>
    public Latencies Rehydration { get; } = new();

    /// <summary>The fence half of a rehydration, on its own.</summary>
    public Latencies Fence { get; } = new();

    /// <summary>The frontier-read half of a rehydration, on its own.</summary>
    public Latencies Frontier { get; } = new();

    /// <summary>How many commits the store refused, by error code.</summary>
    /// <remarks>
    /// A refusal is a value, not an exception (ADR-0007), so nothing throws when a commit is
    /// fenced out or duplicated — and a run in which every commit was refused would otherwise
    /// look exactly like a fast one. Counted, and printed beside the p99.
    /// </remarks>
    public ConcurrentDictionary<string, int> Refusals { get; } = new(StringComparer.Ordinal);

    /// <summary>Forgets every sample, at the end of a warmup.</summary>
    public void Reset()
    {
        Commit.Clear();
        Start.Clear();
        Complete.Clear();
        Rehydration.Clear();
        Fence.Clear();
        Frontier.Clear();
        Refusals.Clear();
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowInstanceRecord>> StartAsync(
        FlowInstanceStart start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);

        var from = Stopwatch.GetTimestamp();
        var result = await _inner.StartAsync(start, cancellationToken).ConfigureAwait(false);

        Start.Add(Elapsed(from));
        Note(result.IsFailure ? result.Error.Code : null);

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FencingToken>> FenceAsync(
        Guid instanceId,
        FencingToken token,
        CancellationToken cancellationToken)
    {
        var from = Stopwatch.GetTimestamp();

        // Recorded before the call, so the span covers the fence itself and not only what
        // follows it. An instance fenced more than once keeps the latest entry, which is the
        // rehydration currently in flight for it.
        _fenceEntered[instanceId] = from;

        var result = await _inner.FenceAsync(instanceId, token, cancellationToken).ConfigureAwait(false);

        Fence.Add(Elapsed(from));
        Note(result.IsFailure ? result.Error.Code : null);

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<Result<JournalStep>> CommitAsync(
        StepCommit commit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(commit);

        var from = Stopwatch.GetTimestamp();
        var result = await _inner.CommitAsync(commit, cancellationToken).ConfigureAwait(false);

        Commit.Add(Elapsed(from));
        Note(result.IsFailure ? result.Error.Code : null);

        return result;
    }

    /// <inheritdoc />
    public async ValueTask<Result<FlowInstanceRecord>> CompleteAsync(
        Guid instanceId,
        FencingToken token,
        FlowInstanceState state,
        JournalPayload stateBag,
        CancellationToken cancellationToken)
    {
        var from = Stopwatch.GetTimestamp();
        var result = await _inner
            .CompleteAsync(instanceId, token, state, stateBag, cancellationToken)
            .ConfigureAwait(false);

        Complete.Add(Elapsed(from));
        Note(result.IsFailure ? result.Error.Code : null);

        return result;
    }

    /// <inheritdoc />
    public ValueTask<Result<FlowInstanceRecord>> ReadInstanceAsync(
        Guid instanceId,
        CancellationToken cancellationToken) =>
        _inner.ReadInstanceAsync(instanceId, cancellationToken);

    /// <inheritdoc />
    public async ValueTask<Result<ResumeFrontier>> ReadResumeFrontierAsync(
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var from = Stopwatch.GetTimestamp();
        var result = await _inner
            .ReadResumeFrontierAsync(instanceId, cancellationToken)
            .ConfigureAwait(false);

        var now = Stopwatch.GetTimestamp();

        Frontier.Add(Elapsed(from, now));

        if (_fenceEntered.TryRemove(instanceId, out var fenced))
        {
            Rehydration.Add(Elapsed(fenced, now));
        }

        Note(result.IsFailure ? result.Error.Code : null);

        return result;
    }

    /// <inheritdoc />
    public ValueTask<Result<IReadOnlyList<OutboxRecord>>> ReadOutboxAsync(
        Guid instanceId,
        CancellationToken cancellationToken) =>
        _inner.ReadOutboxAsync(instanceId, cancellationToken);

    private void Note(string? code)
    {
        if (code is not null)
        {
            _ = Refusals.AddOrUpdate(code, 1, static (_, count) => count + 1);
        }
    }

    private static double Elapsed(long from) => Elapsed(from, Stopwatch.GetTimestamp());

    private static double Elapsed(long from, long to) =>
        (to - from) * 1000.0 / Stopwatch.Frequency;
}
