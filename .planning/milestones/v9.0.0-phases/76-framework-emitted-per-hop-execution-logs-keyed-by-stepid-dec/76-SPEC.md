# Phase 76: Framework-Emitted Per-Hop Execution Logs Keyed by stepId — Specification

**Created:** 2026-07-16
**Ambiguity score:** 0.19 (gate: ≤ 0.20)
**Requirements:** 10 locked

## Goal

The platform (BaseProcessor.Core + Orchestrator) emits its own per-hop execution telemetry keyed by `stepId`, so the full-stack flow is reconstructable from Elasticsearch for **any** processor — and the resilience verdict stops deriving its binding terms from logs an operator had to write by hand.

## Background

**This is a production observability feature.** "Did this step execute" is platform-level operability, and it currently does not exist. The resilience sweep is a *consumer* of that telemetry, not its justification — an operator must never be required to implement logs in their own concrete processor in order to make the full-stack sweep verifiable.

Current reality in code:

- **The framework emits no happy-path per-hop log.** `BaseProcessor.Core/Processing/` carries only fault-path warnings (`ProcessorPipeline.cs:137,147,195`; `PostProcessConsumer.cs:36`). The *only* evidence a hop succeeded is one author-written line in `SampleProcessor.ProcessAsync` (`src/Processor.Sample/SampleProcessor.cs:89`).
- **The analyzer binds to that author log.** `PassFailEngine.cs:65` hardcodes nine `Step_*` strings as `HopLabels`; `IsComplete` set-compares a run's `DistinctLabels` against it. Those labels are a *config-payload string* the sample logs verbatim (`SampleProcessor.cs:56`, D-10) — a naming convention masquerading as an identity.
- **`attributes.StepId` already reaches ES today.** `InboundExecutionScopeConsumeFilter.cs:28` opens the MEL scope bus-wide via `ExecutionLogScope.BuildState(ec)`, so every log during a consume already carries `StepId`. The analyzer simply never reads it (no `StepIdFieldPath` in `EsIndexNames.cs`).
- **Processor logs alone have no edges.** Hop N stamps its result `EntryId = dr.MessageId` (`OutputTail.cs:89`), but the orchestrator mints a **fresh** MassTransit envelope id for the next step and dispatches with `entryId = messageId` (`RelocateTail.cs:23,44`). So hop N's `MessageId` ≠ hop N+1's `EntryId`. Processor-only logs yield a bag of disconnected `(stepId, entryId, messageId)` tuples — no causal edges, and no expected-set.

Two live findings from Phase 75 motivate the verdict half:

- **TEST-10 (false FAIL):** the processor emitted all 9 hop-logs for all 22 executions; OTLP dropped the `Step_E1/E2` records for 2 on the way to ES. Verdict said `Missing=2`. Real data loss: **zero** — both executions reached terminal `Step_G` with the correct value. The instrument lied.
- **TEST-08 (false PASS):** work the processor never touches emits no trace at all, so a total pre-observability loss reads as PASS.

Both are the same defect: **the verdict treats evidence as truth.** Neither is a recovery defect — the data path has been clean throughout.

**Causality is one-way: observability failure cannot *cause* data loss; it can only manufacture the *appearance* of it.** The data path is RabbitMQ + Redis + the consumers; logs/metrics are fire-and-forget side effects that nothing in the flow reads, waits on, or branches on. The observability tier is a witness, not a participant.

