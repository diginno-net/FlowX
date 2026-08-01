#!/usr/bin/env python3
"""Turn a journal benchmark run's JSON into a verdict — PASS, FAIL or INCONCLUSIVE — for B7 and B8.

    ./scripts/check-journal-benchmarks.py b7-b8-journal.json
    ./scripts/check-journal-benchmarks.py run-2.json --against docs/benchmarks/B7-B8-journal.json

docs/14-Performance.md §1.1 states the two budgets this gates:

    B7   Durable step commit (Postgres, group commit)    p99  15 ms @ 5 000 commits/s/node
    B8   Flow instance rehydration from journal          p99   8 ms

B7's budget is a pair, not a number. "15 ms" alone is not the budget and a run that
measured 15 ms at 400 commits/s has not met it — it has measured something else. So the
load is half of what is judged, and a run that could not offer or sustain the rate is
refused rather than passed. B8 names no load and is judged on its p99 alone.


Why a run can be refused rather than passed
-------------------------------------------

The same discipline as scripts/check-chaos-qr2.py, for the same reason: a harness that
reports a measurement which never happened as a green job is worse than no harness. Five
conditions return INCONCLUSIVE rather than PASS:

  * the run has no arm at the rate the budget names, so nothing was measured at it;
  * that arm recorded no commit samples at all;
  * the node did not sustain the offered rate, so the p99 belongs to a smaller load than
    the budget names — reported with both numbers, never rounded up to a pass;
  * B8's budget arm rehydrated fewer instances than it prepared, so the population being
    quoted is not the population that was measured;
  * B8's budget arm recorded no rehydration samples.

A p99 **over** budget is a FAIL whichever rate it was measured at, and that asymmetry is
deliberate: latency under a queueing system is monotone in offered load to a first
approximation, so a node that misses 15 ms at 2 000 commits/s does not meet it at 5 000.
The reverse does not hold, which is why the under-rate case is refused instead.

Exit codes: 0 PASS, 1 FAIL, 2 INCONCLUSIVE — the same three-way convention as
scripts/check-chaos-qr2.py and scripts/analyse-scale-samples.py.
"""

from __future__ import annotations

import argparse
import json
import sys

EXIT = {"PASS": 0, "FAIL": 1, "INCONCLUSIVE": 2}

# How much of the offered rate a node has to actually sustain before its p99 counts as a
# p99 "at" that rate. 5 % rather than 0 % because an open-loop generator that arms a timer
# per arrival loses a fraction of a percent to scheduling on any machine, and refusing a
# run for that would be refusing every run.
DEFAULT_RATE_TOLERANCE = 0.95


def judge_b7(document: dict, budget_ms: float, budget_rate: float, tolerance: float) -> dict:
    """Reduce the commit arms to a verdict on B7 and the reasons for it."""
    section = document.get("b7") or {}
    arms = section.get("arms") or []

    arm = next(
        (candidate for candidate in arms
         if abs(candidate.get("offeredCommitsPerSecond", 0) - budget_rate) < 1e-9),
        None,
    )

    if arm is None:
        offered = ", ".join(
            f"{candidate.get('offeredCommitsPerSecond')}" for candidate in arms) or "none"

        return refusal(
            "B7",
            f"no arm was run at {budget_rate:g} commits/s, which is the rate the budget "
            f"names. Rates measured: {offered}.")

    samples = arm.get("commitSamples", 0)

    if samples == 0:
        return refusal(
            "B7",
            f"the arm at {budget_rate:g} commits/s recorded no commit at all, so nothing "
            f"about B7 was measured.")

    p99 = arm["commitMs"]["p99"]
    achieved = arm.get("achievedCommitsPerSecond", 0.0)

    detail = (
        f"p99 {p99:.3f} ms against {budget_ms:g} ms, at {achieved:.1f} of "
        f"{budget_rate:g} commits/s offered, over {samples} commits")

    if p99 > budget_ms:
        return {
            "budget": "B7",
            "verdict": "FAIL",
            "detail": detail,
            "reasons": [
                f"the durable step commit's p99 is {p99:.3f} ms, over the {budget_ms:g} ms "
                f"budget. Measured at {achieved:.1f} commits/s; a rate below the budget's "
                f"{budget_rate:g} cannot rescue this, because more load does not make a "
                f"commit faster."
            ],
            "arm": arm,
        }

    if achieved < budget_rate * tolerance:
        return {
            "budget": "B7",
            "verdict": "INCONCLUSIVE",
            "detail": detail,
            "reasons": [
                f"the node sustained {achieved:.1f} commits/s of the {budget_rate:g} the "
                f"budget names, so {p99:.3f} ms is a p99 at {achieved:.1f} commits/s and "
                f"not at {budget_rate:g}. Reported rather than passed: the budget is a "
                f"latency AND a rate, and only one of them was reached."
            ],
            "arm": arm,
        }

    return {"budget": "B7", "verdict": "PASS", "detail": detail, "reasons": [], "arm": arm}


