---
phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
plan: 04
subsystem: observability
tags: [observability, auditor, seeder, value-chain, convergent-terminal, fan-in, live-es, trip-duration, sweep-script]

# Dependency graph
requires:
  - phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
    plan: 01
    provides: "ES-attribute contract field names attributes.Received / attributes.Produced (the auditor reads Produced as the per-step surfaced value)"
  - phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
    plan: 03
    provides: "Extended RunTrace.FromLabels(corr, exec, labels, values) + PassFailEngine.Analyze(... tripDurationMsByExecution, tripDurationMsByCorrelation, seedsByExecution) + AnalyzerReport.ValueChainOk / TripDurationMsBy{Execution,Correlation}"
provides:
  - "Seeder data extended to the G-extended DAG: 10 steps / 10 edges (adds Step_F1->Step_G + Step_F2->Step_G) / 10 assignments, Step_G the lone zero-outgoing sink, every payload number=1 (uniform +1 per hop)"
  - "RealStack auditor fixture reads attributes.Produced into a per-label Values map, threads it into the extended FromLabels, retains Step_G x2, computes per-(corr,exec) + per-corr trip duration from the ES @timestamp min->max span (no new ES query), and feeds the extended Analyze (trip maps + per-exec seed oracle)"
  - "Live terminal-anchor PROXY: the Step_G completed-terminal ES log at seed+6 (106/206) is the live anchor; the fixture is ES-read-only and performs NO Redis skp:out: read"
  - "scripts/phase-73-sweep.ps1 — operator-runnable, parse-valid live sweep (seed -> round-trip -> audit) with no fail-fast and a phase-73-summary.json roll-up; live Docker run deferred-automated"
affects: ["live ES auditor verdict", "operator phase-73 bring-up sweep"]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Live value-chain proof wired to real ES: the proven pure model (Plan 03) is driven by parsed attributes.Produced + @timestamp from real hits — green RealStack run is trustworthy, not vacuous"
    - "ES-log terminal value as a durable-anchor proxy (D-11): Step_G's seed+6 completed-terminal log stands in for the Redis skp:out: blob the ES-read-only auditor cannot read (durable-blob proof owned by hermetic Plan 02)"
    - "Trip duration from the already-pulled, time-sorted hits: min->max @timestamp span per (corr,exec) and per corr, reusing the scored cohort — no second ES query (D-12)"
    - "Defensive tolerant-numeric-attribute read mirrored verbatim: TryReadProduced copies TryReadSum's TryGetProperty + Number/String parse, dropping odd-shaped/missing attributes rather than throwing (T-73-07)"
    - "Deferred-automated operator sweep: a parse-valid PowerShell driver documents the live Docker stage as a non-gate placeholder so the phase closes on hermetic GREEN + 0-warning, not on a sandbox-unavailable live run"

key-files:
  created:
    - "scripts/phase-73-sweep.ps1 - operator-runnable live sweep: stage 1 seeds the G-extended DAG (FanOutSeeder), stage 2 (live round-trip) is deferred-automated (no Docker; not a gate), stage 3 invokes the RealStack auditor; no fail-fast; rolls up analyzer-reports/phase-73-summary.json"
  modified:
    - "tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs - G-extended DAG data (NodeNumbers uniform 1 incl. Step_G, two convergence edges, regex incl. G, reverse-topo G-first create, self-verify 10/10/10 + G-lone-sink) [committed a7f14cc by prior executor]"
    - "tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs - TryReadProduced + TryReadTimestamp defensive readers, BuildRunTraces returns a TraceCohort (4-arg FromLabels with per-label Produced values, @timestamp trip-duration maps, per-exec seed oracle), extended Analyze feed; ES-read-only, no metric-counter assertion"

key-decisions:
  - "Task 1 (seeder, a7f14cc) was committed by the prior executor and verified sound here (10/10/10, Step_G lone sink, F1/F2 each 1 outgoing edge -> G, NodeNumbers fully uniform 1) — NOT re-committed"
  - "Per-exec seed oracle recovered explicitly as Produced[Step_B] - 1 (the +1-per-hop chain) and passed as seedsByExecution; a run that never surfaced Step_B is omitted so the engine falls back to its own ResolveSeed recovery"
  - "Step_G's two arrivals carry the identical terminal value, so a last-write into the per-label Values map is correct (both equal seed+6); the engine treats Step_G x2 as legitimate"
  - "The sweep's round-trip stage is a documented no-op placeholder (deferred-automated) — operators wire it to live bring-up; the script parses but is not exercised against Docker here"

