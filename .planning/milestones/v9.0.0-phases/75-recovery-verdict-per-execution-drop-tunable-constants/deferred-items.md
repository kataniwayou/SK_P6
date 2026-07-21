# Phase 75 — Deferred Items

## ✅ RESOLVED — live Docker-up sweep (2026-07-15)

The three deferred live gates below were exercised on a real Docker RealStack via
`scripts/phase-68-sweep.ps1` (all 7 scenarios). **Result: 7/7 PASS.**

**Bootstrap note (rebuild→SourceHash reseed order):** the phase-75 source changes gave the
rebuilt images a new SourceHash (`ae91e700…`) with no `processors` row, so the first sweep
aborted every scenario at `phase-65-reset` STEP 2 heal-wait (exit 20). Fixed with a one-time
graph-delete → `FanOutSeeder` → verified 2 liveness keys + POST /start 204, creating the
persistent processor row `f54670ea…` (preserved across resets + `down`). Re-run then proceeded.

**Per-scenario analyzer verdicts (fresh Release reports):**

| Scenario | Fault | Verdict | Started/Complete | Missing | InFlightLoss |
|----------|-------|---------|------------------|---------|--------------|
| TEST-01 | none (baseline) | Pass | 18/18 | 0 | 0 |
| TEST-02 | processor crash | Pass | 19/19 | 0 | 0 |
| TEST-03 | orchestrator crash | Pass | 16/16 | 0 | 0 |
| TEST-04 | keeper crash (both replicas) | Pass | 19/19 | 0 | 0 |
| TEST-05 | redis crash | Pass | 26/26 | 0 | 0 |
| TEST-06 | rabbitmq crash | Pass | 16/16 | 0 | 0 |
| TEST-07 | redis+rabbitmq | Pass | 14/14 | 0 | 0 |

- **D75-6 (TTL neutralization) — CLOSED / approved:** `InFlightLoss=0` on every non-redis
  scenario (TEST-02/03/04/06) at the new 900s TTL; the Phase-68 TTL-expiry artifact is gone.
- **D75-4 (keeper ES-join) — CLOSED / approved:** TEST-04 keeper whole-tier crash fully
  recovered 19/19 (Missing=0); the analyzer built its per-(corr,exec) keeper-outcome map from
  ES REINJECT logs and produced a pure per-execution verdict (no absolute-count/TTL term).
- **D75-7 (K_EXECUTIONS) — NOT ADOPTED this milestone:** OPTIONAL/default-off seam left off;
  the default 300s wall-clock ran unchanged on all 7 scenarios. Acceptable resolution per below.

**Negative-path FAIL confirmed live (2026-07-15, commit 6e52f13):** the 7/7 PASS sweep proves the
verdict greens on real recovery, but not that it correctly FAILs on a real loss. Added an env-gated
processor latency hook (`PROCESSOR_STEP_DELAY_MS`, default-off) + harness TEST-09 (stop-on-inflight:
RMQ queue-depth-timed kill, no recovery). Result: **Verdict=Fail, StartedRuns=3 CompleteRuns=1
Missing=2**, both misses classified "recoverable-but-lost → binding miss" — the live analogue of the
hermetic fact `RecoverableButLost_NoKeeperDrop_AfterRecovery_Yields_Fail`. Also surfaced a genuine
measurement caveat (TEST-08, stop-only): work the processor never touches emits no Step_* trace, so a
total pre-observability loss reads as PASS — a green verdict certifies recovery of *observably-started*
work, not "nothing was lost."

**Outage-outlasts-window + telemetry false-FAIL (2026-07-15, TEST-10, commit 1221858):** processor
killed on real in-flight backlog, kept down 300s (> the 300s window), then restarted. Verdict=Fail,
StartedRuns=22 CompleteRuns=20 Missing=2. **CORRECTION — the 2 "misses" were NOT data loss.**
Log-capture post-mortem (`CAPTURE_LOGS` hook): the processor emitted all 9 hop-logs for all 22
executions (Step_E1/E2 ×22 in the container log), but the OTLP export dropped the Step_E1/E2 records
for 2 executions on the way to ES (ES received E1/E2 for only 20). Both "missing" executions actually
COMPLETED CORRECTLY — they reached the terminal Step_G with the right value (206) and pass the
value-chain check; only their intermediate E-layer *trace* was lost. So real data loss under a 5-minute
total outage = **ZERO** (all 22 recovered); the FAIL was a **telemetry-completeness loss under load**,
because the analyzer requires the full 9-label trace to call a run complete. This is the mirror image of
the TEST-08 blind spot: trace loss can make completed work look incomplete (false FAIL) just as it can
make lost work invisible (false PASS). **The verdict's trustworthiness ceiling is the OTLP/ES trace
pipeline, not the recovery machinery** — the data path is stronger than any single verdict implied.