def judge_b8(document: dict, budget_ms: float, depth: int | None) -> dict:
    """Reduce the rehydration arms to a verdict on B8 and the reasons for it."""
    section = document.get("b8") or {}
    arms = section.get("arms") or []

    if not arms:
        return refusal("B8", "the run has no rehydration arm, so nothing was measured.")

    wanted = section.get("budgetDepth") if depth is None else depth

    arm = next(
        (candidate for candidate in arms if candidate.get("committedSteps") == wanted),
        None,
    )

    if arm is None:
        measured = ", ".join(f"{candidate.get('committedSteps')}" for candidate in arms)

        return refusal(
            "B8",
            f"no arm rehydrated instances with {wanted} committed step(s), which is the "
            f"depth this run's verdict is taken at. Depths measured: {measured}.")

    samples = arm.get("rehydrationSamples", 0)

    if samples == 0:
        return refusal(
            "B8",
            f"the arm at {wanted} committed step(s) rehydrated nothing, so the resume path "
            f"never ran.")

    prepared = arm.get("instances", 0)

    if samples < prepared:
        return refusal(
            "B8",
            f"only {samples} of {prepared} prepared instances were rehydrated, so the arm "
            f"did not measure the population it reports.")

    unfinished = arm.get("instancesLeftUnfinished", 0)

    if unfinished:
        return refusal(
            "B8",
            f"{unfinished} instance(s) were still Pending, Running or Compensating when the "
            f"arm finished, so they were read back and not resumed. FlowRecoveryScan counts "
            f"a takeover by whether this node became the writer, not by how the flow ended, "
            f"so a fast read of an instance that then failed would otherwise be quoted as a "
            f"met budget.")

    p99 = arm["rehydrationMs"]["p99"]

    detail = (
        f"p99 {p99:.3f} ms against {budget_ms:g} ms, over {samples} rehydrations of "
        f"instances carrying {wanted} committed step(s)")

    if p99 > budget_ms:
        return {
            "budget": "B8",
            "verdict": "FAIL",
            "detail": detail,
            "reasons": [
                f"rehydrating an instance takes {p99:.3f} ms at p99, over the "
                f"{budget_ms:g} ms budget, at the shallowest history this run measured."
            ],
            "arm": arm,
        }

    return {"budget": "B8", "verdict": "PASS", "detail": detail, "reasons": [], "arm": arm}


def refusal(budget: str, reason: str) -> dict:
    """A budget whose measurement did not happen, which is not the same as one that failed."""
    return {
        "budget": budget,
        "verdict": "INCONCLUSIVE",
        "detail": "not measured",
        "reasons": [reason],
        "arm": None,
    }


def deeper_arms_over_budget(document: dict, budget_ms: float, verdict: dict) -> list[str]:
    """B8 arms outside the one the verdict is taken at that are over the budget anyway."""
    section = document.get("b8") or {}
    judged = verdict.get("arm") or {}
    notes: list[str] = []

    for arm in section.get("arms") or []:
        if arm is judged or arm.get("rehydrationSamples", 0) == 0:
            continue

        p99 = arm["rehydrationMs"]["p99"]

        if p99 > budget_ms:
            notes.append(
                f"an instance carrying {arm['committedSteps']} committed step(s) "
                f"rehydrates in {p99:.3f} ms at p99, over the {budget_ms:g} ms budget. "
                f"Reported, not gated: the verdict above is taken at the shallowest "
                f"history, so this is what the budget costs as a history grows.")

    return notes


def analyse(document: dict, args: argparse.Namespace) -> dict:
    budgets = document.get("budgets") or {}
    b7_budget = budgets.get("b7") or {}
    b8_budget = budgets.get("b8") or {}

    b7_ms = args.b7_ms if args.b7_ms is not None else b7_budget.get("p99Ms", 15.0)
    b7_rate = args.b7_rate if args.b7_rate is not None else b7_budget.get("commitsPerSecond", 5000)
    b8_ms = args.b8_ms if args.b8_ms is not None else b8_budget.get("p99Ms", 8.0)

    verdicts = [
        judge_b7(document, b7_ms, b7_rate, args.rate_tolerance),
        judge_b8(document, b8_ms, args.b8_depth),
    ]

    overall = "PASS"

    if any(v["verdict"] == "FAIL" for v in verdicts):
        overall = "FAIL"
    elif any(v["verdict"] == "INCONCLUSIVE" for v in verdicts):
        overall = "INCONCLUSIVE"

    return {
        "verdicts": verdicts,
        "overall": overall,
        "notes": deeper_arms_over_budget(document, b8_ms, verdicts[1]),
        "budgets": {"b7Ms": b7_ms, "b7Rate": b7_rate, "b8Ms": b8_ms},
        "document": document,
    }


