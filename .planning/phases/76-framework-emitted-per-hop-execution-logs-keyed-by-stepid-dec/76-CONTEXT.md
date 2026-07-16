# Phase 76: Framework-Emitted Per-Hop Execution Logs Keyed by stepId — Context

**Gathered:** 2026-07-16
**Status:** Ready for planning

<domain>
## Phase Boundary

The platform (BaseProcessor.Core + Orchestrator) emits its own per-hop execution telemetry keyed by `stepId`, so the full-stack flow is reconstructable from Elasticsearch for **any** processor. The resilience analyzer re-keys structural completeness onto `stepId` (deleting the hardcoded `HopLabels`), derives its expected set from orchestrator dispatch records, reconciles dropped records generically, and gains a third verdict class (**INCONCLUSIVE**) so an observability-tier failure is never reported as data loss. This is a production observability feature; the resilience sweep is a consumer of it, not its justification.

</domain>

<spec_lock>
## Requirements (locked via SPEC.md)

**10 requirements are locked.** See `76-SPEC.md` for full requirements, boundaries, and acceptance criteria. Downstream agents MUST read `76-SPEC.md` before planning or implementing. Requirements are not duplicated here.

**In scope (from SPEC.md):**
- `ProcessorPipeline` per-hop execution log (FW-01)
- Orchestrator per-fan-out log carrying the inbound-`entryId` → outbound-`messageId` causal edge (FW-02)
- `StepIdFieldPath` (and any new field-path consts) added to `EsIndexNames`
- Analyzer completeness re-keyed to `stepId`; `HopLabels` deleted (ANL-01)
- Expected-set from ES orchestrator dispatch records (ANL-02)
- Generic, domain-free telemetry-gap reconciliation via framework redundancy (ANL-03)
- Three-class verdict + inverted metric-gate semantics for the blind case (ANL-04/05)
- Sample's `received/produced` retained as optional enrichment (SMP-01)
- Live 7-scenario sweep re-run on a real Docker stack, after the mandatory SourceHash reseed, with results examined

**Out of scope (from SPEC.md):**
- Re-keying the value chain to `stepId` (label→depth is a DAG property; value chain stays label-keyed, sample-owned)
- Removing the sample's `received/produced` log (retained as value oracle; only its load-bearing status is removed)
- Prometheus scraping services directly (future option; "collector down ⇒ INCONCLUSIVE" is sound without it)
- Keeper log changes / metric instrument changes / adopting `K_EXECUTIONS` / Postgres reads on the analyzer path / Phase 69 work

</spec_lock>

<decisions>
## Implementation Decisions

### Verdict class & exit codes (SPEC ANL-04/05)
- **D-01:** The three verdict classes split on **evidence sufficiency**, not severity — **PASS** (evidence sufficient, flow complete; a non-zero `TelemetryGap` is reported but NON-binding, preserving `6e90216`), **FAIL** (evidence sufficient, positive loss proven), **INCONCLUSIVE** (evidence insufficient to judge either way). INCONCLUSIVE is narrow: it fires only on genuine blindness (total trace darkness with self-consistent conservation — the TEST-01 cold-ES shape), never merely because something is imperfect. TEST-10's reconciled shape stays PASS.
- **D-02:** New harness/sweep exit code **2 = INCONCLUSIVE**. This keeps `0/1/2` as the *verdict* codes and `≥10` as *infra aborts* — extending the existing exit-code table's organizing principle (`phase-67-harness.ps1:31-42`, "an infra abort is never mistaken for a verdict"). 2 is currently unused (table jumps 1 → 10).
- **D-03:** INCONCLUSIVE is **sweep-fatal** — the sweep's final exit stays "0 IFF all selected scenarios PASS", so an inconclusive run is non-zero. "We couldn't prove recovery" is not "recovery works."
- **D-04:** **No auto-retry** on INCONCLUSIVE — D-04's "no auto-retry on any class" discipline holds (`phase-68-sweep.ps1`). The roll-up classifies it with a DISTINCT message identifying it as an instrument failure and stating that a deliberate re-run is the appropriate response (e.g. `INCONCLUSIVE — observability tier blind (StartedRuns=0, conservation intact 188=188); re-run against warm ES`). The operator retries deliberately; the harness never silently greens a run and hides a persistent observability defect. (This is exactly the TEST-01 cold-ES first-pass case, which today mislabels as `VERDICT_FAIL`.)

