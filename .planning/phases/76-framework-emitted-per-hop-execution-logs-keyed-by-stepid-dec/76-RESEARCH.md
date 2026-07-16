# Phase 76: Framework-Emitted Per-Hop Execution Logs Keyed by stepId — Research

**Researched:** 2026-07-16
**Domain:** .NET MEL→OTLP structured logging + MassTransit consume pipeline + ES-binding resilience analyzer (test project)
**Confidence:** HIGH for the integration map (all sites read from live source this session); MEDIUM on the one architectural drift (outbound-MessageId mint site) which requires an orchestrator decision, not more research.

## Summary

Every locked decision in 76-SPEC.md / 76-CONTEXT.md was checked against live code this session. The framework-log half (FW-01..04) and the analyzer half (ANL-01..05, SMP-01) both land in well-understood, already-instrumented seams: the processor per-hop record lands in `ProcessorPipeline.RunAsync` (confirmed both Mode-1 and Mode-2 pass through it; `OutputTail` is correctly ruled out because Mode-2 returns `null` before the tail), and the analyzer re-key lands entirely inside `tests/BaseApi.Tests/Observability/` (`PassFailEngine` + the `BuildRunTraces` ES reader in `AnalyzerE2ETests`). The MEL→OTLP bridge that already surfaces `attributes.StepId` is confirmed live (`ExecutionLogScope` + `InboundExecutionScopeConsumeFilter`, both bus-wide), and the `CapturingLogger<T>` hermetic pattern exists and works exactly as CONTEXT describes.

**One material discrepancy** must be resolved by the orchestrator before planning the FW-02 fan-out record: D-11/D-14 assume the orchestrator Pre next-step loop "mints a messageId per one," but the live code (`OrchestratorPrePipeline.cs:126-145`) sends `NextStepHandoff` with **no MessageId override** — MassTransit assigns the outbound envelope id opaquely, and it only becomes a concrete value downstream in `RelocateTail.RunAsync` (as its `messageId` parameter). So the "outbound MessageId dispatched for that step" is **not in hand** at the fan-out loop. Three viable resolutions are laid out below; the ANL-03 redundancy edge itself (the inbound `EntryId` = M_N consumed) *is* fully in hand at the Pre loop, so the core goal is reachable regardless of which is chosen.

**Two secondary design points** (not discrepancies): (a) the true terminal outcome for the normal path is decided *inside* `OutputTail` (Completed→Failed output-schema downgrade at `OutputTail.cs:57-59`), so a pipeline-sited record logs the pre-tail outcome unless `OutputTail` surfaces the resolved outcome back; and (b) `CapturingLogger<T>` records only explicit placeholder args, **not** ambient scope — so all six FW-01 fields (and all FW-02 fields) must be **explicit `{Placeholder}` args**, even though most also ride ambient scope in production.

**Primary recommendation:** Emit the processor per-hop record at the terminal branches of `ProcessorPipeline.RunAsync` with all six ids as explicit `Information`-level placeholders; resolve the FW-02 outbound-MessageId site decision first; re-key the analyzer's `BuildRunTraces` structural reader and `PassFailEngine` verdict to a new `attributes.StepId`-keyed framework-record path while leaving the `attributes.StepLabel`/`Produced` value oracle untouched.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions (D-01..D-17 — verbatim intent)
- **D-01:** Three verdict classes split on **evidence sufficiency**, not severity — **PASS** (evidence sufficient, flow complete; non-zero `TelemetryGap` reported but NON-binding, preserving `6e90216`), **FAIL** (evidence sufficient, positive loss proven), **INCONCLUSIVE** (evidence insufficient). INCONCLUSIVE is narrow: genuine blindness only (total trace darkness + self-consistent conservation = TEST-01 cold-ES). TEST-10's reconciled shape stays PASS.
- **D-02:** New sweep/harness exit code **2 = INCONCLUSIVE**; keeps `0/1/2` as verdict codes, `≥10` as infra aborts (2 currently unused, table jumps 1→10).
- **D-03:** INCONCLUSIVE is **sweep-fatal** — final exit stays "0 IFF all selected scenarios PASS."
- **D-04:** **No auto-retry** on INCONCLUSIVE; roll-up emits a DISTINCT instrument-failure message advising a deliberate re-run.
- **D-05:** **One record per hop, at completion** — no start/end pair (orchestrator dispatch record covers "should run"; outcome only knowable at end).
- **D-06:** Level **`Information`** (Debug is filtered by default → violates default-on/survives-export).
- **D-07:** **No bespoke volume knob** — MEL category filter (`BaseProcessor.Core` → `Warning`) is the standard opt-out.
- **D-08:** **Emit in `ProcessorPipeline`, NOT `OutputTail`** (Mode-2 skips `OutputTail`).
- **D-09:** Entry step (`Step_A`, `executionId == Guid.Empty`) **IS logged**, but the analyzer treats empty-executionId records as an entry **MARKER**, not a member of any execution's expected set.
- **D-10:** Framework templates carry **ids + outcome ONLY**, placeholder form, never `validatedData`/`dr.Data`/`d.Payload`. Six fields: `StepId`, `ExecutionId`, `CorrelationId`, `EntryId`, `MessageId`, outcome.
- **D-11:** **One record per fan-out EDGE (per next step)**: `(CorrelationId, ExecutionId, WorkflowId, inbound EntryId, next StepId, outbound MessageId)`. 2-way fan-out at C emits exactly 2.
- **D-12:** **Terminal-reached record** when the orchestrator resolves NO next steps: `(CorrelationId, ExecutionId, WorkflowId, inbound EntryId, StepId)` — no outbound messageId.
- **D-13:** `Step_G` fans in **twice** per execution → **two** terminal-reached records per `(corr, exec)`, distinguished by inbound `entryId`; analyzer expects multiplicity 2 there.
- **D-14:** Emission site: `OrchestratorPrePipeline` / `RelocateTail` (next-step resolution + messageId minting).
- **D-15:** **Clean break** for structural completeness — one stepId-keyed path; `HopLabels` DELETED, `StepLabel`-keyed `IsComplete` removed, structural reader reads framework records ONLY.
- **D-16:** **Value oracle stays** as a separate, layered axis (`Received`/`Produced` correlated by `StepLabel` → `ExpectedHopOffset`); absent → not-applicable, never FAIL.
- **D-17:** `EsIndexNames`: **add** `StepIdFieldPath`; **keep** `StepLabelFieldPath`/`Received`/`Produced`; structural queries stop referencing `StepLabelFieldPath`.

### Claude's Discretion
- Exact message-template wording for the three record types (processor per-hop, orchestrator fan-out, orchestrator terminal-reached).
- The `attributes.*` field names for the new orchestrator records (follow the `ExecutionLogScope` key convention).
- Internal analyzer structure (how the structural reader and value reader are factored), provided D-15's single-structural-source and D-16's degrade-to-N/A hold.
- Whether the INCONCLUSIVE roll-up message is one line or a short block, provided it names the instrument-failure cause and the deliberate-re-run guidance.

