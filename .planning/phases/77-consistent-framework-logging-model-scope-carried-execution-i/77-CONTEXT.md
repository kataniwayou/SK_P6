# Phase 77: Consistent framework logging model — Context

**Gathered:** 2026-07-16
**Status:** Ready for planning
**Source:** In-conversation design discussion (locked decisions A–D)

<domain>
## Phase Boundary

**In scope (this phase):** the *source-side* logging refactor across the three framework components — `BaseProcessor.Core`, `Orchestrator`, `Keeper` — plus removing all concrete-processor (`Processor.Sample`) logs. Establish one uniform logging pattern so the framework's own records carry the full evidence picture.

**Out of scope (→ Phase 78):** the analyzer (`AnalyzerE2ETests` / `PassFailEngine`) and the live resilience sweep. Phase 77 deliberately changes the framework log *shape* in ways that will temporarily break the LIVE analyzer (which is `Category=RealStack`, Docker-gated, so it does NOT run in the hermetic build). Phase 78 adapts the analyzer to the new shape and re-runs the live gate. **Sequencing is intentional (user-directed): first implement the logging, then fix the sweep.**

**Verification for THIS phase** is hermetic only: 0-warning Debug+Release build, the existing hermetic fact suites stay green (they build `RunTrace` from explicit values, not from live log strings, so they are unaffected), plus grep-assertions on the changed log templates. Live ES verification belongs to Phase 78.
</domain>

<decisions>
## Implementation Decisions (LOCKED)

### The three-tier logging model
Every framework log resolves its ids in one of three tiers:
- **Tier 1 — ambient scope, on EVERY log, NEVER in the message string:** `WorkflowId`, `StepId`, `ProcessorId`, `ExecutionId`, `EntryId`, `CorrelationId`. Delivered as `attributes.*` by the bus-wide `InboundExecutionScopeConsumeFilter` (`ExecutionLogScope.BuildState`) + `InboundCorrelationConsumeFilter`, surfaced via OTel `IncludeScopes=true`. Empty GUIDs are skipped by `BuildState` (so the id is ABSENT, not zero, when empty).
- **Tier 2 — send/consume framework logs, in the string:** `{MessageId}` — the one delivery-unit id the scope does NOT carry.
- **Tier 3 — outcome/customized framework logs, in the string:** domain extras only — `{Outcome}`, `{NextStepId}`, `{ReinjectOutcome}`.

### D1 — Strip all redundant Tier-1 placeholders from framework message strings
The scope already emits Tier-1 as `attributes.*`; restating them in the template is pure duplication. Remove `{WorkflowId}/{StepId}/{ProcessorId}/{ExecutionId}/{EntryId}/{CorrelationId}` from every framework log template. Attributes are unchanged (scope still supplies them). Known framework records and their target shape:
- `BaseProcessor.Core/Processing/ProcessorPipeline.cs` LogHopExecuted (~:208): `"hop executed {StepId} {ExecutionId} {CorrelationId} {EntryId} {MessageId} {Outcome}"` → **`"hop executed {MessageId} {Outcome}"`** (consume/outcome record; keeps `{MessageId}` Tier-2 + `{Outcome}` Tier-3).
- `Orchestrator/Dispatch/OrchestratorPrePipeline.cs` fan-out (~:171): `"fan-out {CorrelationId} {ExecutionId} {WorkflowId} {EntryId} {NextStepId}"` → **`"fan-out {NextStepId}"`** (`{NextStepId}` Tier-3; plus `{MessageId}` per D3 if the outbound id is captured).
- `Orchestrator/Dispatch/OrchestratorPrePipeline.cs` terminal-reached (~:106): `"terminal reached {CorrelationId} {ExecutionId} {WorkflowId} {EntryId} {StepId}"` → **`"terminal reached"`** (terminal ack marker; not a send/consume; Tier-1 from scope only).
- `Orchestrator/Dispatch/OrchestratorPrePipeline.cs` business "Trip ended …" lines (:75/:95/:134/:187): strip the Tier-1 placeholders (`{WorkflowId}/{StepId}`), keep any Tier-3 extra (`outcome={Outcome}`).
- `Keeper/Recovery/ReinjectConsumer.cs` (:49 drop, :78 sent) and `OrchestratorReinjectConsumer.cs` (:47): currently `"REINJECT … {CorrelationId} {ExecutionId} {EntryId} {MessageId} {ReinjectOutcome}"` → **`"REINJECT sent {MessageId} {ReinjectOutcome}"`** / **`"REINJECT drop {MessageId} {ReinjectOutcome}"`** — but ONLY after D2 (keeper opens the scope) so Tier-1 still arrives.

