---
phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec
plan: 05
status: complete
verdict: live-gate-passed
requirements: [FW-01, FW-02, FW-03, FW-04, ANL-01, ANL-02, ANL-03, ANL-04, ANL-05, SMP-01]
completed: 2026-07-16
---

# 76-05 — Live acceptance gate: reseed + 7-scenario sweep

## Outcome

**PASSED (after a live-gate-surfaced regression was root-caused and fixed): 7/7 PASS.**

The mandatory SourceHash reseed proved currency (`POST /start` → 204), the first sweep
surfaced a false-FAIL regression this phase introduced (1/7 PASS), the regression was
root-caused against live Elasticsearch and fixed, and the re-run sweep is **7/7 PASS** on a
real Docker stack. The live acceptance gate did exactly its job.

## Task 1 — reseed to 204

Editing `ProcessorPipeline.cs` (BaseProcessor.Core) changes `Processor.Sample`'s embedded
SourceHash, AND the container bakes that hash at `docker build` time (Dockerfile runs
`dotnet publish` in-image). Sequence:

1. Host clean-rebuild BOTH configs (`--no-incremental`) — 0-warning.
2. `docker compose build` — rebuilt processor-sample/orchestrator/keeper from new source.
3. `phase-65-up.ps1` — 10 service types healthy.
4. Post-rebuild the new hash has no processor row → `phase-65-reset.ps1` heal-wait aborts (expected). FLUSHALL had run; did manual graph-DELETE → seed (`FanOutSeeder`, host hash `0555d345…`) → liveness (2 keys) → restart orchestrator.
5. Activation `POST /start` with body `["<v8-fanout-proof wfId>"]` → **204** (a bare POST returns 415).

(Note: `phase-67-harness.ps1` STEP A0 rebuilds images itself per run, so the sweep self-manages SourceHash currency — the manual reseed above was the pre-sweep gate.)

## Task 2 — sweep classification + regression fix

### First sweep: 1/7 PASS — regression detected

TEST-02..07 all false-FAILed. Every FAIL: `Missing=0`, `startedRuns==completeRuns`, and the
ONLY duplicated stepId was the terminal fan-in **Step_G** in 100% of runs — the SPEC-line-116
convergent terminal ("Step_G logs twice per correlationId, per-arrival fan-in") mis-flagged as
an illegitimate duplicate. Classified as a **regression introduced by phase 76** (not one of
the four acceptable non-PASS classes).

### Root cause (confirmed against live ES)

OTel `IncludeScopes=true` + the bus-wide `InboundExecutionScopeConsumeFilter` stamp
`attributes.StepId` (= the CONSUMED step) onto **every** log emitted during a consume. So the
analyzer's structural query (`exists attributes.StepId`) swept up the orchestrator fan-out
records, the sample's author value logs, and orchestrator business logs. The
`no-MessageId ⇒ terminal-reached` discriminator (`AnalyzerE2ETests.cs`) then dumped all their
consumed-step ids into `terminalStepIds`, so `tset.Count` was ~9 (never 1) → the convergent
terminal id resolved to `null` → `RunTrace.FromStepIds` could not exempt the legitimate
Step_G ×2. Live ES proof: of 26 no-MessageId StepId records per run, 10 were fan-out records
(carrying `attributes.StepId` from scope + `NextStepId`); only 6 were true `"terminal reached"`
records; the rest were author/business logs. The hermetic facts pass a fixed
`ConvergentStepId="hop-g"`, so 44/44 stayed green — the gap was purely on the live ES path.

### Fix (`3319bb5`)

Derive the convergent fan-in terminal from the FW-02 **dispatch** records (positively keyed by
`attributes.NextStepId`, immune to scope pollution): the unique `NextStepId` dispatched-to more
than once per `(corr,exec)` (Step_F1→G and Step_F2→G). Proven live: in every exec exactly one
`NextStepId` (Step_G) is dispatched ×2 and it equals the processor's per-arrival ×2 stepId. The
polluted `terminalStepIds` bucket is removed; the `proven`-set contribution is retained
unchanged (over-population can only mask loss, never manufacture it — a separate,
fault-scenario-verified concern, flagged below).

### Second sweep: 7/7 PASS

| TEST-01 | TEST-02 | TEST-03 | TEST-04 | TEST-05 | TEST-06 | TEST-07 |
|---------|---------|---------|---------|---------|---------|---------|
| PASS | PASS | PASS | PASS | PASS | PASS | PASS |

Every scenario green on a real Docker stack. `Duplicates: 0` confirmed (the Step_G false-flag
is gone); `Missing=0`, `startedRuns==completeRuns` throughout. Debug + Release builds 0-warning;
hermetic analyzer facts unaffected.

## Follow-up (flagged, not blocking)

The same scope-pollution over-populates the ANL-03 `proven` set (every scope-stamped stepId is
added). It caused no failure here (all runs complete, nothing to reconcile), and over-population
can only *mask* a real loss (false PASS), never manufacture one — so it cannot cause a false
FAIL. But it weakens ANL-03's loss-detection under fault and should get its own fix, verified
against fault scenarios that genuinely drop a hop. Recommend a follow-up plan. The live
`BuildRunTraces` derivation is `Category=RealStack`-only; the passing sweep is its regression
gate — a future refactor to make it unit-testable is advisable.