**TEST-01 first-pass note:** on the initial full sweep TEST-01 (scenario #1) failed against a
cold-started Elasticsearch — pipeline conservation was perfect (`orch_consumed=proc_sent=188,
gap=0`) but the analyzer's ES query returned `StartedRuns=0` before trace docs indexed. A
warm-ES single re-run (`-ScenarioIds TEST-01`) passed 18/18. Not a recovery regression (no-fault
baseline cannot regress recovery). **Minor tooling bug observed:** the sweep roll-up's
`Get-ChildItem -Recurse -Filter "$id.json" | Select -First 1` read a month-old stale Debug report
(StartedRuns=0) instead of the fresh Release one — cosmetic; the harness exit code (analyzer
verdict) is authoritative.

---

## Original deferred entries (superseded by the RESOLVED section above)


## Out-of-scope pre-existing failures (Docker-less sandbox)

**Discovered during:** 75-01 Task 2 full-hermetic-suite verification (2026-07-15)

**Observation:** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` reports
810 total / 538 passed / **272 failed**. All 272 failures are broker-connection
failures (`Connection Failed: rabbitmq://localhost/` and `rabbitmq://rabbitmq/`,
892 such lines in the run) — MassTransit/RabbitMQ-dependent tests that carry a
broker dependency but are NOT tagged `Category=RealStack`, so they run under the
hermetic filter and fail with no broker present in the Docker-less sandbox.

**Scope determination:** OUT OF SCOPE for 75-01. This plan's change is confined to
the pure in-process `PassFailEngine` / `AnalyzerReport` / `PassFailEngineFacts`
(no messaging, no network). Zero analyzer facts appear in the 272 failures; all
28 `*PassFailEngineFacts` + `*PassFailEngineValueChainFacts` are GREEN.

**Precedent:** Consistent with STATE.md — phases 68/73/74 ran their live/broker
close gates "deferred-automated" when Docker is unavailable. These broker-bound
hermetic-filter tests are environmental, not introduced by this phase.

**Action:** None taken (do NOT auto-fix — pre-existing, unrelated files). The live
`phase-68-sweep.ps1` / RealStack analyzer gate for D75-4/D75-6 remains
deferred-automated pending a Docker-up environment.

## DEFERRED: 75-03 live TTL-neutralization verify (D75-6, checkpoint:human-verify)

**Deferred during:** 75-03 Task 2 (`checkpoint:human-verify`, gate="blocking") — 2026-07-15

**Why deferred:** The TTL neutralization is only OBSERVABLE on a running Docker
RealStack (redis/rabbitmq/elasticsearch/prometheus + orchestrator/processor/keeper).
Docker is NOT available in this sandbox — established deferred-automated precedent
(phases 68/73/74 live close gates). The AUTOMATED half of 75-03 is FULLY done and
committed (`e8e9732`): all TTL knobs raised 300→900 in lock-step, Debug+Release
0-warning, the two coupled hermetic facts (`ProcessorOptionsBindingFacts`,
`ComposeYamlFacts`) GREEN.

**Knobs raised to 900 (verify these are the effective values on the live stack):**
1. `src/BaseProcessor.Core/Configuration/ProcessorLivenessOptions.cs` `ExecutionDataTtlSeconds = 900`
2. `src/Keeper/RecoveryOptions.cs` `ExecutionDataTtlSeconds = 900`
3. `src/Orchestrator/Configuration/OrchestratorOutputOptions.cs` `OutputDataTtlSeconds = 900`
4. `src/Processor.Sample/appsettings.json` `ExecutionDataTtl: 900`
5. `src/Processor.BadConfig/appsettings.json` `ExecutionDataTtl: 900`
6. `compose.yaml` `Processor__ExecutionDataTtl: "900"` on **both** processor-sample
   and processor-badconfig (this env override binds OVER appsettings + the Options
   default at container runtime — Pitfall 3; it is the value the LIVE containers read).