### D2 — Keeper opens the execution scope (symmetry, decision D)
The keeper currently opens NO ambient scope (`ReinjectConsumer.cs:45` comment), so it hand-carries ids. Make each keeper recovery consume open the execution scope so Tier-1 arrives from scope like processor/orchestrator — the `ExecutionLogScope` doc's unbuilt "next wave" (`ExecutionLogScope.cs:19-23`: "the Keeper fault consumers that open the scope manually — the filter does NOT fire on Fault<T>"). The keeper recovery messages (`KeeperReinject`/`KeeperInject`/etc.) already carry all ids (they build `EntryStepDispatch(m.WorkflowId, m.StepId, m.ProcessorId, …)`), so `BeginScope(ExecutionLogScope.BuildState(m))` is buildable. Single choke point: `RecoveryConsumerBase.Consume` (`Keeper/Recovery/RecoveryConsumerBase.cs:36`) if the message type exposes the ids uniformly; otherwise per-consumer.

### D3 — Send/consume framework logs carry `{MessageId}` (the one non-scope id)
- Processor consume (`hop executed`): already carries the consumed `{MessageId}`. ✓ (keep it).
- Sends whose outbound envelope id is currently NOT in hand at emit (the fan-out `post.Send`, the processor result `OutputTail.SendResult`): capture the minted id via a send-context callback and log it — the established idiom already exists (`BaseProcessor.cs:102` and every keeper send do `ctx => ctx.MessageId = …`). D-11 Option C dropped the fan-out outbound MessageId ONLY because it was "not in hand at the fan-out loop"; capturing it via the send callback resolves that. This is the meatiest change — treat as its own task with its own hermetic fact.