### Framework log shape & level (SPEC FW-01/03/04)
- **D-05:** **One record per hop, at completion** — no start/end pair. Mid-hop crash detection is already covered by the orchestrator dispatch record (ANL-02 expected-vs-actual): orchestrator dispatch = "should run", processor completion = "did run" + outcome (outcome is only knowable at the end). A start record would double production volume to re-prove what the orchestrator already proves.
- **D-06:** Level **`Information`**. `Debug` is filtered by default in production → would violate SPEC's "default-on, survives export" and reproduce the Phase-75 keeper level-filter risk. A unit of business work executing is Information-grade, not diagnostic chatter.
- **D-07:** **No bespoke volume knob.** SPEC requires default-on, so any knob is opt-out — and MEL's standard category filter already is that opt-out: an operator sets `BaseProcessor.Core` → `Warning` in `Logging:LogLevel`. A custom toggle would duplicate that and add a second way to silently disable the sweep's evidence base. Volume (~per-step-execution) is the system's natural business-event granularity.
- **D-08:** **Emit in `ProcessorPipeline`, NOT `OutputTail`.** `OutputTail` looks like the choke point (all fault paths route through it) but **Mode-2 (entry/seed) skips it entirely** — the pipeline comment: *"req 3: spawn handled everything — skip the framework inline tail."* Siting the log in `OutputTail` would silently miss every entry-step execution. `ProcessorPipeline` sees both modes.
- **D-09:** The **entry step (`Step_A`, `executionId == Guid.Empty`, the source sentinel) IS logged** — the operability feature must have no blind spot — but the analyzer treats **empty-`executionId` records as an entry MARKER**, not a member of any execution's expected set (it would otherwise key to `(corr, Guid.Empty)`, belonging to neither execution's run). This generalizes today's `HopLabels`-excludes-`Step_A` semantics instead of special-casing a label.
- **D-10:** Framework log templates carry **ids + outcome ONLY** — placeholder form (no `$"..."`), never `validatedData` / `dr.Data` / `d.Payload` (FW-03). Six fields per FW-01: `StepId`, `ExecutionId`, `CorrelationId`, `EntryId`, `MessageId`, outcome.

### Orchestrator record shape (SPEC FW-02, ANL-02/03)
- **D-11:** **One record per fan-out EDGE (per next step)**, not one per pre-pipeline run — forced by ANL-03: the record's purpose is the causal edge `inbound EntryId → the specific outbound MessageId minted for THAT next step` (`RelocateTail.cs`). A per-run record listing all next steps would flatten the edges and lose the one→one mapping. Lands inside the orchestrator Pre's existing next-step loop (already iterates next steps + mints a messageId per one): `(CorrelationId, ExecutionId, WorkflowId, inbound EntryId, next StepId, outbound MessageId)`. A 2-way fan-out at C emits exactly 2 (FW-02 acceptance).
- **D-12:** **Terminal-reached record.** `Step_G` has no next steps → zero fan-out records → its only evidence would be its own processor completion record, collapsing ANL-03's two-ways-to-prove at the ONE place it matters most (the convergence point "zero-missing" is measured against). So when the orchestrator Pre resolves next steps and finds NONE, it emits a distinct terminal-reached record: `(CorrelationId, ExecutionId, WorkflowId, inbound EntryId, StepId)` — no outbound messageId. This is the orchestrator independently witnessing "this execution reached its terminal," giving `Step_G` the same redundancy as every other hop and ES-evidencing run *closure* (not just progress) from the orchestrator side.
- **D-13:** Because `Step_G` fans in **twice** per execution (Phase 73 D-06), the orchestrator emits **two** terminal-reached records per `(corr, exec)`, distinguished by inbound `entryId` — the analyzer expects multiplicity 2 there, mirroring today's `Step_G` processor-record rule (D-10, Phase 73). Consistent, not a new special case.
- **D-14:** Emission site: `OrchestratorPrePipeline` / `RelocateTail`, where next-step resolution + messageId minting already happen (same identities-in-hand argument as D-08).

