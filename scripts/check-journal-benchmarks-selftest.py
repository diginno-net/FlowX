#!/usr/bin/env python3
"""The gate can fail, and can refuse — asserted rather than assumed.

    ./scripts/check-journal-benchmarks-selftest.py

scripts/check-journal-benchmarks.py is the only thing that turns a run of the journal
harness into a verdict, so it is the only thing that can turn a run which measured nothing
into a green job. A gate nobody has seen fail is an assumption
(docs/benchmarks/README.md §5), and the same is true of a refusal.

Every case below is a synthetic document fed to the real `analyse`. Nothing here talks to a
database, so this runs anywhere and in under a second; what it exercises is the verdict
logic and nothing else. Exits 0 when every case holds, 1 on the first that does not.
"""

from __future__ import annotations

import argparse
import copy
import importlib.util
import pathlib
import sys

CHECKER = pathlib.Path(__file__).with_name("check-journal-benchmarks.py")


def load_checker():
    """Import the checker by path, because its filename is not an identifier."""
    spec = importlib.util.spec_from_file_location("check_journal_benchmarks", CHECKER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    return module


def defaults() -> argparse.Namespace:
    """The command line the checker is normally run with."""
    return argparse.Namespace(
        b7_ms=None,
        b7_rate=None,
        b8_ms=None,
        b8_depth=None,
        rate_tolerance=0.95,
        markdown=False,
    )


def document() -> dict:
    """A run that met both budgets at the rate and depth they are judged at."""
    return {
        "schemaVersion": 1,
        "recordedAt": "2026-08-01T00:00:00.0000000+00:00",
        "budgets": {
            "b7": {"p99Ms": 15.0, "commitsPerSecond": 5000},
            "b8": {"p99Ms": 8.0},
        },
        "parameters": {"steps": 3, "payloadBytes": 256},
        "machine": {"processorCount": 4, "postgres": "PostgreSQL 16.13"},
        "b7": {
            "budgetArmRate": 5000,
            "arms": [
                arm7(1000, 990.0, 4.0),
                arm7(5000, 4990.0, 11.0),
            ],
        },
        "b8": {
            "budgetDepth": 1,
            "arms": [arm8(1, 3.0), arm8(16, 5.0)],
        },
    }


def arm7(offered: float, achieved: float, p99: float) -> dict:
    return {
        "offeredCommitsPerSecond": offered,
        "achievedCommitsPerSecond": achieved,
        "commitSamples": 30000,
        "flowsStarted": 10000,
        "flowsCompleted": 10000,
        "flowsFailed": 0,
        "commitMs": {"p50": p99 / 3, "p95": p99 * 0.9, "p99": p99, "max": p99 * 2},
    }


def arm8(depth: int, p99: float) -> dict:
    return {
        "committedSteps": depth,
        "instances": 400,
        "rehydrationSamples": 400,
        "instancesLeftUnfinished": 0,
        "rehydrationMs": {"p50": p99 / 3, "p95": p99 * 0.9, "p99": p99, "max": p99 * 2},
    }


def cases():
    """Every case, as (name, mutate, expected overall, expected per-budget verdicts)."""

    def unchanged(_: dict) -> None:
        pass

    def b7_over_budget(run: dict) -> None:
        run["b7"]["arms"][1]["commitMs"]["p99"] = 15.001

    def b7_over_budget_below_rate(run: dict) -> None:
        run["b7"]["arms"][1]["commitMs"]["p99"] = 40.0
        run["b7"]["arms"][1]["achievedCommitsPerSecond"] = 300.0

    def b7_rate_not_sustained(run: dict) -> None:
        run["b7"]["arms"][1]["achievedCommitsPerSecond"] = 4749.0

    def b7_rate_just_sustained(run: dict) -> None:
        run["b7"]["arms"][1]["achievedCommitsPerSecond"] = 4750.0

    def b7_no_arm_at_the_rate(run: dict) -> None:
        run["b7"]["arms"] = [run["b7"]["arms"][0]]

    def b7_measured_nothing(run: dict) -> None:
        run["b7"]["arms"][1]["commitSamples"] = 0

    def b8_over_budget(run: dict) -> None:
        run["b8"]["arms"][0]["rehydrationMs"]["p99"] = 8.001

    def b8_deep_arm_over_budget(run: dict) -> None:
        run["b8"]["arms"][1]["rehydrationMs"]["p99"] = 22.0

    def b8_short_population(run: dict) -> None:
        run["b8"]["arms"][0]["rehydrationSamples"] = 399

    def b8_measured_nothing(run: dict) -> None:
        run["b8"]["arms"] = []

    def b8_left_an_instance_running(run: dict) -> None:
        run["b8"]["arms"][0]["instancesLeftUnfinished"] = 1

    def both_fail_and_refuse(run: dict) -> None:
        b8_over_budget(run)
        b7_measured_nothing(run)

    return [
        ("a run that met both budgets passes", unchanged, "PASS", ("PASS", "PASS")),
        ("a commit p99 one microsecond over budget fails",
         b7_over_budget, "FAIL", ("FAIL", "PASS")),
        ("a commit p99 over budget at a fraction of the rate still fails",
         b7_over_budget_below_rate, "FAIL", ("FAIL", "PASS")),
        ("a p99 within budget at a rate the node did not sustain is refused, not passed",
         b7_rate_not_sustained, "INCONCLUSIVE", ("INCONCLUSIVE", "PASS")),
        ("and 95 % of the offered rate is sustaining it",
         b7_rate_just_sustained, "PASS", ("PASS", "PASS")),
        ("a run with no arm at the budget's rate is refused",
         b7_no_arm_at_the_rate, "INCONCLUSIVE", ("INCONCLUSIVE", "PASS")),
        ("a run that recorded no commit is refused, not passed",
         b7_measured_nothing, "INCONCLUSIVE", ("INCONCLUSIVE", "PASS")),
        ("a rehydration p99 over budget fails", b8_over_budget, "FAIL", ("PASS", "FAIL")),
        ("a deeper arm over budget is reported and does not change the verdict",
         b8_deep_arm_over_budget, "PASS", ("PASS", "PASS")),
        ("an arm that rehydrated fewer instances than it prepared is refused",
         b8_short_population, "INCONCLUSIVE", ("PASS", "INCONCLUSIVE")),
        ("a run with no rehydration arm is refused", b8_measured_nothing,
         "INCONCLUSIVE", ("PASS", "INCONCLUSIVE")),
        ("an arm that left an instance unfinished is refused, however fast it read",
         b8_left_an_instance_running, "INCONCLUSIVE", ("PASS", "INCONCLUSIVE")),
        ("a failure outranks a refusal", both_fail_and_refuse, "FAIL",
         ("INCONCLUSIVE", "FAIL")),
    ]


def main() -> int:
    checker = load_checker()
    failures: list[str] = []

    for name, mutate, expected, (b7, b8) in cases():
        run = document()
        mutate(run)

        report = checker.analyse(run, defaults())
        actual = (report["verdicts"][0]["verdict"], report["verdicts"][1]["verdict"])

        if report["overall"] != expected or actual != (b7, b8):
            failures.append(
                f"{name}: expected {expected} ({b7}, {b8}), got {report['overall']} "
                f"({actual[0]}, {actual[1]})")
            continue

        # A refusal or a failure that says nothing is a verdict a reader cannot act on.
        for verdict in report["verdicts"]:
            if verdict["verdict"] != "PASS" and not verdict["reasons"]:
                failures.append(f"{name}: {verdict['budget']} is {verdict['verdict']} with no reason.")

        print(f"  ok  {name}")

    # The deeper-arm case is the only one whose evidence is a note rather than a verdict.
    noted = document()
    noted["b8"]["arms"][1]["rehydrationMs"]["p99"] = 22.0

    if not checker.analyse(noted, defaults())["notes"]:
        failures.append("a deeper arm over budget produced no note, so it was measured and lost.")
    else:
        print("  ok  a deeper arm over budget is named in the report")

    # Exit codes are the contract a CI job reads; asserting the map is asserting the gate.
    if checker.EXIT != {"PASS": 0, "FAIL": 1, "INCONCLUSIVE": 2}:
        failures.append(f"exit codes are {checker.EXIT}, not 0 PASS / 1 FAIL / 2 INCONCLUSIVE.")
    else:
        print("  ok  exit codes are 0 PASS, 1 FAIL, 2 INCONCLUSIVE")

    print()

    if failures:
        for failure in failures:
            print(f"FAILED: {failure}", file=sys.stderr)

        return 1

    print("The checker fails when it should, refuses when it must, and says why.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