### D4 — Remove all concrete-processor logs (full SMP-01, decision B)
Delete `Processor.Sample/SampleProcessor.cs` author logs (`:68` "{StepLabel} seeded …", `:89` "{StepLabel} received {Received} produced {Produced}"). The verdict may never depend on a concrete-processor log. (The analyzer's value-oracle already degrades to N/A when these are absent — Phase 78 confirms; for Phase 77 the removal is the deliverable.)

### D5 — Operator freedom is preserved
A concrete-processor author may write ANY message string (even bare `$"…"` interpolation with no placeholders). The framework scope still attaches Tier-1 `attributes.*` to their log. Do NOT add any mechanism that requires operators to use placeholders. This is a property to PRESERVE, not build — verify it isn't regressed (the scope filter stays bus-wide and unconditional).

### D6 — Keep outcome-shaped records (decision C)
The emitting component knows the outcome at emit time, so `{Outcome}`/`{NextStepId}`/`{ReinjectOutcome}` staying in the string is consistent, not a violation. Do NOT collapse to bare `sent/consumed` — clean the existing outcome records in place.

### Load-bearing couplings — DOCUMENT for Phase 78 (do not fix here)
1. **Entry-marker becomes ABSENT ExecutionId.** The Mode-2 entry step has `ExecutionId == Guid.Empty`; today its all-zeros value comes from the EXPLICIT `{ExecutionId}` placeholder on `hop executed`. `BuildState` skips empty GUIDs, so once `{ExecutionId}` is stripped the entry marker's `attributes.ExecutionId` is ABSENT, not zero. The analyzer's `PassFailEngine.IsEntryMarker` (`:63`, checks `== Guid.Empty`) and `AnalyzerE2ETests.IsEntryMarkerExecution` must switch to "attribute absent" — Phase 78.
2. **Keeper records gain `attributes.StepId`+`MessageId`.** Once the keeper opens the scope (D2), its reinject records enter the analyzer's structural query and look like processor "did-run" records. Discriminate via `attributes.ReinjectOutcome` (only keeper records carry it) — Phase 78.

### Constraint — SourceHash reseed
This touches `BaseProcessor.Core` production source (`ProcessorPipeline.cs`, `OutputTail.cs`), so any later live run (Phase 78) re-triggers the mandatory SourceHash reseed (rebuild BOTH host configs + `docker compose build` the images → graph-DELETE → seed → 204). Not exercised in Phase 77 (hermetic only).
</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Scope mechanism (Tier-1 source of truth)
- `src/Messaging.Contracts/ExecutionLogScope.cs` — the 5-key scope dict; empty-GUID skip rule (load-bearing for the entry marker)
- `src/BaseConsole.Core/Messaging/InboundExecutionScopeConsumeFilter.cs` — bus-wide scope filter (opens the scope per consume)
- `src/BaseConsole.Core/Messaging/InboundCorrelationConsumeFilter.cs` — CorrelationId scope
- `src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:52-53` — where both filters are registered (`UseConsumeFilter`)
- `src/BaseConsole.Core/DependencyInjection/BaseConsoleObservabilityExtensions.cs:52-65` — OTel `IncludeScopes=true` bridge

### Framework log sites to change
- `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs` (~:208 LogHopExecuted; :138/:152/:232 fault warnings)
- `src/BaseProcessor.Core/Processing/OutputTail.cs` (:130 result send — outbound MessageId capture)
- `src/Orchestrator/Dispatch/OrchestratorPrePipeline.cs` (:106 terminal-reached, :171 fan-out, :75/:95/:134/:187 business lines; :144-154 fan-out send)
- `src/Keeper/Recovery/ReinjectConsumer.cs` (:49/:78), `OrchestratorReinjectConsumer.cs` (:47), `RecoveryConsumerBase.cs` (:36 consume choke point)
- `src/Processor.Sample/SampleProcessor.cs` (:68/:89 — DELETE)

### Prior context
- `.planning/phases/76-*/76-SPEC.md` — the FW-01..04 / SMP-01 record definitions this refactor cleans
- `.planning/phases/76-*/76-05-SUMMARY.md` — the live-gate fix (dispatch-in-degree convergent derivation) that Phase 78 builds on
</canonical_refs>

<specifics>
## Specific Ideas
- The consistent pattern in one line: **Tier-1 from scope, `{MessageId}` on send/consume, domain extra otherwise — no id ever restated in a string that the scope already carries.**
- Hermetic verification levers: `rg` the changed templates to assert the Tier-1 placeholders are gone and `{MessageId}`/`{Outcome}`/`{NextStepId}`/`{ReinjectOutcome}` remain; a fact asserting the keeper's recovery consume opens a scope carrying the 5 ids; a fact asserting a captured outbound `{MessageId}` on the fan-out / result send.
- Test host is **net8.0** (`tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe`). REQUIREMENTS.md does not exist; requirements tracked via ROADMAP + this CONTEXT. Full hermetic baseline is 272 pre-existing infra-dependent failures (Docker-less) — only zero NEW failures in changed scope matters.
</specifics>

<deferred>
## Deferred Ideas
- **Analyzer + live sweep → Phase 78** (entry-marker=absent-ExecutionId; keeper discrimination via ReinjectOutcome; drop value oracle; keep Prometheus secondary; reseed + 7-scenario live gate; verify purely from framework ES logs).
- **MessageId-graph reconstruction** (nodes=MessageId, edges=consumed-EntryId→produced-MessageId) — an option for Phase 78's analyzer if the outcome-shaped records prove insufficient; not required by Phase 77.
</deferred>

---

*Phase: 77-consistent-framework-logging-model-scope-carried-execution-i*
*Context gathered: 2026-07-16 via in-conversation design discussion*