### Deferred Ideas (OUT OF SCOPE)
- Prometheus scraping each service's `/metrics` directly (true independent metrics axis).
- Framework-level value/correctness telemetry (irreducibly domain knowledge).
- Re-keying the value chain to `stepId`; removing the sample's `received/produced`; keeper log changes; metric instrument changes; `K_EXECUTIONS`; Postgres/graph reads on the analyzer path; Phase 69 work.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| FW-01 | `ProcessorPipeline` emits one structured per-hop record for every outcome | Integration map §1; all six ids in hand at `RunAsync`; emission-point branch map resolves the "exactly one" requirement |
| FW-02 | Orchestrator per-fan-out record carrying the causal edge | Integration map §3; ⚠ outbound-MessageId mint-site discrepancy + 3 resolution options |
| FW-03 | Framework logs carry identities only, never payload | Placeholder-only rule confirmed (D-10 / Phase-75 precedent); grep target = `validatedData`/`dr.Data`/`d.Payload` |
| FW-04 | Observability must remain non-blocking | Export path = OTLP default **batch** processor (drop-on-full); hermetic guard needed on the new log calls (see Pitfall 4) |
| ANL-01 | Completeness re-keyed to `stepId`; `HopLabels` deleted | `BuildRunTraces` (structural reader) + `PassFailEngine.HopLabels`/`IsComplete` located exactly; re-key surface §Analyzer re-key |
| ANL-02 | Expected set derived from ES orchestrator dispatch records | New ES query for FW-02 records; grep proves no Postgres/graph on analyzer path (already ES-only) |
| ANL-03 | Generic telemetry-gap reconciliation via framework redundancy | Generalizes `6e90216` value-oracle gate at `PassFailEngine.cs:185-209`; inbound `EntryId`=M_N proves hop ran |
| ANL-04 | Three-class verdict | `enum Verdict` (`AnalyzerReport.cs:24`) add INCONCLUSIVE; verdict gate `PassFailEngine.cs:368` |
| ANL-05 | Metric gate inverted for the blind case | `metricGateOk` (`PassFailEngine.cs:360`) + `mg1Binding` seam; blind = frozen counters + absent traces |
| SMP-01 | Author logs are enrichment, never a prerequisite | Value oracle already gated (`valueOracleSupplied`, `PassFailEngine.cs:161`); degrade-to-N/A already partially modeled |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Per-hop execution record | Processor framework (`BaseProcessor.Core`) | — | The pipeline is the only site that sees both Mode-1 and Mode-2 and knows the outcome |
| Fan-out causal-edge record | Orchestrator (`Orchestrator.Dispatch`) | — | Only the orchestrator resolves next steps + mints/dispatches the outbound envelope |
| Terminal-reached record | Orchestrator (`OrchestratorPrePipeline`) | — | Only the orchestrator detects "no next steps" (true terminal) |
| MEL→OTLP field bridge | `Messaging.Contracts` (`ExecutionLogScope`) + `BaseConsole.Core` filter | — | Bus-wide scope already surfaces ids as `attributes.*` |
| Structural completeness verdict | Test analyzer (`PassFailEngine` + `BuildRunTraces`) | — | Pure arbiter; reads ES only (v8.0.0 constraint) |
| Value/correctness oracle | Sample processor log → analyzer value reader | — | Irreducibly domain-specific; stays label-keyed & sample-owned (D-16) |
| Sweep verdict-class plumbing | `scripts/phase-67-harness.ps1` + `phase-68-sweep.ps1` | — | Exit-code table owns verdict/infra separation |

## Verified Integration Map

Every file:line below was read live this session. "In hand" = the identity is a local/parameter at that exact site.

### §1 Processor per-hop record — `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs`

**Method:** `public async Task RunAsync(EntryStepDispatch d, Guid messageId, CancellationToken ct)` (`:58`).

**Identities in hand at the call site (all six FW-01 fields present):**
| FW-01 field | Source at `RunAsync` | Notes |
|-------------|----------------------|-------|
| `StepId` | `d.StepId` | on the dispatch |
| `WorkflowId` | `d.WorkflowId` | on the dispatch (bonus) |
| `ProcessorId` | `d.ProcessorId` / `context.Id!.Value` | on the dispatch (bonus) |
| `ExecutionId` | `d.ExecutionId` | **`Guid.Empty` for the Mode-2 entry step (D-09 marker)** |
| `CorrelationId` | `d.CorrelationId` | on the dispatch |
| `EntryId` | `d.EntryId` | `Guid.Empty` (source sentinel) for the entry step |
| `MessageId` | `messageId` (method param) | **NOT in `ExecutionLogScope`** — must be an explicit placeholder |
| outcome | resolved per branch (see below) | |

**Emission-point branch map (this is the real design work for "exactly one record per hop execution, for every outcome"):** `RunAsync` has ~7 exit points. Emit the per-hop record on the branches that represent an actual executed hop; deliberately skip the non-execution / transient branches.

| Line | Branch | Outcome | Emit per-hop record? |
|------|--------|---------|----------------------|
| `:82` | `if (!exists.Value) return;` clean-absent | — (no work ran) | **No** (not one of Completed/Failed/Cancelled) |
| `:81`,`:92` | gate/read exhaust → REINJECT + return | infra escalation | **No** (transient, not a terminal outcome) |
| `:105-106` | input-schema fail → `outputTail.RunAsync(failDr,…)` | **Failed** | **Yes** |
| `:138-139` | seam `ProcessStatusException` → tail | **Failed / Cancelled** (Processing = transient) | **Yes** for Failed/Cancelled; **No** for Processing |
| `:153-154` | unexpected exception → tail | **Failed** | **Yes** |
| `:157` | `if (dr is null) return;` Mode-2 spawn handled | **Completed** (entry step ran) | **Yes** — this is the D-09 entry marker record |
| `:162-173` | normal completion via `outputTail.RunAsync(carried,…)` | **Completed** (may be forced Failed inside tail) | **Yes** (see ⚠ Design Point B) |

**Recommended implementation shape:** a private `LogHopExecuted(EntryStepDispatch d, Guid messageId, string outcome)` helper (wrapped in a swallow — Pitfall 4) called at each of the 5 "Yes" branches, OR a resolved `string? outcome` local set on each terminal branch with a single `finally`-guarded emission that skips when `outcome is null` (clean-absent/reinject/Processing leave it null). Either satisfies FW-01's "exactly one per consume, framework-emitted even when `ProcessAsync` writes no log."

**SPEC line-drift (minor, non-material):** SPEC calls `:137,147,195` "fault-path warnings"; live: `:137` is `LogInformation` (Processing status), `:147` and `:195` are `LogWarning`. No impact on the plan.

### §2 `OutputTail` is correctly NOT the site — `src/BaseProcessor.Core/Processing/OutputTail.cs`