**The two evidence axes are only partially independent.** Per `otel-collector-config.yaml` and `prometheus.yml`: logs go services → OTLP → **otel-collector** → ES; metrics go services → OTLP → **otel-collector** → prometheus exporter `:8889`, which Prometheus *scrapes*. Prometheus scrapes the **collector**, not the services. So an **ES failure** leaves metrics intact (TEST-01's cold-ES first pass: Prom `orch_consumed = proc_sent = 188, gap = 0` while ES returned `StartedRuns=0`), but a **collector failure** blinds both axes at once.

## Requirements

1. **FW-01 — Processor per-hop execution log**: `ProcessorPipeline` emits exactly one structured log per hop execution, for every outcome.
   - Current: no happy-path per-hop log exists in `BaseProcessor.Core/Processing/` — only fault-path warnings
   - Target: one log per hop carrying `StepId`, `ExecutionId`, `CorrelationId`, `EntryId`, `MessageId`, and outcome, in placeholder form only (no `$"..."` interpolation), so the MEL→OTLP bridge surfaces each as an `attributes.*` field
   - Acceptance: hermetic fact with a capturing logger asserts exactly one record per consume with all six fields present as structured state, across Completed / Failed / Cancelled; a processor whose `ProcessAsync` writes **no** log of its own still produces the record

2. **FW-02 — Orchestrator per-fan-out log**: the orchestrator emits a structured record per next-step fan-out, carrying the causal edge.
   - Current: no per-dispatch record links the inbound `entryId` to the next step; the fresh envelope id minted downstream at `RelocateTail.cs` breaks the chain
   - Target: one record per fan-out carrying `CorrelationId`, `ExecutionId`, `WorkflowId`, the inbound `EntryId` consumed, and the next `StepId`
   - **⚠ AMENDED 2026-07-16 (research Discrepancy 1 — Option C):** the original target's sixth field, "outbound `MessageId` dispatched for that step," is DROPPED — the outbound MassTransit envelope id is not in hand at the fan-out loop (`OrchestratorPrePipeline.cs` sends with no id override; the id concretizes only downstream at `RelocateTail`). The inbound `EntryId` (= M_N) satisfies ANL-03's proof-of-execution edge and the next `StepId` satisfies ANL-02's expected set, so the goal is unaffected. See CONTEXT D-11 resolution.
   - Acceptance: hermetic fact asserts one record per next-step; a 2-way fan-out (C→D1,D2) emits exactly 2 records with **distinct next-`StepId`s** (each carrying the correct inbound `EntryId`)

3. **FW-03 — Framework logs carry identities only, never payload**: no blob content in platform telemetry.
   - Current: the sample logs values, safe only because they are synthetic (D-03, "no real/sensitive payload is logged"); the framework logs nothing
   - Target: framework logs carry ids + outcome only — never `validatedData`, `dr.Data`, or `d.Payload`. Blob hash or length is permitted; the blob is not
   - Acceptance: grep proves no framework log template or argument references those three; a fact feeding a sentinel string through a processor's blob asserts the sentinel appears in no framework log record

4. **FW-04 — Observability must remain non-blocking**: the witness never becomes a participant.
   - Current: holds today only because the OTel .NET batch export processor drops on queue-full rather than blocking
   - Target: the per-hop and fan-out logs are emitted on a non-blocking path; a stalled/failed exporter can never apply backpressure into, throw into, or fail a consume
   - Acceptance: fact asserts a throwing/blocking `ILogger` does not fail or delay the hop (pipeline outcome unchanged, no exception surfaces); export config remains batch/drop, asserted structurally

5. **ANL-01 — Completeness re-keyed to `stepId`; `HopLabels` deleted**: structural completeness stops depending on author-chosen strings.
   - Current: `PassFailEngine.cs:65` hardcodes 9 `Step_*` labels; `IsComplete` (`:75`) set-compares `DistinctLabels` against them
   - Target: completeness computed per `(correlationId, executionId)` from `attributes.StepId` on framework records; `HopLabels` removed from the engine
   - Acceptance: `HopLabels` is grep-clean/gone; facts produce correct complete and incomplete verdicts from stepId-keyed framework records containing **zero** `Step_*` labels

6. **ANL-02 — Expected set derived from ES dispatch records (closes TEST-08)**: what *should* have run comes from the orchestrator's own telemetry.
   - Current: expected = the hardcoded 9 labels; work the processor never touches emits nothing, so total pre-observability loss reads PASS
   - Target: expected = the stepIds the orchestrator logged dispatching per `(corr, exec)`; actual = the stepIds a processor logged executing; dispatched-but-never-executed is a binding miss
   - Acceptance: a scenario where a dispatched step never executes yields `Verdict=Fail` (the TEST-08 shape now fails); the expected set is derived from ES only — grep proves no Postgres/graph read on the analyzer path

7. **ANL-03 — Generic telemetry-gap reconciliation (no domain values)**: a dropped record is recoverable from framework evidence alone.
   - Current: `6e90216` reconciles a dropped hop-log **only** via the sample's value oracle (terminal `Step_G == seed + 6`, gated on `seedsByExecution`). A processor without value logs cannot reconcile → dropped log = false FAIL (TEST-10)
   - Target: a hop whose own record is absent is provably-executed from platform evidence — the orchestrator's fan-out record consuming that hop's output `EntryId` proves the hop produced it. Redundancy is a **requirement**, not an emergent property: a hop's execution must be provable two independent ways (its own record, or the orchestrator's downstream record)
   - Acceptance: fact — a run missing hop N's processor record but carrying the orchestrator record that consumed `M_N` reconciles as a non-binding telemetry gap **with no seed oracle supplied**; a run missing **both** records remains a binding miss