patterns-established:
  - "Integration layer wires the pure model to real telemetry while preserving the auditor's ES-read-only / defensive-parse posture (no Redis read, no new ES query, no metric-counter assertion)"
  - "Operator sweep documents its deferred live stage explicitly so it is parse-valid tooling without becoming a phase gate"

requirements-completed: [SPEC-R1, SPEC-R3, SPEC-R5, SPEC-R6, SPEC-R7]

# Metrics
duration: ~10min (continuation)
completed: 2026-06-17
---

# Phase 73 Plan 04: Live Seeder + RealStack Auditor Value-Chain + Operator Sweep Summary

**The live half is landed: the in-test seeder now builds the G-extended DAG (10/10/10, `Step_G` the lone convergent sink, every payload `number = 1`), the RealStack auditor reads `attributes.Produced` + the ES `@timestamp` trip-duration span into the Plan-03 extended `FromLabels` / `Analyze` (Step_G x2 retained, the `seed+6` Step_G ES log = the live terminal-anchor proxy, ES-read-only — no Redis read), and `scripts/phase-73-sweep.ps1` gives operators a parse-valid seed->round-trip->audit sweep whose live Docker run is deferred-automated. 0-warning Debug + Release.**

## Continuation Context

This plan was resumed from a partial prior-executor state:
- **Task 1** was already COMPLETE & COMMITTED (`a7f14cc`) — verified sound here, not re-committed.
- **Task 2** had ~145 lines of UNCOMMITTED, contract-correct edits in the working tree — reviewed, confirmed against the 73-01 (`attributes.Produced`) and 73-03 (`FromLabels` / `Analyze` / `AnalyzerReport`) contracts, confirmed compiling 0-warning Debug + Release, then COMMITTED (`5b89e2a`).
- **Task 3** was NOT STARTED — `scripts/phase-73-sweep.ps1` created and committed (`d5caff6`).

## Accomplishments

- **Seeder (Task 1, verified):** `FanOutSeederE2ETests` builds the 10-step / 10-edge / 10-assignment DAG with `Step_G` the lone zero-outgoing sink reachable from both `Step_F1` and `Step_F2` (each now carrying exactly one outgoing edge -> G), every `NodeNumbers` value uniform `1` (incl. `Step_G`), the label regex extended to include `G`, the reverse-topo create making `Step_G` first, and the self-verify asserting `10/10/10` + the G-sink check.
- **Auditor (Task 2):** `TryReadProduced` mirrors `TryReadSum` verbatim (T-73-07 defensive: drops odd-shaped/missing `Produced`, never throws). `BuildRunTraces` now returns a `TraceCohort` carrying the per-instance `RunTrace`s (built via the 4-arg `FromLabels` with the per-label `Produced` values), the per-`(corr,exec)` + per-`corr` trip-duration maps from the `@timestamp` min->max span (reusing the already-pulled, time-sorted hits — no new ES query), and a per-exec seed oracle (`Produced[Step_B] - 1`). These feed the extended `PassFailEngine.Analyze(... tripDurationMsByExecution, tripDurationMsByCorrelation, seedsByExecution)`. The fixture remains ES-read-only (no Redis `skp:out:` read) with no metric-counter assertion; the Prom path, drain, poll-to-stable, and window-pin are untouched.
- **Sweep (Task 3):** `scripts/phase-73-sweep.ps1` is a thin run-stages-and-collect driver analogous to `phase-68-sweep.ps1`: stage 1 seeds the G-extended DAG (`FanOutSeeder`), stage 2 (live round-trip) is documented `DEFERRED-AUTOMATED` (no Docker; not a phase gate), stage 3 invokes the RealStack auditor (`Category=RealStack&FullyQualifiedName~Analyzer`). No fail-fast; reads + tabulates the analyzer report (Verdict/Missing/Duplicates/ValueChainOk) and rolls up `analyzer-reports/phase-73-summary.json`. Keeps the NEVER-auto-retry / NEVER-re-score anti-pattern guards. Parse-valid (`PARSE_OK`).

## Task Commits