- `EntryId = dr.MessageId` stamping **confirmed at `:89`** (the `StepCompleted` arm; the Failed/Cancelled arms at `:91`,`:94` stamp it too). This is why the orchestrator's inbound `m.EntryId` == the upstream hop's `MessageId` (M_N) — the backbone of ANL-03.
- Mode-2 skip **confirmed**: `ProcessorPipeline.cs:157` `if (dr is null) return;` with comment "req 3: Mode-2 spawn handled everything; skip the tail." D-08 is correct — siting the log here would miss every entry-step execution.
- **⚠ Design Point B (outcome resolution):** the final terminal outcome for the normal path is decided **inside** `OutputTail.RunAsync` — `OutputTail.cs:57-59` downgrades `Completed → Failed` when the output blob fails schema validation. The pipeline only holds `carried.Result` (pre-tail). To log the *true* terminal outcome at the D-08 pipeline site, either (a) have `OutputTail.RunAsync` return the resolved outcome (it currently returns `bool proceed`), or (b) accept that the pipeline-sited record logs the pre-validation outcome (optimistic Completed even when the wire `StepFailed` is sent). This is an edge (output-schema-forced-Failed only) but affects FW-01 outcome fidelity. **Flag for planner.**

### §3 Orchestrator fan-out + terminal-reached — `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs`

**Method:** `public async Task RunAsync(IStepResult m, StepOutcome outcome, Guid messageId, CancellationToken ct)` (`:69`).

- **Terminal-reached record (D-12/D-13):** the "completed-terminal" branch is **`:87-93`** (`if (selection.Matches.Count == 0 && selection.UnresolvedIds.Count == 0)`). Identities in hand: `m.WorkflowId`, `m.StepId`, `m.CorrelationId`, `m.ExecutionId`, `m.EntryId` (= M_N, the consumed out-blob key). No outbound messageId — matches D-12 exactly. Because `Step_G` fans in twice, this branch fires twice per `(corr, exec)` with distinct `m.EntryId` — satisfies D-13's multiplicity-2 with **no new special case**. **This site is clean; all fields in hand.**
  - Note there is a THIRD trip-end branch at `:73-80` ("completed-unresolved," L1 miss) and a clean-absent skip at `:113-119`. D-12 is about the *terminal* (no successor) case only; the plan should NOT emit a terminal-reached record on the L1-miss or clean-absent branches (those are not "this execution reached its terminal").
- **Fan-out edge record (D-11):** the next-step loop is **`:126-145`** (`foreach (var (stepId, step) in selection.Matches)`). Identities in hand: `m.CorrelationId`, `m.ExecutionId`, `m.WorkflowId`, `m.EntryId` (inbound = M_N), `stepId` (**the next step**). **Missing:** the outbound MessageId minted for that next step — see ⚠ Discrepancy 1.

### §4 ⚠ Discrepancy 1 — outbound MessageId is NOT minted in the Pre loop

**What CONTEXT/SPEC assume:** D-11 — "Lands inside the orchestrator Pre's existing next-step loop (already iterates next steps + **mints a messageId per one**)." SPEC background `:20` — "the orchestrator mints a fresh MassTransit envelope id for the next step and dispatches with `entryId = messageId` (`RelocateTail.cs:23,44`)."

**What the code actually does:**
- `OrchestratorPrePipeline.cs:125-136` sends `NextStepHandoff` via `post.Send((object)handoff, …)` with **NO MessageId override** — comment at `:122-123`: "NO MessageId override (D-11 — MassTransit assigns a fresh envelope id = the next step's messageId)." This D-11 is the **Phase-72** decision, distinct from Phase-76 D-11.
- `NextStepHandoff` (`src/Messaging.Contracts/NextStepHandoff.cs:15-20`) has **no message-id body field** (deliberate, its doc `:11`).
- The outbound envelope id is assigned opaquely by MassTransit on that `Send`, and only becomes a concrete value downstream at `RelocateTail.RunAsync(NextStepHandoff h, Guid messageId, …)` (`RelocateTail.cs:47`), where `messageId` is the inbound `NextStepHandoff` envelope id and becomes `entryId = messageId` (`:66`) — i.e. the `EntryId` the *next* processor hop will read.

**Consequence:** at the `OrchestratorPrePipeline` fan-out loop, the "outbound MessageId dispatched for that step" (M_{N+1}) is **not available**. The inbound `EntryId` (M_N) *is* available.

**Why the goal is still reachable:** ANL-03's redundancy edge is "the orchestrator record that consumed hop N's output `EntryId` (M_N) proves hop N ran." That evidence is **fully satisfied by the inbound `m.EntryId`** at the Pre loop / terminal branch — the outbound MessageId is a *forward*-linking convenience, not the proof-of-execution field. ANL-02's expected set needs the **next `stepId`**, also in hand.