### Analyzer: clean break + layered value oracle (SPEC ANL-01, SMP-01)
- **D-15:** **Clean break for structural completeness — one stepId-keyed path.** After D-05..D-14 the framework records carry everything the structural verdict needs (`StepId`, `ExecutionId`, both evidence directions, terminal closure); `StepLabel` contributes nothing to completeness/duplicates/expected-set. So `HopLabels` is DELETED, the `StepLabel`-keyed `IsComplete` set-comparison is removed, and the structural reader (completeness / expected-set / reconciliation) reads framework records ONLY. No transitional coexistence of two completeness paths — both halves ship in this phase against the same rebuilt stack, so there is nothing to transition *from*; keeping a second `StepLabel`-keyed completeness path would reintroduce the two-sources-of-truth coupling ANL-01 exists to kill.
- **D-16:** **Value oracle stays as a separate, layered axis.** SMP-01 keeps `received/produced` (Phase 73 D-11: `attributes.Received`/`attributes.Produced`, correlated by `StepLabel` → `ExpectedHopOffset` label→depth). This is the ONLY remaining `StepLabel` consumer, and it answers a DIFFERENT question (was the work *correct*, not did it *run*), so it is not a second completeness path. It is present only when the sample's oracle logs exist; **absent → not-applicable, never FAIL** (SMP-01 degrade rule).
- **D-17:** `EsIndexNames`: **add** `StepIdFieldPath`; **keep** `StepLabelFieldPath` / `Received` / `Produced` (value reader still uses them); the *structural* queries stop referencing `StepLabelFieldPath`. Grep-clean check: no `Step_*` literal in the structural path.

### Claude's Discretion
- Exact message-template wording for the three record types (processor per-hop, orchestrator fan-out, orchestrator terminal-reached).
- The `attributes.*` field names for the new orchestrator records (follow the existing `ExecutionLogScope` key convention so the MEL→OTLP bridge surfaces them consistently).
- Internal analyzer structure (how the structural reader and value reader are factored — separate methods/classes vs one pass with two concerns), provided D-15's single-structural-source and D-16's degrade-to-N/A hold.
- Whether the INCONCLUSIVE roll-up message is one line or a short block, provided it names the instrument-failure cause and the deliberate-re-run guidance (D-04).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Locked requirements (read first)
- `.planning/phases/76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec/76-SPEC.md` — Locked requirements, boundaries, acceptance criteria. **MUST read before planning.** (10 reqs: FW-01..04, ANL-01..05, SMP-01)

### Prior-phase context this phase depends on / reverses
- `.planning/phases/73-verify-and-instrument-end-to-end-l2-data-delivery-across-a-f/73-CONTEXT.md` — D-07 (`entryId`/`messageId` framework-owned + framework off-limits — **REVERSED here**, the framework is now in scope), D-03 (no framework-level payload logging — **reinforced** as FW-03), D-06/D-10/D-11 (`Step_G` fan-in multiplicity 2; value-chain via `attributes.Received`/`Produced`)
- `.planning/phases/74-reshape-business-metrics-into-a-uniform-two-counter-model/74-CONTEXT.md` — uniform two-counter model the metric gate (ANL-05) reads; unchanged here
- `milestones/v8.0.0-ROADMAP.md` — "truth = Prometheus + Elasticsearch ONLY" constraint (no Postgres/graph read on the analyzer path)

