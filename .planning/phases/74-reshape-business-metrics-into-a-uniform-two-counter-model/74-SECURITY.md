# Phase 74 — Security Audit: Reshape Business Metrics to a Uniform Two-Counter Model

**Audited:** 2026-06-18
**ASVS Level:** 1
**Block-on:** high
**Verdict:** SECURED — 9/9 threats closed (2 mitigate verified in code, 7 accept logged)

This phase is an internal observability refactor: it collapses each service's business counters into a
uniform `{service}_messages_consumed` / `{service}_messages_sent` pair. No new endpoint, auth surface, or
trust boundary was introduced (per 74-01-SUMMARY.md `## Threat Flags`: "None"). The threat register is
dominated by cardinality / info-disclosure concerns that are accepted by the bounded-label design, plus two
tampering (miscount) threats that required code verification.

---

## Threat Verification

| Threat ID | Category | Disposition | Status | Evidence |
|-----------|----------|-------------|--------|----------|
| T-74-01 | D (DoS via cardinality) | accept | CLOSED | Logged below. Labels = `workflowId`+`processorId` only (bounded); `messageId` NOT a label (D-04). `OrchestratorMetrics.cs:40-41` doc + increment sites tag camelCase pair only. |
| T-74-02 | I (disclosure) | accept | CLOSED | Logged below. Label values are GUIDs (workflowId/processorId), not PII; `messageId` excluded. |
| T-74-03 | D (cardinality) | accept | CLOSED | Logged below. `ProcessorMetrics.cs:23-24,42-43` — `outcome` label REMOVED this phase; labels reduced to `workflowId`+`processorId`. |
| T-74-04 | I (disclosure) | accept | CLOSED | Logged below. GUIDs, not PII. |
| T-74-05 | D (cardinality) | accept | CLOSED | Logged below. `KeeperMetrics.cs:42-45` — `keeper_l2_probe` is label-less; messages counters labeled `workflowId`+`processorId` only; `messageId` not a label. |
| T-74-07 | I (disclosure) | accept | CLOSED | Logged below. GUIDs, not PII. |
| T-74-09 | R (repudiation — lost outcome corroboration) | accept | CLOSED | Logged below. Non-completed-outcome corroboration WARNING intentionally removed (`PassFailEngine.cs:278-280`); it was a non-binding corroboration warning, not a gate. Primary completion math unaffected. |
| T-74-06 | T (tampering via miscount) | mitigate | CLOSED | See verification below. |
| T-74-08 | T (tampering — false-green verdict) | mitigate | CLOSED | See verification below. |

---

## Mitigate-Disposition Verification (code-level)

### T-74-06 — CountSent placement on the Reinject drop path  — CLOSED

**Threat:** A falsely-inflated `keeper_messages_sent` count could mask real message loss in the recovery
audit if `CountSent` fired on the Reinject early-return drop branch or on an exhausted send.

**Mitigation verified:**
- `keeper_messages_sent` has exactly ONE increment site — the shared `CountSent` helper
  (`RecoveryConsumerBase.cs:70-73`). No other code increments `metrics.MessagesSent`.
- All four SENDING consumers call `CountSent` strictly AFTER the guarded `ep.Send`:
  - `ReinjectConsumer.cs:65` (after `await Guard(() => ep.Send(...))` at line 62)
  - `OrchestratorReinjectConsumer.cs:73` (after send at line 70)
  - `InjectConsumer.cs:63` (after send at line 59)
  - `OrchestratorInjectConsumer.cs:63` (after dispatch at line 59)
- `Guard` re-throws on retry exhaustion (`RecoveryConsumerBase.cs:54-59`), so a failed/exhausted send never
  reaches the `CountSent` line — a give-up throw falls out of `Consume` to broker nack-requeue. The increment
  is therefore reached only on a confirmed send.