**Resolution options for the orchestrator (do NOT silently re-decide):**
- **Option A — mint + override at the Pre loop.** `var outboundId = NewId.NextGuid();` set it on the send context (`post.Send(handoff, ctx => ctx.MessageId = outboundId)`) and log it. Pro: keeps the record one-per-edge with all 6 fields at one site; the id is still fresh/unique so the data path is unchanged. Con: reverses Phase-72 D-11's deliberate "no override" (the value is now author-set rather than MassTransit-auto). Low risk but touches a locked prior decision's mechanism.
- **Option B — emit at `RelocateTail`.** The outbound id (= the next hop's `EntryId`) is in hand there as `messageId`. Con: `RelocateTail` does NOT have the inbound M_N (not carried on `NextStepHandoff`) — would require adding an `UpstreamEntryId` field to `NextStepHandoff`. Also `RelocateTail` runs in the *Post* consumer, one message later, which is a different tier boundary than "fan-out."
- **Option C — log at the Pre loop with `(inbound EntryId, next StepId)` and omit outbound MessageId.** Satisfies ANL-02 (expected set = next stepIds) and ANL-03 (M_N consumed) fully; drops only the forward M_{N+1} link. Simplest, no contract change, no reversal. The SPEC's 6th field ("outbound MessageId") would be absent from the fan-out record.

**Recommendation to surface:** Option A or C. The researcher does not decide; the orchestrator/discuss step should pick, because it trades a SPEC field (outbound MessageId) against reopening Phase-72 D-11.

### §5 The MEL→OTLP bridge — `src/Messaging.Contracts/ExecutionLogScope.cs` + `InboundExecutionScopeConsumeFilter.cs`

- `ExecutionLogScope` (`:10-35`) carries **5 keys**: `WorkflowId`, `StepId`, `ProcessorId`, `ExecutionId`, `EntryId`. **`CorrelationId` is NOT here** — it is a *separate* scope owned by `CorrelationKeys.LogScope = "CorrelationId"` (`CorrelationKeys.cs:7`), opened by `InboundCorrelationConsumeFilter` (per `ExecutionLogScope` doc `:6-8`).
- `InboundExecutionScopeConsumeFilter<T>` (`:17-33`) is **bus-wide** and open-generic; `BuildState(ec)` is called in `BeginScope` at `:28`. **Skip rules (critical for D-09):** `ExecutionId` is omitted when `Guid.Empty` (`:31`); `EntryId` is omitted when the source sentinel (`:32`). So for the entry step, the *ambient scope* carries neither `ExecutionId` nor `EntryId`.
- **KEY MECHANIC for the hermetic facts:** `CapturingLogger<T>` (see §CapturingLogger) records **only explicit placeholder args, NOT scope state**. Therefore all six FW-01 fields (and all FW-02 fields, including the entry-marker's `ExecutionId = Guid.Empty`) must be passed as **explicit `{Placeholder}` args** on the template — not relied upon from ambient scope. In production those explicit args ALSO surface as `attributes.*` via `ParseStateValues=true`, coinciding with (same value as) the scope-emitted ones. This is exactly the Phase-75 keeper precedent (the `CapturingLogger` doc `:20-22` says so). Passing `ExecutionId = Guid.Empty` explicitly for the entry step gives the analyzer its all-zeros marker (the scope would have omitted it).
- **New-field key convention (Claude's discretion, D-11 note):** new orchestrator attributes (`NextStepId`, and if kept, `OutboundMessageId`) should be explicit placeholder args using PascalCase names matching the `ExecutionLogScope`/`attributes.*` convention, so `attributes.NextStepId` etc. surface consistently.

### §6 Analyzer re-key surface (test project only)

**Structural reader — `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs`:**
- `BuildStepSearchBody` (`:261-276`) filters `exists attributes.StepLabel` + `exists attributes.ExecutionId`. **ANL-01 re-key:** the *structural* query must switch to `attributes.StepId` (the new framework record) and add a query for the FW-02 orchestrator records (ANL-02 expected set). The value query keeps `StepLabel`/`Produced` (D-16/D-17).
- `BuildRunTraces` (`:438-558`) reads `attributes.CorrelationId` + `attributes.ExecutionId` + `attributes.StepLabel` and groups into `RunTrace`. **This is the single structural reader D-15 collapses.** Post-phase, structural completeness/expected-set/reconciliation must be built from framework `StepId` records; `Produced` value collection stays as the layered oracle.
- `RunTrace` (`tests/BaseApi.Tests/Observability/Analysis/RunTrace.cs`) is keyed by `(CorrelationId, ExecutionId)` with `DistinctLabels`/`Values`. Post-D-15 the structural dimension becomes a set of `StepId`s; the plan must decide whether `RunTrace` gains a `DistinctStepIds` set or a parallel model. `Values` (label→int) stays for the oracle.

**Verdict engine — `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs`:**
| Element | Line | D-15/D-16 action |
|---------|------|------------------|
| `HopLabels` (9 `Step_*`) | `:65-66` | **DELETE** (ANL-01/D-15) |
| `IsComplete` (StepLabel set-compare) | `:74-76` | **REPLACE** with stepId-keyed completeness against ANL-02 expected set |
| `ExpectedHopOffset` (label→depth) | `:407-417` | **KEEP** (D-16 value oracle) |
| `ResolveSeed` / `CheckValueChain` | `:433-478` | **KEEP** (value oracle) |
| `valueOracleSupplied` gate | `:161` | **KEEP + generalize** — SMP-01 degrade-to-N/A already partly here |
| Telemetry-gap reconciliation (value-oracle path) | `:185-209` | **GENERALIZE** (ANL-03) — add the framework-redundancy path (orchestrator record consuming M_N reconciles the hop with **no seed oracle**) |
| `metricGateOk` | `:360` | **INVERT for blind case** (ANL-05) — frozen counters + absent traces → INCONCLUSIVE, not FAIL |
| verdict `pass` gate | `:368` (`pass = missing == 0 && !dupFail && valueChainOk && metricGateOk`) | **RE-THREAD** to three-class (ANL-04) |
| `Verdict` enum | `AnalyzerReport.cs:24-39` | **ADD `Inconclusive`** (D-01) |

**Three-class threading (ANL-04/05) at `:363-369`:** today `pass` is a single bool → `Verdict.Pass`/`Fail`. The re-key must split into: (1) **evidence-sufficient?** — is there positive evidence (framework records present, conservation self-consistent)? If total trace darkness + `orch_consumed == proc_sent` (self-consistent conservation), classify **INCONCLUSIVE** regardless of `metricGateOk`; (2) if evidence sufficient, apply the existing PASS/FAIL logic. The `mg1Binding` seam (`AnalyzerE2ETests.cs:87-92`, `PassFailEngine.cs:351`) already distinguishes "conservation is a valid measure here" — ANL-05's blind case is a *new* branch: metric gate failing **because the metrics tier is absent/frozen** (both axes blind) → INCONCLUSIVE; metric gate failing on a **live** genuine gap → FAIL.

**Empty-executionId entry-marker (D-09):** today the entry step is excluded structurally because `IsComplete` strips `Step_A` (`:75`) and the ES query requires `exists attributes.ExecutionId` (`AnalyzerE2ETests.cs:268`) which the Mode-2 `Step_A` "seeded…" log lacks (it carries empty ExecutionId, `PassFailEngine.cs:54-62`). Post-phase, the framework entry record WILL carry `attributes.ExecutionId` = all-zeros GUID (explicit placeholder). The generalized marker rule (D-09): a framework record whose `ExecutionId` is `Guid.Empty` is an **entry marker** — counted for "the entry step ran" but excluded from any `(corr, exec)` execution's expected/complete set (it keys to `(corr, Guid.Empty)`, belonging to neither spawned execution). This **replaces** the `Step_A`-string special-case with an id-shape rule.

### §7 Sweep / harness verdict-class plumbing
- `scripts/phase-67-harness.ps1` exit-code table **confirmed at `:31-42`** (0 PASS, 1 FAIL, 10/20/25/30/40/50/60/70 infra, 64 bad-arg). **`2` is unused** (table jumps 1→10) — D-02's INCONCLUSIVE=2 slots in cleanly.
- `scripts/phase-68-sweep.ps1` roll-up class switch **confirmed at `:80-85`** (`0→PASS`, `1→VERDICT_FAIL`, `64→BAD_ARG`, `default→INFRA_ABORT`). D-02/D-03/D-04: add `2→INCONCLUSIVE` (sweep-fatal, no auto-retry, distinct message). Final exit stays "0 IFF all PASS."
- The analyzer fixture (`AnalyzerE2ETests.cs:250`) asserts `report.Verdict == Verdict.Pass` → any non-Pass (Fail OR Inconclusive) yields a non-zero test exit. For D-02's exit-code **2**, the harness (STEP H) must map the analyzer's INCONCLUSIVE verdict to exit 2 specifically (today the harness mirrors dotnet-test exit = 1 on any failed assert). The plan must give the harness a way to read `report.Verdict == Inconclusive` from the JSON artifact and exit 2 (the artifact is already written before the assert, `:245-246`).

### §8 SourceHash reseed reality — `src/BaseProcessor.Core/SourceHash.targets`
- `@(ImplFiles)` fold **confirmed at `:82`**: `Include="$(MSBuildThisFileDirectory)**\*.cs;$(MSBuildProjectDirectory)\**\*.cs"` — i.e. **BaseProcessor.Core's own sources** + the concrete's sources. Editing `ProcessorPipeline.cs` (BaseProcessor.Core) **changes `Processor.Sample`'s embedded SourceHash** → the live-sweep reseed is genuinely MANDATORY (SPEC constraint verified, not assumed).
- Reseed order (from SPEC constraint, operator scripts confirmed present): clean-rebuild BOTH configs (`rm -rf src/Processor.Sample/obj bin/Release && dotnet build SK_P.sln -c Release --no-incremental`) → FK-safe graph DELETE → seed via `BaseApi.Tests.exe --filter-method "*FanOutSeeder_SeedsAndSelfVerifies*"` → verify liveness keys → `docker compose restart orchestrator` → `POST /start` must return **204** (the 204 IS the proof of hash currency; 422 = still diverged) → only then `scripts/phase-68-sweep.ps1`.
- Operator scripts the live-gate task invokes (all confirmed to exist): `scripts/phase-68-sweep.ps1` (drives all 7), `scripts/phase-67-harness.ps1` (per-scenario; STEP B calls `phase-65-reset.ps1`), `scripts/phase-65-reset.ps1` (FLUSHALL + heal-wait + FK-safe graph DELETE).

## Standard Stack

No new libraries. This phase uses only what is already wired:

| Component | Version / source | Purpose | Why standard |
|-----------|------------------|---------|--------------|
| Microsoft.Extensions.Logging (MEL) | already referenced | Structured `{Placeholder}` logging | The framework's only logging surface |
| OpenTelemetry .NET logs (`AddOpenTelemetry` + `AddOtlpExporter`) | `BaseConsoleObservabilityExtensions.cs:51-60` | MEL→OTLP export; `IncludeScopes`/`ParseStateValues=true` bridge | Already the log export path; surfaces `{Placeholder}` args + scope as `attributes.*` |
| MassTransit | already referenced | Consume pipeline, `SendContext` (Option A) | The dispatch/consume backbone |
| `ExecutionLogScope` bridge | `Messaging.Contracts` | Ambient id scope → `attributes.*` | Already surfaces `attributes.StepId` |
| `CapturingLogger<T>` | `tests/…/Keeper/ReinjectConsumerFacts.cs:24-48` | Hermetic assertion of structured log state | Phase-75 precedent (`5517a58`); no live stack |
| xUnit.v3 under Microsoft.Testing.Platform | test project | Hermetic + RealStack facts | Existing suite (run `BaseApi.Tests.exe` directly per project convention) |

**No `npm`/version verification applicable** (no package changes). The OTLP logs exporter default export processor is **Batch** (`ExportProcessorType.Batch`) in OpenTelemetry .NET — relevant to FW-04 (drop-on-full, non-blocking). Confidence MEDIUM (default behavior of `AddOtlpExporter()` for logs; verify structurally in the FW-04 fact rather than asserting from memory).

## Architecture Patterns

### Data-flow (per-hop evidence, both directions)

```
                          ┌─────────────────────────────────────────────┐
   EntryStepDispatch d    │  ProcessorPipeline.RunAsync(d, messageId)    │
   (StepId, ExecutionId,  │   Mode-1: gate→read→validate→seam→OutputTail │
    CorrelationId,        │   Mode-2: seam spawns→returns null (no tail) │
    EntryId=M_N, +msgId)  │                                              │
        ───────────────▶  │   ★ FW-01: emit ONE per-hop record at each   │
                          │      terminal branch (all 6 ids, explicit)   │
                          │      outcome ∈ {Completed,Failed,Cancelled}  │
                          └───────────────┬─────────────────────────────┘
                                          │ StepCompleted{EntryId = dr.MessageId = M_N}
                                          ▼  (OutputTail.cs:89)  → orchestrator Result queue
                          ┌─────────────────────────────────────────────┐
   IStepResult m          │  OrchestratorPrePipeline.RunAsync(m, …)      │
   (m.EntryId = M_N)      │   resolve next steps (StepAdvancement)       │
        ───────────────▶  │   ├─ NO next steps  → ★ FW-02/D-12 terminal- │
                          │   │                    reached record (×2 @G)│
                          │   └─ fan-out loop   → ★ FW-02/D-11 edge record│
                          │        (inbound M_N, next StepId, [M_{N+1}?]) │
                          │        Send(NextStepHandoff)  ── MT mints ───┐│
                          └──────────────────────────────────────────────┘│
                                          │ NextStepHandoff (no msgId field)│
                                          ▼  RelocateTail.RunAsync(h, messageId=M_{N+1})
                                    data:M_{N+1} written; dispatch entryId=M_{N+1}
                                          ▼
                                  next hop reads EntryId = M_{N+1}

   ES ← OTLP(batch,drop) ← all three record types as attributes.StepId / .NextStepId / .ExecutionId / …
   Analyzer (ES-read-only): structural completeness from StepId records;
                            expected set from FW-02 records; value oracle from StepLabel/Produced.
```

### Pattern: placeholder-only structured log (FW-03 / D-10)
**What:** `logger.LogInformation("Hop executed step={StepId} exec={ExecutionId} corr={CorrelationId} entry={EntryId} msg={MessageId} outcome={Outcome}", d.StepId, d.ExecutionId, d.CorrelationId, d.EntryId, messageId, outcome);` — never `$"..."`, never a payload arg.
**Why:** `ParseStateValues=true` surfaces each `{Placeholder}` as `attributes.<Name>`; `CapturingLogger` sees them; grep for `validatedData`/`dr.Data`/`d.Payload` stays clean.
**Source:** SampleProcessor precedent `SampleProcessor.cs:89`; keeper precedent (Phase 75, `EsIndexNames.cs:110-129`).

### Anti-patterns to avoid
- **Siting the per-hop log in `OutputTail`** — misses Mode-2 (D-08). Confirmed: `ProcessorPipeline.cs:157` returns before the tail.
- **Relying on ambient scope for hermetic assertions** — `CapturingLogger` ignores scope; use explicit placeholders.
- **Adding a Postgres/graph read for the expected set** — violates v8.0.0 "truth = Prometheus + ES only." Expected set MUST come from ES FW-02 records.
- **Interpolating ids into a message template** (`$"...{id}..."`) — breaks the `attributes.*` bridge and the FW-03 grep discipline.
- **Keeping a second `StepLabel`-keyed completeness path** — reintroduces the two-sources-of-truth coupling D-15 kills.

## Don't Hand-Roll

| Problem | Don't build | Use instead | Why |
|---------|-------------|-------------|-----|
| Assert structured log fields hermetically | A custom log sink | `CapturingLogger<T>` (`ReinjectConsumerFacts.cs:24`) | Proven Phase-75 pattern; captures placeholder state exactly |
| Surface ids as ES attributes | Manual JSON enrichment | `ExecutionLogScope` + explicit placeholders | Bridge already live; `attributes.StepId` already reaches ES |
| Non-blocking log export | A custom async queue | OTLP **batch** export processor (default) | Already drop-on-full; FW-04's guarantee rides it |
| Reconstruct per-run traces from ES | New ES client | `BuildRunTraces` / `RunTrace.FromLabels` | Existing grouping + duplicate/convergent arithmetic |
| Telemetry-gap reconciliation | New reconciler | Generalize `PassFailEngine.cs:185-209` | `6e90216` machinery + `TelemetryGap` report field already exist |
| Verdict/infra exit separation | New scheme | Extend the `:31-42` exit-code table (add 2) | Discipline already established |

**Key insight:** almost everything is a *re-key* and a *generalization* of existing, tested machinery — the only genuinely new production code is three log-emission sites (one processor, two orchestrator) plus the FW-04 swallow guard.

## Runtime State Inventory

> Not a rename/refactor phase, but the SourceHash fold + live stack create runtime state that a code-only view misses.

| Category | Items Found | Action Required |
|----------|-------------|------------------|
| Stored data | ES `logs-generic.otel-default` data stream holds OLD-shape records (no `attributes.StepId` framework records) from prior runs | None — the live sweep runs a fresh window after reset; `phase-65-reset.ps1` FLUSHALLs Redis, and the analyzer bounds by `[windowStart, snapshot]` |
| Live service config | `Processor.Sample`'s embedded `AssemblyMetadata("SourceHash")` changes when `ProcessorPipeline.cs` (BaseProcessor.Core) is edited (`SourceHash.targets:82`) | **Mandatory reseed** (rebuild BOTH configs → graph DELETE → seed → 204) before `phase-68-sweep.ps1` |
| OS-registered state | Orchestrator in-process Quartz crons (RAMJobStore) can keep firing ghost workflows | Handled by harness STEP B1 (`docker compose restart orchestrator` after reset) — no plan action beyond invoking the harness |
| Secrets/env vars | None referencing renamed things; `PROCESSOR_STEP_DELAY_MS` (test-only fault hook, `SampleProcessor.cs:49`) unchanged | None |
| Build artifacts | Stale `src/Processor.Sample/bin/Release` dll carries the OLD hash → seeder binds wrong processor row → `POST /start` 422 | Clean-rebuild Release (`--no-incremental`) is step 1 of the reseed |

**Verified:** the SourceHash coupling is real (fold includes `$(MSBuildThisFileDirectory)**\*.cs` = BaseProcessor.Core), so a framework-only edit invalidates the sample's identity.

## Common Pitfalls

### Pitfall 1: `CapturingLogger` sees no scope
**What goes wrong:** you rely on ambient `ExecutionLogScope` for `StepId`/`ExecutionId`; the hermetic FW-01/FW-02 fact asserts them and fails because `CapturingLogger.Log` only reads the state's `IReadOnlyList<KeyValuePair>` (the template args), not `BeginScope`.
**Avoid:** pass all six (FW-01) / all edge (FW-02) fields as explicit `{Placeholder}` args. Confirmed by `CapturingLogger` `:37-39` and its doc `:20-22`.

### Pitfall 2: ES `.keyword` sub-field trap
**What goes wrong:** you query `attributes.StepId.keyword` and get zero hits (broke 4 facts at Phase-11 UAT, `EsIndexNames.cs:63-69`).
**Avoid:** the OTel-managed data stream maps string attributes DIRECTLY to `keyword`. Add `StepIdFieldPath = "attributes.StepId"` (no `.keyword`), mirroring `ExecutionIdFieldPath`.

### Pitfall 3: outcome fidelity vs. output-schema downgrade
**What goes wrong:** the per-hop record logs `Completed` but `OutputTail` forced `Failed` (`OutputTail.cs:57-59`), so the framework record disagrees with the `StepFailed` wire.
**Avoid:** have `OutputTail.RunAsync` return the resolved outcome, or document the record as pre-validation outcome. (Design Point B.)

### Pitfall 4: a throwing `ILogger` must not fail the hop (FW-04)
**What goes wrong:** MEL `ILogger.Log` propagates exceptions by default; a throwing/blocking logger would fail/delay the consume — making the witness a participant.
**Avoid:** wrap the NEW per-hop/fan-out log calls in a swallow (`try { logger.LogInformation(...); } catch { /* observability must never fail the hop */ }`). The FW-04 acceptance fact passes a throwing `ILogger` and asserts the pipeline outcome is unchanged — this **requires** the guard on the new calls specifically (existing warning logs at `:147`,`:195` are unguarded and out of FW-04's scope). Plus a structural assert that export is batch/drop.

### Pitfall 5: forgetting the SourceHash reseed → `POST /start` 422
**What goes wrong:** editing `ProcessorPipeline.cs` and running the sweep without a Release clean-rebuild + reseed → 422, false INFRA_ABORT.
**Avoid:** follow the 5-step reseed; the **204 is the proof of currency**.

### Pitfall 6: MTP silently ignores `dotnet test --filter`
**What goes wrong:** `dotnet test --filter` (VSTest syntax) is ignored under Microsoft.Testing.Platform and runs the whole suite; `dotnet test` can hang on Windows MTP.
**Avoid:** run `BaseApi.Tests.exe` directly with MTP-native filters after `--` (project convention / user memory). Hermetic runs use `--filter-not-trait Category=RealStack`.

### Pitfall 7: cold-ES flake resembles the INCONCLUSIVE case
**What goes wrong:** a cold ES returns `StartedRuns=0` while conservation is intact — this is *exactly* the D-01 INCONCLUSIVE shape, and TEST-01 can flake on cold ES (user memory).
**Avoid:** the three-class verdict now *correctly* labels this INCONCLUSIVE (not FAIL); the D-04 roll-up message must advise a deliberate warm-ES re-run, not auto-retry.

## Code Examples

### Processor per-hop record (FW-01) — shape only, wording is Claude's discretion
```csharp
// Source pattern: SampleProcessor.cs:89 (placeholder-only) + CapturingLogger contract
// Placed at each terminal branch of ProcessorPipeline.RunAsync (or via a guarded helper).
try
{
    logger.LogInformation(
        "hop executed {StepId} {ExecutionId} {CorrelationId} {EntryId} {MessageId} {Outcome}",
        d.StepId, d.ExecutionId, d.CorrelationId, d.EntryId, messageId, outcome);
}
catch { /* FW-04: observability must never fail or delay the hop */ }
```

### Fan-out edge record (FW-02/D-11) — Option C variant (no outbound MessageId)
```csharp
// Source: OrchestratorPrePipeline.cs:126 fan-out loop; identities m.* + loop stepId in hand
foreach (var (stepId, step) in selection.Matches)
{
    // ... existing NextStepHandoff send ...
    try
    {
        logger.LogInformation(
            "fan-out {CorrelationId} {ExecutionId} {WorkflowId} {EntryId} {NextStepId}",
            m.CorrelationId, m.ExecutionId, m.WorkflowId, m.EntryId, stepId);
    }
    catch { }
}
```

### Terminal-reached record (FW-02/D-12/D-13)
```csharp
// Source: OrchestratorPrePipeline.cs:87-93 completed-terminal branch (fires ×2 per (corr,exec) @ Step_G)
try
{
    logger.LogInformation(
        "terminal reached {CorrelationId} {ExecutionId} {WorkflowId} {EntryId} {StepId}",
        m.CorrelationId, m.ExecutionId, m.WorkflowId, m.EntryId, m.StepId);
}
catch { }
```

## State of the Art

| Old approach | Current approach | When changed | Impact |
|--------------|------------------|--------------|--------|
| Evidence == truth (binary PASS/FAIL) | Three-class; FAIL needs evidence of loss | This phase (ANL-04) | INCONCLUSIVE for observability blindness |
| Structural completeness from author `Step_*` labels | Framework `attributes.StepId` records | This phase (ANL-01/D-15) | Verdict works for any processor |
| Expected set = hardcoded 9 labels | Expected = ES orchestrator dispatch records | This phase (ANL-02) | Closes TEST-08 blind spot |
| Reconciliation only via sample value oracle | Generic framework redundancy (M_N consumed) | This phase (ANL-03) | Dropped log ≠ false FAIL without a seed oracle |
| Framework off-limits / no per-hop log | Framework emits per-hop + fan-out records | This phase (reverses Phase-73 D-07) | Production operability feature |

**Deprecated/outdated after this phase:** `PassFailEngine.HopLabels` (deleted); `StepLabel`-keyed `IsComplete` (removed); `Step_A`-string exclusion special-case (replaced by empty-executionId marker rule).

## Assumptions Log

| # | Claim | Section | Risk if wrong |
|---|-------|---------|---------------|
| A1 | `AddOtlpExporter()` for logs uses the Batch export processor (drop-on-full) by default | FW-04 / Standard Stack | LOW — FW-04 fact asserts export shape structurally anyway; if Simple, the plan adds an explicit `ExportProcessorType.Batch` |
| A2 | The FW-02 outbound MessageId is not otherwise recoverable at the Pre loop without Option A/B | §4 Discrepancy 1 | MEDIUM — drives a real design decision; verified against live send code but MassTransit send-context capture nuances could offer a 4th path |
| A3 | Emitting `ExecutionId = Guid.Empty` as an explicit placeholder yields `attributes.ExecutionId` = all-zeros string in ES (not omitted) | §5 / D-09 | MEDIUM — scope omits empty GUIDs (`ExecutionLogScope.cs:31`), but explicit template args are not subject to that skip; confirm in the ANL-01 live probe |
| A4 | The harness can read `Verdict == Inconclusive` from the JSON artifact and exit 2 | §7 | LOW — artifact is written before the assert (`AnalyzerE2ETests.cs:245-246`); mechanism exists |

## Open Questions

1. **Which FW-02 outbound-MessageId option (A/B/C)?**
   - Known: inbound `EntryId` (M_N) + next `StepId` are in hand at the Pre loop; outbound M_{N+1} is not.
   - Unclear: whether the SPEC's 6th fan-out field (outbound MessageId) is worth reopening Phase-72 D-11 (Option A) or a `NextStepHandoff` field (Option B).
   - Recommendation: default to **Option C** (drop the forward link, keep ANL-02/03 fully satisfied) unless the discuss step wants the full forward chain, then **Option A**.

2. **Does `OutputTail` return the resolved outcome (Design Point B)?**
   - Known: output-schema downgrade happens inside `OutputTail.cs:57-59`.
   - Recommendation: small return-shape change (`bool` → a `(bool proceed, StepOutcome resolved)` or an `out`) so the pipeline logs the true terminal outcome; else document the pre-validation-outcome caveat.

3. **`RunTrace` structural model shape** — add a `DistinctStepIds` set, or a parallel `FrameworkRecord` model? (Claude's discretion under D-15, but affects fact ergonomics.)

## Environment Availability

| Dependency | Required by | Available | Version | Fallback |
|------------|-------------|-----------|---------|----------|
| Docker + compose stack (ES 8.15.5, otel-collector 0.152.0, Prometheus, RabbitMQ, Redis) | Live 7-scenario sweep gate | Not probed this session (research task, no stack spin-up) | — | Hermetic facts cover all verdict-class logic without the stack; live gate is the final acceptance only |
| `pwsh` (PowerShell) | `phase-67-harness.ps1` / `phase-68-sweep.ps1` | Assumed (scripts are `.ps1`, prior phases ran them) | — | — |
| `BaseApi.Tests.exe` (built test host) | Hermetic + seeder + analyzer facts | Built on demand | xUnit.v3 / MTP | — |
| .NET SDK (build BOTH configs) | SourceHash reseed | Assumed present | — | — |

**Missing dependencies with no fallback:** none identified for the hermetic (plannable/verifiable) work. The live sweep requires a healthy Docker stack; that is an acceptance gate, not a per-task blocker.

## Validation Architecture

> Nyquist validation is enabled (`.planning/config.json` `nyquist_validation: true`). This section seeds `76-VALIDATION.md`.

### Test Framework
| Property | Value |
|----------|-------|
| Framework | xUnit.v3 under Microsoft.Testing.Platform (MTP) |
| Config file | none (MTP-native; test host is `tests/BaseApi.Tests/bin/.../BaseApi.Tests.exe`) |
| Quick run (hermetic) | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` (run the exe directly — `dotnet test` hangs on Windows MTP; `--filter` is silently ignored) |
| Targeted run | `BaseApi.Tests.exe -- --filter-method "*<FactName>*"` |
| Live suite | `pwsh -File scripts/phase-68-sweep.ps1` (all 7 scenarios; after mandatory SourceHash reseed + `POST /start` 204) |

### Sampling-rate reasoning (Nyquist: sample rate vs. signal)
The "signal" is the verdict-class space. There are **three classes** (PASS/FAIL/INCONCLUSIVE) × two evidence-redundancy failure modes (missing-one-record, missing-both-records). Nyquist here means: **each distinguishable verdict outcome needs at least one positive hermetic fact** — you cannot infer the INCONCLUSIVE branch works from a PASS fact, nor the reconcile-one branch from the reconcile-none branch. Coverage below is built to place ≥1 sample on every distinguishable outcome, so a green hermetic suite is not vacuously green. The live sweep is a *lower-rate* confirmation over the real stack (7 scenarios), sufficient because the fine-grained branch logic is already sampled hermetically.

### Phase Requirements → Test Map
| Req | Behavior | Test type | Automated command | Exists? |
|-----|----------|-----------|-------------------|---------|
| FW-01 | one record/hop, all six fields, Completed/Failed/Cancelled | hermetic (CapturingLogger) | `BaseApi.Tests.exe -- --filter-method "*ProcessorPipeline*PerHop*"` | ❌ Wave 0 |
| FW-01 | processor with NO author log still yields the record | hermetic | same class | ❌ Wave 0 |
| FW-01/D-09 | entry step (executionId=Empty) logged as marker | hermetic | same class | ❌ Wave 0 |
| FW-02/D-11 | 2-way fan-out emits exactly 2 edge records, distinct next-StepIds | hermetic | `*OrchestratorPrePipeline*FanOut*` | ❌ Wave 0 (extend `OrchestratorPrePipelineFacts`) |
| FW-02/D-12/D-13 | terminal-reached record ×2 at Step_G, distinct inbound entryId | hermetic | same class | ❌ Wave 0 |
| FW-03 | no template/arg references `validatedData`/`dr.Data`/`d.Payload`; sentinel absent | hermetic + grep | `*Framework*NoPayload*` + repo grep | ❌ Wave 0 |
| FW-04 | throwing/blocking ILogger neither fails nor delays the hop; export batch/drop | hermetic (structural) | `*NonBlocking*` / `*ExportShape*` | ❌ Wave 0 |
| ANL-01 | completeness from stepId records; `HopLabels` grep-gone; zero `Step_*` present | hermetic (engine) | `*PassFailEngine*StepId*` | ❌ Wave 0 (extend `PassFailEngineFacts`) |
| ANL-02 | dispatched-but-never-executed → FAIL; expected from ES only | hermetic (engine) | `*ExpectedSet*` / `*Test08*` | ❌ Wave 0 |
| ANL-03 | missing hop record but orchestrator consumed M_N → non-binding gap, no seed oracle; missing both → binding miss | hermetic (engine) | `*Reconcile*Redundancy*` | ❌ Wave 0 |
| ANL-04 | (a) trace-dark + conservation intact → INCONCLUSIVE; (b) partial + real gap → FAIL; (c) complete → PASS | hermetic (engine) — **3 facts** | `*Verdict*Inconclusive*` / `*Fail*` / `*Pass*` | ❌ Wave 0 |
| ANL-05 | frozen counters + absent traces → INCONCLUSIVE; live gap → FAIL | hermetic (engine) — **2 facts** | `*MetricGate*Blind*` | ❌ Wave 0 |
| SMP-01 | correct Pass/Fail/Inconclusive from framework records only (no Step_*/Received/Produced); value oracle absent → N/A not FAIL | hermetic (engine) | `*OracleAbsent*` | ❌ Wave 0 |
| All (live) | 7-scenario sweep after reseed + 204, results classified | RealStack | `pwsh -File scripts/phase-68-sweep.ps1` | ✅ script exists; new INCONCLUSIVE class added |

### Per-class positive-fact requirement (explicit Nyquist samples)
- **PASS:** ≥1 fact — complete framework-record cohort (ANL-04c).
- **FAIL:** ≥2 facts — dispatched-but-never-executed (ANL-02), and partial-evidence real-conservation-gap (ANL-04b), and live-counter genuine gap (ANL-05).
- **INCONCLUSIVE:** ≥2 facts — total trace darkness + intact conservation (ANL-04a), and frozen counters + absent traces (ANL-05 blind).
- **Reconciliation redundancy:** ≥2 facts — missing-one-record reconciles non-binding with no seed oracle (ANL-03), and missing-both-records stays binding (ANL-03).

### Sampling rate
- **Per task commit:** hermetic quick run (`--filter-not-trait Category=RealStack`).
- **Per wave merge:** full hermetic suite green (analyzer + pipeline + orchestrator fact classes).
- **Phase gate:** full hermetic suite green, THEN the live sweep (`phase-68-sweep.ps1`) after reseed → every non-PASS verdict explained and classified (genuine loss / telemetry gap / observability-degraded INCONCLUSIVE / blind-spot closure).

### Wave 0 Gaps
- [ ] `tests/BaseApi.Tests/Processor/…PerHopLogFacts.cs` — FW-01/D-09 (new class, CapturingLogger)
- [ ] extend `tests/BaseApi.Tests/Orchestrator/OrchestratorPrePipelineFacts.cs` — FW-02/D-11/D-12/D-13
- [ ] `tests/…/Observability/…FrameworkNoPayloadFacts.cs` — FW-03 (+ repo grep assertion)
- [ ] `tests/…/…NonBlockingLogFacts.cs` — FW-04 (throwing ILogger + export-shape)
- [ ] extend `tests/BaseApi.Tests/Observability/Analysis/PassFailEngineFacts.cs` — ANL-01/02/03/04/05/SMP-01 (three-class + reconciliation)
- [ ] add `StepIdFieldPath` const to `EsIndexNames.cs`; new structural + expected-set ES query bodies in the RealStack fixture
- [ ] harness: map analyzer `Inconclusive` verdict → exit code 2; sweep roll-up: add `2→INCONCLUSIVE` class + distinct message
- [ ] Framework install: none — existing test host covers all fact types

*(No framework install needed; the gap is new fact classes + the analyzer/harness re-key.)*

## Security Domain

> `security_enforcement` is absent from config → treated as enabled. This is an observability/analyzer phase; the security-relevant control is data-protection in logs.

### Applicable ASVS categories
| ASVS category | Applies | Standard control |
|---------------|---------|------------------|
| V5 Input Validation | yes | Existing `ProcessorJsonSchemaValidator` (input/output schema); scenario-id whitelist `^[A-Za-z0-9_-]+$` (`AnalyzerE2ETests.cs:68`) |
| V6 Cryptography | no | none introduced |
| V7 Errors & Logging | **yes (primary)** | **FW-03 no-payload rule** — framework logs carry ids + outcome only; never `validatedData`/`dr.Data`/`d.Payload`. Blob hash/length permitted, blob not. Enforced by grep + a sentinel fact |
| V2/V3/V4 Auth/Session/Access | no | no auth surface touched |

### Known threat patterns
| Pattern | STRIDE | Standard mitigation |
|---------|--------|---------------------|
| Sensitive payload leaking into telemetry (production per-hop logs at real volume) | Information disclosure | FW-03 placeholder-only, ids-only templates; grep-clean assertion; sentinel-through-blob fact (`SPEC` FW-03 acceptance) |
| Log-template injection via ids | Tampering | Ids are server-minted Guids placed as structured values, never interpolated (`InboundExecutionScopeConsumeFilter.cs:14-15` note) — same discipline for the new records |
| Path traversal via scenario id | Tampering | Existing `^[A-Za-z0-9_-]+$` whitelist before any path compose |

## Sources

### Primary (HIGH — read live this session)
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` — per-hop emission site, branch map, ids in hand
- `src/BaseProcessor.Core/Processing/OutputTail.cs` — `:89` EntryId=MessageId; `:57-59` outcome downgrade; Mode-2 skip
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` — `:87-93` terminal branch, `:126-145` fan-out loop
- `src/Orchestrator/Dispatch/RelocateTail.cs` + `StepDispatcher.cs` + `NextStepHandoff.cs` — outbound-MessageId mint reality (Discrepancy 1)
- `src/Messaging.Contracts/ExecutionLogScope.cs` + `CorrelationKeys.cs` + `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` — bridge, 5 keys, empty-GUID skip
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs` — OTLP logs export (`AddOtlpExporter`, IncludeScopes/ParseStateValues)
- `tests/BaseApi.Tests/Observability/Analysis/PassFailEngine.cs` + `AnalyzerReport.cs` + `RunTrace.cs` + `Helpers/EsIndexNames.cs` — re-key surface
- `tests/BaseApi.Tests/Observability/AnalyzerE2ETests.cs` — `BuildRunTraces` structural reader, ES query bodies
- `tests/BaseApi.Tests/Keeper/ReinjectConsumerFacts.cs:24-48` — `CapturingLogger<T>` API
- `src/BaseProcessor.Core/SourceHash.targets:82` — `@(ImplFiles)` fold
- `scripts/phase-67-harness.ps1:31-42` + `scripts/phase-68-sweep.ps1:80-85` — exit-code table + roll-up switch
- `src/Processor.Sample/SampleProcessor.cs:56,68,89` — author log + `Step_*` config-payload label + placeholder precedent

### Secondary (MEDIUM)
- OpenTelemetry .NET default OTLP logs export = Batch processor (training knowledge; verify structurally in the FW-04 fact — A1)

## Metadata

**Confidence breakdown:**
- Integration map (all 9 sites): HIGH — every file:line read live; drift explicitly reconciled
- FW-02 outbound-MessageId resolution: MEDIUM — a real decision, not a research gap
- Analyzer re-key surface: HIGH — exact methods/lines located
- FW-04 export batch/drop: MEDIUM — default behavior; asserted structurally by the fact
- Validation architecture: HIGH — framework + commands verified against project convention/memory

**Research date:** 2026-07-16
**Valid until:** ~2026-08-15 (stable internal codebase; re-verify line numbers if `ProcessorPipeline`/`OrchestratorPrePipeline`/`PassFailEngine` change before planning)