8. **ANL-04 — Three-class verdict: absence of evidence is never evidence of absence**: an observability failure must never be reported as data loss.
   - Current: the verdict is binary — `pass = missing == 0 && !dupFail && valueChainOk && metricGateOk` (`PassFailEngine.cs:368`) — so *any* evidence gap becomes FAIL, conflating "the flow broke" with "we couldn't see"
   - Target: three classes — **PASS** (positive evidence the flow completed), **FAIL** (positive evidence of loss), **INCONCLUSIVE / observability-degraded** (evidence insufficient to judge; explicitly not a flow failure and explicitly not a green). FAIL requires evidence of absence, never absence of evidence
   - Acceptance: facts — (a) total trace darkness with self-consistent conservation (the TEST-01 shape: `StartedRuns=0` while `orch_consumed == proc_sent`, gap 0) yields INCONCLUSIVE, not FAIL; (b) partial evidence with a real conservation gap yields FAIL; (c) complete evidence yields PASS

9. **ANL-05 — Metric gate semantics inverted for the blind case**: a dead collector is not a flow failure.
   - Current: `metricGateOk` is binding (`PassFailEngine.cs:360,368`) — a failing metric gate flips green to red
   - Target: a metric gate failing because the metrics tier itself is absent/frozen (collector down — both axes blind, since Prometheus scrapes the collector not the services) yields INCONCLUSIVE; a metric gate failing on a *real* conservation gap with live counters still yields FAIL
   - Acceptance: fact — absent/frozen counters + absent traces → INCONCLUSIVE; live counters showing a genuine `orch_consumed != proc_sent` gap → FAIL

10. **SMP-01 — Author logs are enrichment, never a prerequisite**: the verdict is fully functional with zero concrete-processor logs.
    - Current: the binding half of the verdict (`missing`/`dupFail`/`valueChainOk`) derives entirely from `SampleProcessor.cs:89`
    - Target: the verdict is computed from framework records alone; the sample retains `received/produced` **solely** as a value-integrity oracle (proving the work was *correct*, not merely that it *ran*) — the irreducibly domain-specific part the platform cannot supply
    - Acceptance: analyzer facts produce correct Pass/Fail/Inconclusive from framework records only (no `Step_*` label, no `Received`/`Produced` present); with the value oracle absent the value-chain check degrades to not-applicable rather than failing

## Boundaries

**In scope:**
- `ProcessorPipeline` per-hop execution log (FW-01)
- Orchestrator per-fan-out log carrying the inbound-`entryId` → outbound-`messageId` causal edge (FW-02)
- `StepIdFieldPath` (and any new field-path consts) added to `EsIndexNames`
- Analyzer completeness re-keyed to `stepId`; `HopLabels` deleted (ANL-01)
- Expected-set from ES orchestrator dispatch records (ANL-02)
- Generic, domain-free telemetry-gap reconciliation via framework redundancy (ANL-03)
- Three-class verdict + inverted metric-gate semantics for the blind case (ANL-04/05)
- Sample's `received/produced` retained as optional enrichment (SMP-01)
- Live 7-scenario sweep re-run on a real Docker stack, after the mandatory SourceHash reseed, with results examined

**Out of scope:**
- **Re-keying the value chain to `stepId`** — `ExpectedHopOffset` (`PassFailEngine.cs:407`) maps label → *topological depth* (`Step_G` → 6); depth is a DAG property, and deriving it from the graph is a separate concern. The value chain stays label-keyed and sample-owned.
- **Removing the sample's `received/produced` log** — retained as the domain value oracle; only its *load-bearing* status is removed.
- **Prometheus scraping services directly** (a truly independent metrics axis) — an architectural change; "collector down ⇒ INCONCLUSIVE" is a sound rule without it. Recorded as a future option.
- **Keeper log changes** — Phase 75 already instrumented `attributes.ReinjectOutcome` (`6e52f13`, `2aa63e5`); the keeper-outcome join is unchanged here.
- **Metric instrument changes** — Phase 74 settled the uniform two-counter model; this phase changes only how the *verdict* reads the gate, not the instruments.
- **Adopting `K_EXECUTIONS`** — Phase 75 D75-7 left the seam default-off; unchanged.
- **Postgres/graph reads on the analyzer path** — violates the v8.0.0 "truth = Prometheus + ES only" constraint.
- **Phase 69's atomic index+data write / gated forward cleanup** — separate unplanned phase; untouched here.

## Constraints