def render(report: dict, markdown: bool) -> None:
    document = report["document"]
    parameters = document.get("parameters") or {}
    machine = document.get("machine") or {}
    bullet = "- " if markdown else "  "

    print()
    print("B7 and B8 — the durable step commit and the rehydration, against a real PostgreSQL")
    print(
        f"  {parameters.get('steps')} step(s) per flow, {parameters.get('payloadBytes')} B "
        f"payload, {parameters.get('measureSeconds')}s window after "
        f"{parameters.get('warmupSeconds')}s warmup, pool {parameters.get('maxPoolSize')}")
    print(
        f"  recorded {document.get('recordedAt')} on {str(machine.get('postgres'))[:48]}, "
        f"{machine.get('processorCount')} cores, load {machine.get('loadAverage')}")
    print()

    print("  --- B7: durable step commit ---")

    for arm in (document.get("b7") or {}).get("arms") or []:
        commit = arm.get("commitMs") or {}
        print(
            f"{bullet}offered {arm['offeredCommitsPerSecond']:>6g}/s -> achieved "
            f"{arm['achievedCommitsPerSecond']:>8.1f}/s | commit p50 "
            f"{fmt(commit.get('p50'))} p95 {fmt(commit.get('p95'))} p99 "
            f"{fmt(commit.get('p99'))} max {fmt(commit.get('max'))} | "
            f"{arm['commitSamples']} commits, {arm.get('flowsFailed', 0)} flow(s) failed")

    print()
    print("  --- B8: flow instance rehydration ---")

    for arm in (document.get("b8") or {}).get("arms") or []:
        rehydration = arm.get("rehydrationMs") or {}
        print(
            f"{bullet}{arm['committedSteps']:>4} committed step(s) | rehydrate p50 "
            f"{fmt(rehydration.get('p50'))} p95 {fmt(rehydration.get('p95'))} p99 "
            f"{fmt(rehydration.get('p99'))} max {fmt(rehydration.get('max'))} | "
            f"{arm['rehydrationSamples']} of {arm['instances']} instance(s)")

    print()

    for verdict in report["verdicts"]:
        print(f"{bullet}{verdict['budget']}: {verdict['verdict']} — {verdict['detail']}")

        for reason in verdict["reasons"]:
            print(f"{bullet}  {verdict['verdict']}: {reason}")

    for note in report["notes"]:
        print(f"{bullet}  NOTE: {note}")

    print()
    print(f"VERDICT: {report['overall']}")


def render_comparison(current: dict, previous: dict) -> None:
    """Print what a re-run of the same commit did to each figure."""
    print()
    print("  --- the same measurement, twice ---")
    print("  A p99 quoted once is not a measurement. These are the two runs' figures and")
    print("  the disagreement between them, which is the noise floor this document has.")
    print()

    for label, this, that in paired(current, previous):
        if that == 0:
            continue

        drift = (this - that) / that * 100.0
        print(f"  {label:<44} {that:>9.3f} -> {this:>9.3f} ms   {drift:+7.1f} %")

    print()


def paired(current: dict, previous: dict):
    """Every p99 both documents measured, matched by arm."""
    for arm in (current.get("b7") or {}).get("arms") or []:
        rate = arm["offeredCommitsPerSecond"]
        other = next(
            (candidate for candidate in (previous.get("b7") or {}).get("arms") or []
             if candidate["offeredCommitsPerSecond"] == rate),
            None,
        )

        if other is not None:
            yield f"B7 commit p99 @ {rate:g}/s", arm["commitMs"]["p99"], other["commitMs"]["p99"]

    for arm in (current.get("b8") or {}).get("arms") or []:
        depth = arm["committedSteps"]
        other = next(
            (candidate for candidate in (previous.get("b8") or {}).get("arms") or []
             if candidate["committedSteps"] == depth),
            None,
        )

        if other is not None:
            yield (
                f"B8 rehydrate p99 @ {depth} committed",
                arm["rehydrationMs"]["p99"],
                other["rehydrationMs"]["p99"],
            )


def fmt(value: float | None) -> str:
    return "—" if value is None else f"{value:7.3f} ms"


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Render a B7/B8 verdict from a journal benchmark run's JSON.")
    parser.add_argument("results", help="JSON written by the journal benchmark harness")
    parser.add_argument("--against", default=None,
                        help="a second run to print the re-run spread against")
    parser.add_argument("--b7-ms", type=float, default=None,
                        help="override B7's p99 budget in milliseconds (default: the document's)")
    parser.add_argument("--b7-rate", type=float, default=None,
                        help="override B7's commits/s (default: the document's)")
    parser.add_argument("--b8-ms", type=float, default=None,
                        help="override B8's p99 budget in milliseconds (default: the document's)")
    parser.add_argument("--b8-depth", type=int, default=None,
                        help="take B8's verdict at this committed-step depth")
    parser.add_argument("--rate-tolerance", type=float, default=DEFAULT_RATE_TOLERANCE,
                        help="fraction of the offered rate that counts as sustaining it")
    parser.add_argument("--markdown", action="store_true",
                        help="render the per-arm lines as markdown bullets")

    args = parser.parse_args()

    with open(args.results, encoding="utf-8") as handle:
        document = json.load(handle)

    report = analyse(document, args)
    render(report, args.markdown)

    if args.against is not None:
        with open(args.against, encoding="utf-8") as handle:
            render_comparison(document, json.load(handle))

    return EXIT[report["overall"]]


if __name__ == "__main__":
    sys.exit(main())
