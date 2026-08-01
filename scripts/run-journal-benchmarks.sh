#!/usr/bin/env bash
#
# B7 and B8: the durable step commit and the journal rehydration, measured against a real
# PostgreSQL through the real engine and the real plugins/FlowX.Postgres.
#
#   ./scripts/run-journal-benchmarks.sh                       # the default run
#   ./scripts/run-journal-benchmarks.sh --rates 500,5000 --seconds 30
#   ./scripts/run-journal-benchmarks.sh --only b8 --depths 1,4,16,64,256
#
# Every argument is passed through to the harness; see
# tests/FlowX.JournalBenchmarks/BenchmarkOptions.cs for the full list. The results document
# is docs/benchmarks/B7-B8-journal.md.
#
# GATING. This does not run as part of the ordinary suite and must not: it offers thousands
# of transactions a second at a database and takes minutes. It is opt-in on
# FLOWX_JOURNAL_BENCH, the way tests/FlowX.Chaos is opt-in on FLOWX_CHAOS and
# tests/FlowX.Postgres.Tests on FLOWX_POSTGRES_CONNECTION, and it follows the same rule —
#
#   * FLOWX_JOURNAL_BENCH unset            -> skip, with a reason, exit 0
#   * FLOWX_JOURNAL_BENCH set, no database -> FAIL, exit non-zero
#
# — because a skip in the second case would report two latency budgets as measured on a run
# that opened no connection, which is the outcome WP-50 exists to prevent.
#
# In CI (nobody runs this in CI yet; scheduling it is WP-62's subject):
#
#   FLOWX_JOURNAL_BENCH=1 \
#   FLOWX_POSTGRES_CONNECTION="Host=localhost;Port=5432;Username=postgres;Database=postgres" \
#     ./scripts/run-journal-benchmarks.sh
#
set -euo pipefail

cd "$(dirname "$0")/.."

# Under .artifacts/ because .gitignore already covers it: a harness that leaves an untracked
# file in the repository root teaches people to ignore `git status`.
RESULTS="${FLOWX_JOURNAL_BENCH_JSON:-.artifacts/b7-b8-journal.json}"

export DOTNET_NOLOGO=1
export DOTNET_CLI_TELEMETRY_OPTOUT=1

if [ -z "${FLOWX_JOURNAL_BENCH:-}" ]; then
  echo "==> Skipped: FLOWX_JOURNAL_BENCH is not set."
  echo "    Nothing was measured and neither B7 nor B8 has been reported. This harness"
  echo "    offers thousands of transactions a second at a real PostgreSQL and takes"
  echo "    minutes, so it is opt-in rather than part of the ordinary suite. Set"
  echo "    FLOWX_JOURNAL_BENCH=1 and FLOWX_POSTGRES_CONNECTION to run it."
  exit 0
fi

if [ -z "${FLOWX_POSTGRES_CONNECTION:-}" ]; then
  echo "==> FAILED: FLOWX_JOURNAL_BENCH is set and FLOWX_POSTGRES_CONNECTION names no server." >&2
  echo "    This is a failure rather than a skip on purpose: a skip here would report B7" >&2
  echo "    and B8 as measured on a run that opened no connection." >&2
  exit 1
fi

mkdir -p "$(dirname "$RESULTS")"

echo "==> Building the harness"
dotnet build tests/FlowX.JournalBenchmarks/FlowX.JournalBenchmarks.csproj -c Release

echo
echo "==> Running"
dotnet tests/FlowX.JournalBenchmarks/bin/Release/net10.0/FlowX.JournalBenchmarks.dll \
  --json "$RESULTS" "$@"

echo
echo "==> Verdict"
python3 scripts/check-journal-benchmarks.py "$RESULTS"
