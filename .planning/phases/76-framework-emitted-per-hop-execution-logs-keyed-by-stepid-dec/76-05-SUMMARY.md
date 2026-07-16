---
phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec
plan: 05
status: blocked
verdict: live-gate-failed
requirements: [FW-01, FW-02, FW-03, FW-04, ANL-01, ANL-02, ANL-03, ANL-04, ANL-05, SMP-01]
completed: 2026-07-16
---

# 76-05 — Live acceptance gate: reseed + 7-scenario sweep

## Outcome

**Task 1 (mandatory SourceHash reseed) — PASSED.** `POST /start` returned **204**, proving container hash == seeder host-build hash after a BaseProcessor.Core source edit.

**Task 2 (live 7-scenario sweep + classification) — the gate did NOT pass: 1/7 PASS.** The sweep surfaced a **regression introduced by this phase** (the stepId re-key of duplicate detection). Six of seven scenarios false-FAIL. This is the live acceptance gate doing exactly its job — catching a defect the hermetic facts could not.

## Task 1 — reseed to 204 (how currency was proven)

Editing `ProcessorPipeline.cs` (BaseProcessor.Core) changes `Processor.Sample`'s embedded SourceHash, AND the container bakes that hash at `docker build` time (Dockerfile runs `dotnet publish` in-image), so **both** host binaries and container images had to be rebuilt. Order executed:

1. Host clean-rebuild BOTH configs — `rm -rf src/Processor.Sample/obj bin/Release` → `dotnet build SK_P.sln -c Release --no-incremental` → `-c Debug --no-incremental`. Both **0-warning, 0-error**.
2. `docker compose build` — rebuilt `processor-sample`, `orchestrator`, `keeper` from new source (baseapi-service layer-cached: it carries no processor SourceHash).
3. `phase-65-up.ps1` — 10 service types healthy, processor-sample replicas:2.
4. `phase-65-reset.ps1` heal-wait **aborted** (expected: post-rebuild, the new hash has no processor row yet, so replicas can't resolve `procId` → no liveness key). FLUSHALL (STEP 1) had already run.
5. Manual graph-DELETE (FK-safe, 6 tables) → seed (`FanOutSeeder_SeedsAndSelfVerifies`, host hash `0555d345…c71ce`, self-verify GET 200) → liveness reconverged (2 keys ~2s) → `docker compose restart orchestrator` → healthy.
6. Activation: `POST /api/v1/orchestration/start` with body `["<v8-fanout-proof wfId>"]` → **204**. (A bare POST returns 415, not 422 — the endpoint requires the workflow-id JSON array body.)

## Task 2 — sweep roll-up (CAPSTONE: 1/7 PASS)

| Scenario | Verdict | Missing | started==complete | effectOnce | Classification |
|----------|---------|---------|-------------------|------------|----------------|
| TEST-01  | Pass    | 0       | 0==0              | true       | PASS (0 runs read — likely cold-ES/stale-report; not a FAIL) |
| TEST-02  | Fail    | 0       | 18==18            | **false**  | **False FAIL — Step_G fan-in regression** |
| TEST-03  | Fail    | 0       | 15==15            | **false**  | **False FAIL — Step_G fan-in regression** |
| TEST-04  | Fail    | 0       | 18==18            | **false**  | **False FAIL — Step_G fan-in regression** |
| TEST-05  | Fail    | 0       | 28==28            | **false**  | **False FAIL — Step_G fan-in regression** |
| TEST-06  | Fail    | 0       | 16==16            | **false**  | **False FAIL — Step_G fan-in regression** |
| TEST-07  | Fail    | 0       | 19==19            | **false**  | **False FAIL — Step_G fan-in regression** |

Roll-up artifact: `analyzer-reports/phase-68-summary.json`. Harness exit codes are the source of truth (all FAILs = exit 1 = VERDICT_FAIL).

## Per-scenario classification of every non-PASS

All six FAILs are the SAME defect, classified as a **fifth category not in the four-way scheme: a regression introduced by phase 76** (neither genuine data loss, nor telemetry gap, nor INCONCLUSIVE, nor blind-spot closure).

**Evidence it is a false positive, not genuine duplication:**
- `Missing=0` and `StartedRuns==CompleteRuns` in all six — no data loss, every started run completed.
- The ONLY duplicated stepId is the terminal fan-in **Step_G**, in **100% of runs across all six scenarios** (18/18, 15/15, 18/18, 28/28, 16/16, 19/19). A genuine fault-induced redelivery would scatter across whichever steps were redelivered and vary run-to-run; a uniform, exactly-×2, Step_G-only signature is the SPEC-line-116 convergent terminal ("Step_G logs twice per correlationId — per-arrival fan-in, collapsed by DISTINCT").
- The value/label oracle view is intact (`Labels`/`DistinctLabels` = 9, single coherent value chain Step_B=201…Step_G=206). Only the new framework-record stepId axis double-counts Step_G.

**Root cause (localized):**
- `RunTrace.FromStepIds(…, convergentStepId)` exempts the Step_G ×2 only when `convergentStepId` is supplied; the doc is explicit: `null ⇒ any repeated stepId is an illegitimate duplicate (fail-closed)` (`RunTrace.cs:194`).
- The live derivation `AnalyzerE2ETests.cs:644` — `convergent = terminalStepIds.TryGetValue(key, out tset) && tset.Count == 1 ? tset.First() : null` — resolves to **null** on live data, so `HasIllegitimateDuplicate` fires on the legitimate Step_G ×2.
- `terminalStepIds` is built from the orchestrator FW-02 terminal-reached records (`OrchestratorPrePipeline.cs:106`). The hermetic facts pass a fixed `ConvergentStepId="hop-g"` and stay 44/44 green, which is exactly why the unit suite could not catch this — the gap is purely on the live ES-fed path.
- Exact null-cause not yet confirmed (needs live ES, which the harness tears down per scenario): (a) terminal-reached records not in ES / not matched by `BuildStepSearchBody`, (b) `(corr,exec)` key mismatch, or (c) `tset.Count != 1`. Root-causing needs a single-scenario re-run with teardown suppressed.

## Self-Check: FAILED (live gate not passed)

The phase's live acceptance criterion (all non-PASS explained as one of the four acceptable classes) is **not** met — the six FAILs are a regression this phase introduced, which is a blocking gap, not an acceptable class. Waves 1–2 (plans 76-01..04) are correct and committed; the defect is confined to the live convergent-terminal derivation in the analyzer fixture (owned by 76-03).

## Recommended next step

Gap closure: root-cause the live `convergent` null (re-run one scenario with `docker compose down` teardown suppressed, query ES for the terminal-reached records' `(corr, exec, StepId)`), fix the derivation at `AnalyzerE2ETests.cs:644` (and/or make `FromStepIds` derive the convergent terminal from the observed stepId multiplicity so it degrades safely), then re-run `phase-68-sweep.ps1` and confirm the Step_G ×2 no longer trips `HasIllegitimateDuplicate`.