**Exact RealStack steps a future Docker-up run MUST perform to close this gate:**
1. Rebuild the containers so the new appsettings/env TTL is baked in:
   `pwsh -File scripts/phase-65-up.ps1` (or the standard compose-up). Confirm the
   effective `Processor__ExecutionDataTtl=900` is picked up (e.g.
   `docker inspect sk-processor-sample | grep ExecutionDataTtl` or a container log).
2. Run the non-redis fault scenarios:
   `pwsh -File scripts/phase-68-sweep.ps1 -ScenarioIds TEST-02,TEST-03,TEST-04,TEST-06`.
3. **Expected:** each scenario shows ZERO TTL-manufactured in-flight loss — the
   previously-observed TTL-expiry artifact (e.g. the Phase-68 TEST-06 self-expiry at
   5s/300s vs the 45s outage) is gone; the analyzer report's InFlightLoss for these
   scenarios is driven ONLY by genuine recovery timing, not by TTL. The jittered
   `random[900,1800]` floor must comfortably exceed the ~300s window (dwell 45s +
   return-to-healthy + keeper reinject + drain 60s + poll-to-stable ≤60s).
4. Redis-crash TEST-05/07 are MOOT for TTL (redis is the wiped store — in-flight-at-
   wipe durability boundary); do not expect a TTL effect there.

**Resume-signal to record when run:** "approved" (TTL no longer manufactures loss for
TEST-02/03/04/06), or describe any TTL-manufactured loss still observed.

## DEFERRED: 75-05 live execution-based-window verify (D75-7, OPTIONAL, checkpoint:human-verify)

**Deferred during:** 75-05 Task 2 (`checkpoint:human-verify`, gate="blocking") — 2026-07-15

**Why deferred:** D75-7 is explicitly OPTIONAL and default-OFF ("Optionally make the
observation window execution-based"). Its live behaviour (the observe loop stopping at
K distinct executions instead of the 300s wall-clock) is only OBSERVABLE on a running
Docker RealStack (redis/rabbitmq/elasticsearch/prometheus + orchestrator/processor/
keeper). Docker is NOT available in this sandbox — established deferred-automated
precedent (phases 68/73/74; 75-03 live TTL gate above). The AUTOMATED half is FULLY
done and committed (`90fb59f`): the `K_EXECUTIONS` seam is added to
`scripts/phase-67-harness.ps1`, default-off, hard-capped by the unchanged 300s
wall-clock, and the script parses (`PARSE_OK`). The script-parse gate is the automated
proof of correctness for the seam itself; the default-off path is byte-for-byte
unchanged when `K_EXECUTIONS` is unset.

**Exact RealStack steps a future Docker-up run MAY perform to exercise the OPTIONAL path:**
1. Bring the stack up: `pwsh -File scripts/phase-65-up.ps1` (or the standard compose-up).
2. Run a scenario with the seam SET:
   `$env:K_EXECUTIONS = "8"; pwsh -File scripts/phase-68-sweep.ps1 -ScenarioIds TEST-01`.
   **Expected:** the observe loop (STEP F.5) closes the window early once 8 distinct
   executions are observed (`Get-FireCount - $fireBaseline >= 8`), OR the 300s window
   deadline, whichever comes first — the harness logs
   `OPTIONAL execution-based window: observed N >= K=8 ... closing window early`.
   The verdict is UNCHANGED IN KIND (still a pure per-execution recovery function —
   K only bounds WHICH executions are in the window, not the pass/fail rule).
3. Run WITHOUT the seam (`Remove-Item Env:K_EXECUTIONS`) to confirm the default 300s
   wall-clock path is unchanged (window closes at 300s, no early-break log line).
4. Clear the seam afterwards (the harness already clears it in its `finally`, but a
   parent-shell `$env:K_EXECUTIONS` set interactively must be removed by the operator).

**Acceptable resolution:** Because D75-7 is explicitly OPTIONAL and MUST NOT block the
verdict rework, "deferred / not adopted this milestone (seam left default-off)" is an
acceptable resolution — the seam exists, default-off, ready if wanted.