- **No raw payload in framework logs** (FW-03). The sample's values are safe only because they are synthetic; a framework logging every input/output blob for every processor is a production data-protection defect.
- **Truth = Prometheus + Elasticsearch only.** The expected set must come from ES dispatch records, never a Postgres/graph read (v8.0.0 milestone constraint).
- **Observability must stay non-blocking** (FW-04) — the one-way causality guarantee (observability cannot cause data loss) holds *only* while the export path drops rather than blocks.
- **Log level must survive export.** The per-hop and fan-out logs must emit at a level the shipped effective log config does not filter before OTLP export, and must be **default-on** — the sweep cannot depend on an env-gated seam. Precedent: Phase 75's deferred "Open-Question-1 level-filter probe" for the keeper's `LogWarning` drop record.
- **Production volume is real.** This is per-hop-per-execution telemetry in production, not test scaffolding. Default-on is required (above), so volume/cost is a genuine design input, not an afterthought.
- **Fan-out shape must survive the re-keying:** `Step_G` logs **twice** per correlationId (per-arrival fan-in, collapsed by DISTINCT) and `Step_A` is the shared Mode-2 entry carrying an **empty** `executionId` (deliberately excluded from `HopLabels` today, `PassFailEngine.cs:54`).
- **Touches production source** (`BaseProcessor.Core`, `Orchestrator`) — framework per-hop logging is justified on its own operability merits, independent of the analyzer.
- **⚠ SourceHash reseed is MANDATORY before the live sweep.** `SourceHash.targets:82` folds `@(ImplFiles)` = `$(MSBuildThisFileDirectory)**\*.cs` (**BaseProcessor.Core's own sources**) + `$(MSBuildProjectDirectory)\**\*.cs` — so editing `ProcessorPipeline.cs` alone **changes `Processor.Sample`'s embedded SourceHash**. Required order after compiling, before `phase-68-sweep.ps1`:
  1. Clean-rebuild **BOTH** configs — `-c Debug` alone leaves a stale Release dll carrying the OLD hash → seeder binds the wrong processor row → `POST /start` **422** even when heal-wait passes: `rm -rf src/Processor.Sample/obj src/Processor.Sample/bin/Release && dotnet build SK_P.sln -c Release --no-incremental`
  2. FK-safe graph DELETE of the 6 workflow tables (the idempotent `FanOutSeeder` GET-matches by name and would not rebind the stale workflow otherwise)
  3. Seed: `BaseApi.Tests.exe --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*"` — `SeedProcessorAsync(hostHash)` creates the processor row for the new hash
  4. Verify liveness keys (`skp:proc:*:*`, ~10s) → `docker compose restart orchestrator` → `POST /api/v1/orchestration/start` must return **204**. The 204 IS the proof of SourceHash currency; a 422 means the hash still diverged
  5. Only then run `scripts/phase-68-sweep.ps1`

## Acceptance Criteria

- [ ] `ProcessorPipeline` emits one per-hop record carrying `StepId`+`ExecutionId`+`CorrelationId`+`EntryId`+`MessageId`+outcome, for Completed/Failed/Cancelled (FW-01)
- [ ] A processor with **no** author-written log still yields a complete structural trace (FW-01, SMP-01)
- [ ] The orchestrator emits one fan-out record per next step carrying inbound `EntryId` + next `StepId` (outbound `MessageId` dropped per Discrepancy-1 Option C); a 2-way fan-out emits exactly 2 with distinct next `StepId`s (FW-02)
- [ ] No framework log template or argument references `validatedData`, `dr.Data`, or `d.Payload` (grep-clean) (FW-03)
- [ ] A throwing/blocking `ILogger` neither fails nor delays a hop (FW-04)
- [ ] `HopLabels` is deleted from `PassFailEngine`; completeness verdicts are produced from stepId-keyed records with zero `Step_*` labels present (ANL-01)
- [ ] A dispatched-but-never-executed step yields `Verdict=Fail` — the TEST-08 blind spot is closed (ANL-02)
- [ ] The expected set is derived from ES only; no Postgres/graph read exists on the analyzer path (grep-clean) (ANL-02)
- [ ] A run missing hop N's processor record, but carrying the orchestrator record that consumed `M_N`, reconciles as a non-binding telemetry gap **with no seed oracle supplied** (ANL-03)
- [ ] A run missing **both** the hop record and its orchestrator record remains a binding miss (ANL-03)
- [ ] Total trace darkness with self-consistent conservation (`StartedRuns=0`, `orch_consumed == proc_sent`, gap 0) yields **INCONCLUSIVE**, not FAIL (ANL-04)
- [ ] Partial evidence with a real conservation gap yields **FAIL**; complete evidence yields **PASS** (ANL-04)
- [ ] Absent/frozen counters + absent traces yield **INCONCLUSIVE**; live counters with a genuine `orch_consumed != proc_sent` gap yield **FAIL** (ANL-05)
- [ ] Analyzer facts produce correct Pass/Fail/Inconclusive from framework records alone (no `Step_*`, no `Received`/`Produced`) (SMP-01)
- [ ] With the value oracle absent, the value-chain check degrades to not-applicable rather than failing (SMP-01)
- [ ] Debug + Release build 0-warning; the analyzer/pipeline/orchestrator hermetic fact suites are GREEN
- [ ] **Live gate:** after the mandatory SourceHash reseed (constraint above) and a `POST /start` → **204**, `scripts/phase-68-sweep.ps1` runs all 7 scenarios on a real Docker stack and the results are examined. Every non-PASS verdict is explained and classified as one of: genuine data loss / telemetry gap / observability-degraded (INCONCLUSIVE) / blind-spot closure now correctly failing

## Ambiguity Report

| Dimension          | Score | Min  | Status | Notes                                                                 |
|--------------------|-------|------|--------|-----------------------------------------------------------------------|
| Goal Clarity       | 0.85  | 0.75 | ✓      | Production feature first; sweep is a consumer, not the justification   |
| Boundary Clarity   | 0.75  | 0.70 | ✓      | Value chain stays label-keyed + sample-owned; direct Prom scrape out   |
| Constraint Clarity | 0.85  | 0.65 | ✓      | No payload, non-blocking, default-on level, SourceHash reseed order    |
| Acceptance Criteria| 0.80  | 0.70 | ✓      | Live sweep re-run + results examined; 16 pass/fail criteria            |
| **Ambiguity**      | 0.19  | ≤0.20| ✓      |                                                                       |

Status: ✓ = met minimum, ⚠ = below minimum (planner treats as assumption)

## Interview Log

| Round | Perspective     | Question summary                                              | Decision locked                                                                                                                              |
|-------|-----------------|---------------------------------------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------|
| 0     | Researcher      | Scout: what emits the evidence the verdict binds to?          | Framework emits no happy-path log; verdict binds to `SampleProcessor.cs:89` + hardcoded `HopLabels`; `attributes.StepId` already reaches ES   |
| 1     | Researcher      | Value chain re-keyed to stepId, or stays label-keyed?         | Stays label-keyed + sample-owned — `ExpectedHopOffset` encodes DAG depth, not identity; out of scope                                          |
| 1     | Researcher      | Is closing the TEST-08 blind spot in scope?                   | Yes — resolved by the reframing below; orchestrator log is required, not a stretch                                                            |
| 1     | Researcher      | Acceptance bar: live sweep or deferred-automated?             | **Live sweep re-run required; results examined.** Not deferred-automated                                                                       |
| 2     | Simplifier      | Framing: is this test scaffolding or a product feature?       | **Production feature.** Operator must never be forced to write logs to make the sweep verifiable; base carries the sweep alone                |
| 2     | Simplifier      | Are concrete-processor logs allowed?                          | Yes — as *enrichment* for a better picture; never a prerequisite (SMP-01)                                                                     |
| 2     | Boundary Keeper | Is the orchestrator log in scope for 76?                      | **Yes.** Processor-only logs have no edges (fresh envelope id at `RelocateTail.cs:23,44`) and no expected-set — goal unreachable without it   |
| 3     | Researcher      | Is all the data in ES to reconstruct the flow?                | Yes at the *emission* layer — query quality is the variable, not missing data. But "emitted" ≠ "in ES" (TEST-10), so redundancy is a **requirement** |
| 4     | Failure Analyst | Observability failure ≠ broken flow — how must the verdict react? | Three-class verdict (ANL-04): FAIL requires positive evidence of loss, never absence of evidence. Metric gate inverted for the blind case (ANL-05) |
| 4     | Failure Analyst | Are Prom and ES independent axes?                             | **Only partially** — Prom scrapes the *collector*, not the services. ES failure ⇒ metrics survive (corroboration works); collector failure ⇒ both blind ⇒ INCONCLUSIVE |
| 4     | Failure Analyst | Does observability failure cause data loss?                   | **No — it cannot cause it, only manufacture its appearance.** Witness, not participant. Holds only while export is non-blocking (FW-04)       |

---

*Phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec*
*Spec created: 2026-07-16*
*Next step: /gsd-discuss-phase 76 — implementation decisions (log shape, level, analyzer query design, verdict-class plumbing)*