- Both Reinject drop branches `return` BEFORE `CountSent`:
  - `ReinjectConsumer.cs:38-45` (absent/empty L2 `data:` → `return` at line 44)
  - `OrchestratorReinjectConsumer.cs:42-49` (absent/empty `out:` → `return` at line 48)
- Both DELETE consumers never reference `CountSent` (they send nothing): grep across `src/Keeper` returns zero
  `CountSent` occurrences in `DeleteConsumer.cs` / `OrchestratorDeleteConsumer.cs`.

**Conclusion:** The sent counter cannot be inflated on a drop or exhausted-send path. D-02 honored.

### T-74-08 — PassFailEngine repoint to processor_messages_sent total  — CLOSED

**Threat:** Repointing the pass/fail gate from the removed `processor_result_sent{outcome="completed"}` field
to the new `processor_messages_sent` total could produce a false-green verdict if the completion math drifted.

**Mitigation verified:**
- `PromCounterSnapshot.cs:37-42` exposes `ProcessorMessagesSentDelta`, the D-12 repoint. The repoint is sound
  only because the close-gate fixture is all-complete (total == completed); this constraint is documented
  inline at `PromCounterSnapshot.cs:38-41`.
- `PassFailEngine.cs` references only the new uniform delta fields (`OrchestratorMessagesSentDelta` at lines
  105/257/273/289/296, `OrchestratorMessagesConsumedDelta` at lines 290/295). The removed `outcome`-keyed field
  appears only in a historical-provenance comment (`PassFailEngine.cs:246`), never as a live read.
- The completion math/guard is intact: verdict at `PassFailEngine.cs:309`
  (`pass = missing == 0 && !dupFail && valueChainOk`); `missing` derived from the ES-binding STARTED vs
  COMPLETE denominator (lines 152-162).
- The non-completed `outcome` corroboration WARNING path is removed (`PassFailEngine.cs:278-280`) — this is the
  intentional T-74-09 accept; it was non-binding corroboration, so its removal cannot affect the gate.
- Metric-facts subsets GREEN per 74-VERIFICATION.md: 45/45 and 22/22; full hermetic suite 668/668.

**Conclusion:** The gate reads the new total, the close-gate fixture invariant (total == completed) holds, and
the binding completion math is unchanged. No false-green path introduced.

---

## Accepted Risks Log

The following 7 threats are accepted by the bounded-label observability design. None introduces new external
attack surface, PII exposure, or unbounded cardinality.

| Threat ID | Category | Rationale for acceptance |
|-----------|----------|--------------------------|
| T-74-01 | D — cardinality (orchestrator) | `orchestrator_messages_*` labels are ONLY `workflowId`+`processorId` (bounded sets); `messageId` is the counting unit, never a label (D-04). No unbounded series. |
| T-74-02 | I — disclosure (orchestrator) | Label values are framework GUIDs (workflowId/processorId), not user/PII data; `messageId` excluded. |
| T-74-03 | D — cardinality (processor) | The unbounded-risk `outcome` dimension was REMOVED this phase; `processor_messages_*` labels reduced to `workflowId`+`processorId`. |
| T-74-04 | I — disclosure (processor) | Label values are GUIDs, not PII. |
| T-74-05 | D — cardinality (keeper) | `keeper_messages_*` labels are ONLY `workflowId`+`processorId`; `keeper_l2_probe` is label-less; `messageId` not a label. |
| T-74-07 | I — disclosure (keeper) | Label values are GUIDs, not PII. |
| T-74-09 | R — repudiation (lost outcome corroboration) | The non-completed-outcome corroboration WARNING was intentionally removed (D-13). It was a non-binding corroboration warning, not an audit gate; primary completion math is unaffected and the dead-run Prom corroboration WARNING (`PassFailEngine.cs:270-276`) survives. |

---

## Unregistered Flags

None. The only SUMMARY with a `## Threat Flags` section (74-01) declares "None — internal observability
refactor; no new endpoints, auth, or trust-boundary surface." Summaries 74-02/03/04 carry no threat flags.

---

## Threats Open

0