1. **Task 1: Seeder G-extended DAG (10/10/10) + number=1** - `a7f14cc` (feat) — *committed by prior executor; verified sound, not re-committed*
2. **Task 2: RealStack auditor reads attributes.Produced + @timestamp trip duration** - `5b89e2a` (feat)
3. **Task 3: operator-runnable phase-73 live sweep (deferred-automated)** - `d5caff6` (feat)

## Verification

- Seeder: `Step_F1->Step_G` / `Step_F2->Step_G` edges present, `["Step_G"] = 1`, `NodeNumbers` fully uniform `1`, regex `^Step_(A|B|C|D1|E1|F1|D2|E2|F2|G)$`, reverse-topo `node["Step_G"] = ... null` first, `Assert.Equal(10, ...)` on steps/edges/assignments/workflow_assignments/edges.Count/seenLabels, sink check = `Step_G` lone zero-outgoing (F1/F2 each 1 outgoing -> G).
- Auditor: `TryReadProduced` present; `FromLabels(... valuesByInstance[...])` 4-arg call; trip-duration maps from `@timestamp` (`WindowTimestampFieldPath`) reusing the existing hits; extended `Analyze` named-arg call (`tripDurationMsByExecution` / `tripDurationMsByCorrelation` / `seedsByExecution`) matching the 73-03 signature exactly; no new metric-counter assertion; no Redis read.
- Sweep: `phase-73-sweep` / `FanOutSeeder` / `Category=RealStack&FullyQualifiedName~Analyzer` markers present; deferred/no-Docker/not-a-phase-gate documented; NEVER-auto-retry / NEVER-re-score guards present; PowerShell `Parser::ParseFile` returns `PARSE_OK`.
- **Build:** `dotnet build tests/BaseApi.Tests` 0-warning under BOTH `-c Debug -warnaserror` AND `-c Release -warnaserror`.

## Threat Mitigations Applied

- **T-73-07 (DoS / robustness — the new `TryReadProduced` ES-attribute reader):** mirrors the existing defensive `TryReadSum` (`TryGetProperty` + tolerant Number/String parse, never throws) so an odd-shaped or missing `attributes.Produced` is dropped from the value map, not fatal — preserving the fixture's T-66-09 defensive-parse posture. `TryReadTimestamp` is equally defensive (unparseable/missing `@timestamp` is skipped).
- **T-73-08 (Information Disclosure — sweep + payloads):** accepted — the sweep interpolates no secrets, feeds only hardcoded `dotnet test` filters, and the operator-supplied `-ScenarioId` names only the per-scenario report file (no shell/sql interpolation). Seeded payloads carry only the synthetic integer `1`.
- **T-73-09 (Tampering — seeded DAG shape):** the seeder self-verify (10/10/10 + G-sink + uniform `number = 1`) fails closed if the seeded graph drifts from the locked shape, giving the live auditor's value chain a trustworthy substrate.

## Deviations from Plan

None — the plan executed exactly as written. Task 1 was pre-committed by the prior executor and verified sound (no defect found, so no re-commit); Task 2's uncommitted edits matched the 73-01 / 73-03 contracts exactly and compiled 0-warning, so they were committed as-is; Task 3 was created per the `phase-68-sweep.ps1` analog.

## Known Stubs

None. The sweep's round-trip stage (stage 2) is an intentional, documented `DEFERRED-AUTOMATED` placeholder (the live Docker run is not a phase gate — the phase closes on hermetic GREEN from Plans 02/03 + 0-warning), not an unwired stub. The trip-duration maps and value map are fully wired from real ES hits in the live fixture.

## Self-Check: PASSED

- FOUND: tests/BaseApi.Tests/Orchestrator/FanOutSeederE2ETests.cs (Step_F1->Step_G, Step_G uniform 1, G-sink check)
- FOUND: tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs (TryReadProduced, 4-arg FromLabels, trip-duration @timestamp maps, extended Analyze)
- FOUND: scripts/phase-73-sweep.ps1 (phase-73-sweep, FanOutSeeder, RealStack auditor filter, deferred-automated, PARSE_OK)
- FOUND commits: a7f14cc, 5b89e2a, d5caff6
- BUILD: 0-warning Debug + Release (-warnaserror)

---
*Phase: 73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f*
*Completed: 2026-06-17*
