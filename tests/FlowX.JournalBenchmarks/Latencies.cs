using System.Text.Json.Nodes;

namespace FlowX.JournalBenchmarks;

/// <summary>
/// A growing bag of latency samples, and the percentiles read out of it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every sample is kept, and no histogram bucket is between the measurement and the
/// percentile.</strong> <c>docs/14-Performance.md</c> §1.2 names HDR histograms as the
/// intended tooling for B7–B9, and an HDR histogram is a way of bounding memory when a run
/// takes hours — it costs a bounded relative error in exchange. This harness's longest arm
/// records a few hundred thousand doubles, which is a few megabytes, so paying for a bucket
/// error here would buy nothing. The deviation from that row is stated in
/// <c>docs/benchmarks/B7-B8-journal.md</c> rather than absorbed.
/// </para>
/// <para>
/// <strong>Nearest-rank, not interpolated.</strong> A p99 obtained by interpolating between
/// two neighbouring samples is a number no request experienced; nearest-rank returns an
/// observation. It is also what makes "the max is the p100" true, which matters when a tail
/// is what is being argued about.
/// </para>
/// <para>
/// Appending takes a lock. At five thousand commits a second that is five thousand
/// uncontended-most-of-the-time lock acquisitions a second on a list, against a millisecond
/// -scale operation that just finished a network round trip — three orders of magnitude below
/// what is being measured, which is the only reason it is acceptable.
/// </para>
/// </remarks>
internal sealed class Latencies
{
    private readonly Lock _gate = new();
    private readonly List<double> _samples = [];

    /// <summary>How many samples have been recorded.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    /// <summary>Records one observation, in milliseconds.</summary>
    /// <param name="milliseconds">The observed latency.</param>
    public void Add(double milliseconds)
    {
        lock (_gate)
        {
            _samples.Add(milliseconds);
        }
    }

    /// <summary>Forgets everything recorded so far.</summary>
    /// <remarks>Called at the end of a warmup, which is the only reason it exists.</remarks>
    public void Clear()
    {
        lock (_gate)
        {
            _samples.Clear();
        }
    }

    /// <summary>The distribution, as the results document records it.</summary>
    /// <returns>The percentiles, or null when nothing was sampled.</returns>
    /// <remarks>
    /// Null rather than zeroes. A row of zeroes is indistinguishable from an
    /// instantaneous operation, and the checker's whole job is to tell "fast" from "not
    /// measured".
    /// </remarks>
    public JsonObject? ToJson()
    {
        double[] sorted;

        lock (_gate)
        {
            if (_samples.Count == 0)
            {
                return null;
            }

            sorted = [.. _samples];
        }

        Array.Sort(sorted);

        return new JsonObject
        {
            ["samples"] = sorted.Length,
            ["mean"] = Round(sorted.Average()),
            ["p50"] = Round(Quantile(sorted, 0.50)),
            ["p90"] = Round(Quantile(sorted, 0.90)),
            ["p95"] = Round(Quantile(sorted, 0.95)),
            ["p99"] = Round(Quantile(sorted, 0.99)),
            ["p999"] = Round(Quantile(sorted, 0.999)),
            ["max"] = Round(sorted[^1]),
        };
    }

    /// <summary>The nearest-rank quantile of a sorted sample.</summary>
    /// <param name="sorted">The samples, ascending.</param>
    /// <param name="quantile">The quantile, between 0 and 1.</param>
    /// <returns>The observation at that rank.</returns>
    public static double Quantile(double[] sorted, double quantile)
    {
        ArgumentNullException.ThrowIfNull(sorted);

        var rank = (int)Math.Ceiling(quantile * sorted.Length);

        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static double Round(double value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
