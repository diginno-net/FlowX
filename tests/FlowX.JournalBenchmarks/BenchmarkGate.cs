using Npgsql;

namespace FlowX.JournalBenchmarks;

/// <summary>
/// The gate that decides whether this run may happen at all, and refuses loudly rather than
/// quietly when it may not.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three outcomes, not two.</strong> The distinction is
/// <c>tests/FlowX.Postgres.Tests/PostgresTestDatabase.cs</c>'s and
/// <c>tests/FlowX.Chaos/ChaosDatabase.cs</c>'s, and it is the whole of the gating discipline
/// here: "nobody asked for a measurement" is a reason to skip, and "somebody asked for a
/// measurement and there is no server" is a defect in the run. A harness that skipped
/// quietly in the second case would report B7 and B8 as green on a run that measured
/// nothing, which is the one outcome this package exists to prevent — WP-50's whole
/// argument is that a budget nobody measures gets renegotiated instead of met.
/// </para>
/// <para>
/// <strong>The server is contacted here rather than discovered later.</strong> A run that
/// fails on its first commit after two minutes of setup is a run whose failure is hard to
/// read. One <c>SELECT version()</c> before anything else turns "the database is not there"
/// into the first line of output.
/// </para>
/// </remarks>
internal static class BenchmarkGate
{
    /// <summary>The variable that supplies a connection string.</summary>
    public const string ConnectionVariable = "FLOWX_POSTGRES_CONNECTION";

    /// <summary>The variable that opts a run in. Without it, nothing is measured.</summary>
    public const string OptInVariable = "FLOWX_JOURNAL_BENCH";

    /// <summary>The exit code a skipped run uses, so it is not mistaken for a pass.</summary>
    public const int SkippedExitCode = 0;

    /// <summary>The connection string, or null when none was supplied.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value
            ? value
            : null;

    /// <summary>Whether the run was opted in to.</summary>
    public static bool IsOptedIn =>
        Environment.GetEnvironmentVariable(OptInVariable) is { Length: > 0 } value
        && !string.Equals(value, "0", StringComparison.Ordinal)
        && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a run was skipped, in a sentence a reader can act on.</summary>
    public static string SkipReason =>
        $"{OptInVariable} is not set, so nothing was measured and neither B7 nor B8 has " +
        $"been reported. This harness offers thousands of transactions a second at a real " +
        $"PostgreSQL and takes minutes, so it is opt-in rather than part of the ordinary " +
        $"suite. Set {OptInVariable}=1 and {ConnectionVariable} to a PostgreSQL connection " +
        $"string to run it.";

    /// <summary>
    /// Decides whether this run may proceed.
    /// </summary>
    /// <returns>
    /// Null when the run may proceed; otherwise the reason, with <c>skipped</c> telling the
    /// caller whether that reason is a skip or a failure.
    /// </returns>
    public static (bool Skipped, string Reason)? Check()
    {
        if (!IsOptedIn)
        {
            return (true, SkipReason);
        }

        if (ConnectionString is null)
        {
            return (false,
                $"{OptInVariable} is set, so this run promised to measure B7 and B8 against " +
                $"a PostgreSQL server, and {ConnectionVariable} names none. This is a " +
                $"failure rather than a skip on purpose: a skip here would report two " +
                $"latency budgets as measured on a run that opened no connection.");
        }

        try
        {
            using var dataSource = NpgsqlDataSource.Create(ConnectionString);
            using var connection = dataSource.OpenConnection();
            using var command = connection.CreateCommand();

            command.CommandText = "SELECT version()";
            _ = command.ExecuteScalar();

            return null;
        }
        catch (Exception failure) when (failure is NpgsqlException or ArgumentException)
        {
            return (false,
                $"{ConnectionVariable} is set and the server did not answer, so this run " +
                $"measured nothing: {failure.Message}");
        }
    }
}