### Source-of-truth code (integration points — verify current line numbers at plan time)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — processor per-hop log site (D-08); both Mode-1/Mode-2 visible here
- `src/BaseProcessor.Core/Processing/OutputTail.cs` — NOT the log site (Mode-2 skips it, D-08); `EntryId = dr.MessageId` stamping (`:89`)
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` / `src/Orchestrator/Dispatch/RelocateTail.cs` — orchestrator fan-out + terminal-reached log site (D-11/D-12/D-14); fresh envelope-id mint (`RelocateTail.cs:23,44`)
- `src/Messaging.Contracts/ExecutionLogScope.cs` — the 5-key scope bridge (`StepId` already surfaces as `attributes.StepId`); `InboundExecutionScopeConsumeFilter.cs:28` opens it bus-wide
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` — `HopLabels` (`:65`, DELETE per D-15), `ExpectedHopOffset` (`:407`, value oracle, KEEP per D-16), verdict `pass` gate (`:368`), `metricGateOk` (`:360`)
- `tests/BaseApi.Tests/Observability/Analysis/AnalyzerReport.cs` — `enum Verdict` (`:24`, add INCONCLUSIVE per D-01)
- `tests/BaseApi.Tests/Observability/Helpers/EsIndexNames.cs` — add `StepIdFieldPath`, keep `StepLabelFieldPath`/`Sum`/`Received`/`Produced` (D-17)
- `scripts/phase-67-harness.ps1` (exit-code table `:31-42`) / `scripts/phase-68-sweep.ps1` (roll-up class switch `:80`) — exit-code 2 + INCONCLUSIVE class (D-02/D-03/D-04)
- `src/BaseProcessor.Core/SourceHash.targets` (`:82` `@(ImplFiles)` fold) — the reason the SourceHash reseed is MANDATORY before the live sweep (SPEC constraint)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`ExecutionLogScope.BuildState` + `InboundExecutionScopeConsumeFilter`** — `attributes.StepId` ALREADY reaches ES for every processor consume; the analyzer just never read it. The processor per-hop log rides the same bridge; the new orchestrator records should use the same `ExecutionLogScope` key convention.
- **The `6e90216` TELEMETRY-GAP reconciliation** — the non-binding-gap machinery already exists; ANL-03 generalizes its evidence source from the sample value oracle to framework redundancy, and D-01 keeps its non-binding-on-PASS behavior.
- **Exit-code table discipline** (`phase-67-harness.ps1`) — verdict codes vs infra-abort codes are already separated; INCONCLUSIVE=2 extends it without restructuring.

### Established Patterns
- **Placeholder-only log templates** (Phase 75 D75-2/D75-4 precedent) — structured args surface as `attributes.*` via the MEL→OTLP bridge ONLY in `{Placeholder}` form, never `$"..."`. FW-03's no-payload rule rides this.
- **Capturing-logger hermetic facts** (`CapturingLogger<T>`, Phase 75 `5517a58`) — the proven way to assert a log record's structured fields without a live stack. FW-01/FW-02/FW-03/FW-04 acceptance facts should reuse it.
- **`Step_G` multiplicity-2 fan-in handling** (Phase 73 D-06/D-10) — already modeled in the analyzer; D-13 mirrors it onto the orchestrator terminal-reached record.

### Integration Points
- Processor log → `ProcessorPipeline` (both modes; NOT `OutputTail`).
- Orchestrator fan-out + terminal-reached logs → `OrchestratorPrePipeline`/`RelocateTail` next-step loop.
- Analyzer structural reader → framework records (stepId-keyed); value reader → sample records (StepLabel-keyed), layered + degradable.
- Live gate → SourceHash reseed (rebuild BOTH configs → graph-delete → seed → 204) → `phase-68-sweep.ps1`.

</code_context>

<specifics>
## Specific Ideas

- The one-way causality principle is the spine of the verdict model: **observability failure cannot cause data loss, only manufacture its appearance** — the observability tier is a witness, not a participant. This holds ONLY while export stays non-blocking (FW-04); a blocking/synchronous exporter would apply backpressure into the consume path and become a participant. Keep this as a standing constraint whenever the export pipeline is touched.
- The two distortions are mirror images of the same root defect (verdict treats evidence as truth): TEST-10 = trace loss → false FAIL (completed work looks missing); TEST-08 = trace absence → false PASS (untouched work is invisible). ANL-03 closes the first generically; ANL-02's expected-set closes the second.
- Prom and ES are only PARTIALLY independent: Prometheus scrapes the **collector** (`:8889`), not the services, so an ES failure leaves metrics intact (corroboration works — the TEST-01 cold-ES case) but a **collector** failure blinds both axes → INCONCLUSIVE. Direct service scraping (true independence) is explicitly out of scope, recorded as a future option.

</specifics>

<deferred>
## Deferred Ideas

- **Prometheus scraping each service's `/metrics` directly** — would make the metrics axis truly independent of the collector (so a collector failure no longer blinds both axes). Architectural change; out of scope for 76. "Collector down ⇒ INCONCLUSIVE" is a sound rule without it.
- **Framework-level value/correctness telemetry** — the platform can prove a hop *ran* but not that its output was semantically *correct* (that is irreducibly domain knowledge, left to the concrete processor's enrichment logs). No path to genericize this; noted only to mark the boundary.

None of these block Phase 76.

</deferred>

---

*Phase: 76-framework-emitted-per-hop-execution-logs-keyed-by-stepid-dec*
*Context gathered: 2026-07-16*