**Resume-signal to record when run:** "approved", "deferred-automated — Docker
unavailable", or "not adopted this milestone (seam left default-off)".

## DEFERRED: 75-04 live keeper ES-join verify (D75-4, checkpoint:human-verify)

**Deferred during:** 75-04 Task 2 (`checkpoint:human-verify`, gate="blocking") — 2026-07-15

**Why deferred:** The keeper→analyzer ES-join is only OBSERVABLE on a running Docker
RealStack (elasticsearch carrying real keeper REINJECT drop/success logs +
orchestrator/processor/keeper producing recoverable-vs-clean-drop executions). Docker
is NOT available in this sandbox — established deferred-automated precedent (phases
68/73/74; 75-03/75-05 live gates above). The AUTOMATED half is FULLY done and committed
(`2aa63e5`): the ES field-path const, the keeper-outcome `_search` body, the defensive
per-(corr,exec) outcome-map builder with the "reinject"-wins tie-break, the `TraceCohort`
field, and the `keeperOutcomeByExecution: cohort.KeeperOutcomeByExecution` wiring into
`PassFailEngine.Analyze`. Debug+Release 0-warning; all 34 `*PassFailEngineFacts` +
`*PassFailEngineValueChainFacts` + `*ReinjectConsumerFacts` GREEN (Plan 01 already proves
the classifier semantics with synthetic keeper maps; Plan 02 proves the keeper emits the
join fields via a capturing logger). The compile gate + those hermetic facts are the
automated proof for the join wiring; the LIVE surfacing of the keeper attributes in ES
is the only piece needing Docker.

**Exact RealStack steps a future Docker-up run MUST perform to close this gate:**
1. **Open-Question-1 probe FIRST (level-filter guard):** inject a keeper clean-absent DROP
   (a non-redis scenario where `L2[entryId]` is gone at reinject time), then query ES for
   the keeper doc and confirm `attributes.ReinjectOutcome` (="drop") + `attributes.CorrelationId`
   / `attributes.ExecutionId` actually LAND. The drop log is a `LogWarning` — verify the
   keeper's console log level does NOT filter Warning out before OTLP export (cross-check
   `LogLevelFilterTests` / the keeper's effective `Logging:LogLevel`). If the attributes do
   not surface, the join silently reads an empty map (every incomplete run then binding-FAILs)
   — the probe catches that before a full sweep.
   Example query:
   `GET http://localhost:9200/logs-generic.otel-default/_search` with
   `{ "query": { "exists": { "field": "attributes.ReinjectOutcome" } } }`
   → expect ≥1 hit carrying `attributes.ReinjectOutcome` + `CorrelationId` + `ExecutionId`.
2. Rebuild the containers so the Plan-02 keeper log widening is baked in (SourceHash reseed
   order per MEMORY: graph-delete → seed → start after a rebuild), then bring the stack up:
   `pwsh -File scripts/phase-65-up.ps1` (or the standard compose-up).
3. Run a full sweep: `pwsh -File scripts/phase-68-sweep.ps1`.
4. **Expected:**
   - A scenario with a **recoverable-but-lost** execution (keeper COULD have reinjected —
     L2 blob present, no clean-drop log; or a reinject-then-still-missing) → **binding FAIL**
     (the `"reinject"`-wins tie-break ensures a recovered execution is never tolerated as a
     clean drop).
   - A scenario where the keeper **clean-dropped** a gone execution (`attributes.ReinjectOutcome="drop"`)
     → **tolerated** (reported + cause-labeled in `AnalyzerReport.UnrecoverableLossDetail`),
     NOT a pass/fail lever.
   - The verdict contains NO absolute in-flight count / window-seconds / cron-rate term
     (D75-2 already grep-clean in the engine).
5. Confirm the join is ES-ONLY (no Redis probe added to the fixture) — already grep-verified
   in the automated half (`IDatabase`/`redis`/`StringLength` appear only in disclaiming
   doc-comments, never as a call).

**Resume-signal to record when run:** "approved" (keeper attributes surfaced; recoverable-
but-lost → FAIL and clean-drop → tolerated as expected), "deferred-automated — Docker
unavailable", or describe any keeper attribute that failed to surface / any misclassification.
