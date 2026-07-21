# Phase 79: Falsification harness for the resilience sweep (negative-control) - Context

**Gathered:** 2026-07-17
**Status:** Ready for planning
**Source:** In-conversation design discussion (grounded by a codebase scout of the reinject/recovery seam + analyzer classification)

<domain>
## Phase Boundary

**In scope:** Build a **negative control** that proves the resilience-sweep pass/fail gate has teeth — i.e. that a True Positive fires and there is no silent-green False Negative. Concretely:

1. A **default-off, env-gated lossy injection seam** in keeper product source that **defeats recovery for exactly one `{correlationId, executionId}`** so it registers as **recoverable-but-lost** (a binding miss → `VERDICT_FAIL`) — NOT a clean keeper-drop, NOT a tolerated in-flight-at-wipe loss.
2. The injected loss must produce ES telemetry **byte-identical to a real recoverable-but-lost strand** (Step_A present, no terminal Step_G, L2 data present, keeper `"reinject"` log present so the WR-01 veto fires, started-after-`RECOVERY_UTC`).
3. A **one-scenario harness** (`scripts/phase-79-falsify.ps1`, mirroring `scripts/phase-67-harness.ps1`) that runs the injected scenario and **asserts `scripts/phase-68-sweep.ps1` flips that scenario to `VERDICT_FAIL`** — confirming the gate correctly fails a genuinely-lost execution.

**This validates the OBSERVER/GATE, not the system's recovery.** The seam deliberately breaks recovery to manufacture a detectable loss; the phase proves the analyzer + sweep *detect* it.

**Out of scope (deferred, follow-on):**
- The **missing-count** negative-control axis (silent drop / never-started work).
- The **duplicate-count** negative-control axis (effect-more-than-once).
- Only the **recoverable-but-lost binding axis** is built in this phase.

**Why now:** Phase 78 reworked the analyzer to score purely from the Phase-77 framework logs and re-proved all 7 scenarios PASS. A green capstone is only trustworthy if we can show the gate also goes RED on a real loss. Phase 79 is that proof — the negative control the milestone was missing.
</domain>

<decisions>
## Implementation Decisions (LOCKED)

### The gate mechanism this exploits (grounding for all decisions)
The pure verdict engine (`PassFailEngine.Analyze`, `PassFailEngine.cs:186-258`) tolerates a started-but-incomplete run ONLY via:
- **path (a)** `keeperCleanDrop` = keeper outcome `"drop"` (provably unrecoverable), or
- **path (b)** `redisWipeInFlight = stalledBeforeRecovery && !startedAfterRecovery && !keeperReinject` (in-flight-at-wipe, D75-5).

The **WR-01 veto** (`keeperReinject` = keeper outcome `"reinject"`) *suppresses path (b)*. Therefore an execution that started (Step_A), never completed (no terminal Step_G), has **L2 data present**, carries a keeper **`"reinject"` log**, and **started after `RECOVERY_UTC`** cannot be tolerated by either path → `bindingMissing` → `Verdict.Fail`. That is exactly the classification the seam manufactures, and it is byte-identical to a genuine "keeper logged reinject, redispatch lost in transit" strand.

### Seam & Injection Mechanism

- **D-01 — Seam location & defeat mechanism: keeper suppress-send.**
  Add a new **default-off, env-gated hook inside `ReinjectConsumer.HandleAsync`** (`src\Keeper\Recovery\ReinjectConsumer.cs:30`). When gated and targeting an execution, the keeper still:
  - reads L2 (`StringLengthAsync(ExecutionData(EntryId)) != 0` → data present), and
  - emits the `REINJECT sent {MessageId} "reinject"` log (`ReinjectConsumer.cs:85`) so `attributes.ReinjectOutcome == "reinject"` reaches the analyzer and the WR-01 veto fires,
  but then **suppresses the actual re-dispatch `SendAsync`** to `queue:{ProcessorId:D}` for the targeted execution. Net effect: keeper says "reinject", but no message reaches the processor → the execution never completes → binding miss.
  - This is the **faithful, deterministic** reproduction of a recoverable-but-lost strand. Rejected alternatives: processor-side drop of the reinjected hop (post-recovery loss, muddier shape, may not cleanly hit the WR-01 path); reuse of the TEST-09 real-crash timing (non-deterministic, whole-tier crash — the exact thing this phase improves on).
  - **This touches keeper PRODUCT source** — a deliberate, accepted departure from Phase 78's "TEST + scripts only" posture. It follows the ONE established env-gated product-code precedent, `PROCESSOR_STEP_DELAY_MS` (`src\Processor.Sample\SampleProcessor.cs:42-52`): env read in code + `${VAR:-0}` compose plumbing + harness export. Default-off ⇒ inert in every normal run.

- **D-02 — Target exactly one execution: one-shot latch + `K_EXECUTIONS=1`.**
  The seam uses a **one-shot latch** (an `Interlocked`/`static` flag) that defeats the **first** reinject it observes while gated, then goes inert for the rest of the process lifetime — so exactly one execution is defeated by count, regardless of which. Combined with **`K_EXECUTIONS=1`** (the existing execution-based observation-window seam, `phase-67-harness.ps1:309`) so the window holds a single execution and "exactly one lost" is unambiguous end-to-end.
  - Rejected: targeting by a specific `correlationId|executionId` passed via env — the ids are runtime-generated, requiring a brittle pre-lookup.
  - The env-var family mirrors the existing `SCENARIO_ID` / `K_EXECUTIONS` seams named in the ROADMAP.

### Assertion Contract

- **D-03 — Assert the RIGHT reason, not just any red.**
  The falsification harness asserts:
  1. the per-scenario **harness exit == 1** (`VERDICT_FAIL`, per the Phase 67 D-04 exit-code table — NOT 2/INCONCLUSIVE, NOT an infra abort 10-70/64), AND
  2. the analyzer report for the scenario shows **`Missing >= 1`** with `MissingDetail` naming *"recoverable-but-lost → binding miss"* (`AnalyzerReport` fields, engine detail at `PassFailEngine.cs:252-257`).

  This distinguishes a correct True-Positive from an accidental FAIL caused by an infra abort or a duplicate. The meta-test PASSES iff the gate correctly FAILED the injected scenario **for the recoverable-but-lost reason**.

- **D-04 — Packaging: new out-of-band scenario + standalone driver, driven through the sweep.**
  - Add a new scenario row **`FALSIFY-01`** to the harness `$Scenarios` `[ordered]` table (`phase-67-harness.ps1:95`) with a new `faultType = 'inject-recovery-loss'`, following the TEST-08/09/10 "negative-path, not in default sweep" convention.
  - The default `phase-68-sweep.ps1` id list stays **`TEST-01..07` — byte-unchanged**. `FALSIFY-01` is never in the default capstone.
  - Standalone **`scripts/phase-79-falsify.ps1`** drives the single scenario, holds the inverted assertion (D-03), AND — per the ROADMAP's literal wording — invokes **`phase-68-sweep.ps1 -ScenarioIds FALSIFY-01`** and asserts the roll-up summary row `verdict == "Fail"` (class `VERDICT_FAIL`) and the wrapper exits non-zero. Both the harness-exit level and the sweep-summary level are asserted.

### Scope & Inertness

- **D-05 — Axis scope guard: recoverable-but-lost binding axis ONLY.** Missing-count and duplicate-count negative controls are deferred to follow-on phases (see Deferred Ideas).

- **D-06 — Inertness guarantee (non-negotiable).** The new keeper env var defaults off (unset/empty ⇒ no behavior change), mirroring `PROCESSOR_STEP_DELAY_MS`'s `${VAR:-0}` default. The 7-scenario capstone must be provably unaffected: a clean `phase-68-sweep.ps1` (TEST-01..07) with the seam present-but-unset must still reproduce 7/7 PASS. The hermetic build stays green (the seam is Docker-gated live-only; no hermetic fact depends on it unless the planner adds one to pin the latch logic).

### Claude's Discretion
- Exact env-var name for the new seam (suggested: `KEEPER_DEFEAT_REINJECT` or similar `KEEPER_*` name; must be default-off and `${VAR:-0/false}`-plumbed in `compose.yaml` for the keeper service).
- Whether the one-shot latch is a `static Interlocked` field vs an injected singleton state holder — planner's call, provided it defeats exactly one reinject per process and is threadsafe across the two keeper reinject consumers/replicas.
- Whether to add a hermetic fact pinning the latch/suppress-send branch (recommended if cheap — proves the seam logic without Docker), and the exact `MissingDetail` string match (grep-exact vs contains).
- Task decomposition, commit granularity, and the precise `phase-79-falsify.ps1` exit-code table (mirror the Phase 67 table; the falsification harness's OWN exit 0 means "the gate correctly failed the injected scenario").
- The `notes`/`dwellSeconds`/`injectAfterNFires` field values for the `FALSIFY-01` row.
</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### The seam target (keeper recovery path — where the hook lands)
- `src\Keeper\Recovery\ReinjectConsumer.cs` — `HandleAsync` (`:30`); L2-presence decision (`:45-47`); success re-dispatch + `REINJECT sent … "reinject"` log (`:63-78`, log `:85`); drop branch + `REINJECT drop … "drop"` log (`:48-60`, log `:59`). **The env-gated suppress-send hook goes here.**
- `src\Keeper\Recovery\RecoveryConsumerBase.cs` — shared `Consume` funnel + `Guard` RetryLoop (`:36-54`).
- `src\Processor.Sample\SampleProcessor.cs` — `:42-52` the `PROCESSOR_STEP_DELAY_MS` env-gated fault-hook **precedent to copy** (env read + default-off + comment style).
- `src\Messaging.Contracts\KeeperReinject.cs`, `src\Messaging.Contracts\Projections\L2ProjectionKeys.cs` — the reinject envelope + L2 key shapes.

### The gate being validated (analyzer classification — what must flip to FAIL)
- `tests\BaseApi.Tests\Observability\Analysis\PassFailEngine.cs` — `Analyze` (`:104`); started-but-incomplete classification, tolerance paths (a)/(b), WR-01 veto, binding-miss detail (`:186-258`); verdict (`:329-349`).
- `tests\BaseApi.Tests\Observability\AnalyzerE2ETests.cs` — `Analyze_Window_Yields_Pass` fixture (`:109`); `RECOVERY_UTC` read (`:233-234`); `BuildKeeperOutcomeMap` with `"reinject"`-wins tie-break (`:417-444`).
- `tests\BaseApi.Tests\Observability\Analysis\AnalyzerReport.cs` — report fields (`Verdict`, `Missing`, `InFlightLoss`, `Duplicates`, `MissingDetail`, `UnrecoverableLossDetail`).
- `tests\BaseApi.Tests\Observability\Analysis\PassFailEngineFacts.cs` — hermetic facts exercising the recoveryUtc/firstHop/lastHop tolerance-vs-binding branches (e.g. `:206-256`, `:295-324`) — the pattern for any new hermetic pin of the seam.

### The harness family to mirror + drive
- `scripts\phase-67-harness.ps1` — the per-scenario harness to mirror; `$Scenarios` table (`:95`), env seams `SCENARIO_ID`/`WINDOW_*`/`RECOVERY_UTC`/`K_EXECUTIONS` (STEP H, `:504-515`), execution-based window (`:309`), exit-code table (`:31-44`), STEP-H analyzer invocation (`:504-528`).
- `scripts\phase-68-sweep.ps1` — the roll-up wrapper; `-ScenarioIds` param (`:48-49`), per-row `verdict`/`class`/`harnessExit` (`:101-110`), capstone exit (`:126-132`). **Driven by `phase-79-falsify.ps1` for the sweep-level assertion.**
- `scripts\lib\exit-code-resolution.ps1` — `Resolve-AnalyzerExitCode` / `Resolve-SweepClass` (shared verdict-class mapping the assertion reads).
- `compose.yaml` — `${PROCESSOR_STEP_DELAY_MS:-0}` plumbing pattern (`:293`,`:330`) to copy for the new keeper env var on the keeper service.

### Prior-phase context (the log shape + classification this builds on)
- `.planning\phases\78-*\78-CONTEXT.md` — the analyzer rework (structural stepId completeness + ANL-03 redundancy + metric gate; value oracle deleted); the D-02 keeper `ReinjectOutcome` discrimination.
- `.planning\phases\77-*\77-CONTEXT.md` — the framework-logging model; the stripped `REINJECT {sent|drop} {MessageId} {ReinjectOutcome}` templates + Tier-1-via-scope that make the keeper record analyzer-visible.
- `.planning\phases\76-*\76-SPEC.md` — FW/ANL record + analyzer-branch definitions (WR-01/ANL-03 origins).
</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- **`PROCESSOR_STEP_DELAY_MS` fault-hook** (`SampleProcessor.cs:42-52`) — the exact env-gated-product-seam pattern to replicate for the keeper hook (code read + `${VAR:-0}` compose + harness export + "NEVER in production" comment).
- **`K_EXECUTIONS` execution-based window** (`phase-67-harness.ps1:309,445-454`) — already bounds the window to K observed executions; set `K_EXECUTIONS=1` to isolate a single execution. No new harness machinery needed.
- **`BuildKeeperOutcomeMap` + WR-01 veto** (`AnalyzerE2ETests.cs:417-444`, `PassFailEngine.cs:230-234`) — the existing classification path the seam exploits; the analyzer already forces a reinject-logged-but-incomplete run to a binding miss. No analyzer change required for the FAIL to fire.
- **`$Scenarios` table + negative-path convention** (`phase-67-harness.ps1:95-107`) — TEST-08/09/10 show how to add a negative scenario kept out of the default sweep; `FALSIFY-01` follows the same shape.
- **`Resolve-SweepClass` / exit-code table** — reuse for the falsification harness's own classification + assertion.

### Established Patterns
- Env-gated product seams are DEFAULT-OFF and documented as harness-only, never production (the `PROCESSOR_STEP_DELAY_MS` comment is the template).
- Keeper reinject logs emit inside a MEL execution scope stamping Tier-1 ids as ES `attributes.*` — the suppress-send hook must fire AFTER the log/scope so telemetry stays byte-identical to a real loss.
- Test host is **net8.0**; run `BaseApi.Tests.exe` directly (`dotnet test` hangs on Windows MTP); `--filter-not-trait Category=RealStack` for the hermetic subset (see [[hermetic-test-command]]).

### Integration Points
- The seam is a keeper-container behavior change gated by a compose-plumbed env var; the falsification harness exports it (like `PROCESSOR_STEP_DELAY_MS`) before STEP A and clears it in `finally`.
- The live path is otherwise unchanged: clean → seed → 204-gate → observe (K=1) → the seam defeats the one reinject → analyze → the analyzer emits `Verdict.Fail` for the right reason → teardown.
- Live reseed order matters: if any product source (keeper) changed, rebuild host configs + `docker compose build` → graph-DELETE → seed → 204 → only then run (see [[rebuild-sourcehash-reseed-order]]).
</code_context>

<specifics>
## Specific Ideas

- One-line intent: **manufacture one byte-identical recoverable-but-lost strand by making the keeper log `"reinject"` but suppress the redispatch, then prove the sweep flips that scenario to `VERDICT_FAIL` for the right reason.**
- The falsification harness's OWN exit 0 means "the gate correctly went RED" — the assertion is INVERTED relative to `phase-67-harness.ps1` (which asserts PASS).
- Scenario id: **`FALSIFY-01`** (self-documenting, clearly out-of-band; preferred over `TEST-11`).
- Watch the known live-gate traps: stale Debug analyzer report read ([[phase-68-sweep-stale-report-read]] — trust the harness exit code), and TEST-01/cold-ES INCONCLUSIVE flake (re-run deliberately; INCONCLUSIVE ≠ FAIL). For `FALSIFY-01` the assertion must require exit **1** specifically, not merely non-zero, so an INCONCLUSIVE (2) or infra abort does not masquerade as a passing negative control.
- REQUIREMENTS.md does not exist for this milestone; requirements tracked via ROADMAP + this CONTEXT (phase_req_ids = null).
</specifics>

<deferred>
## Deferred Ideas

- **Missing-count negative-control axis** — a seam proving the gate catches never-started / silently-dropped work (a distinct injection point and a distinct tolerated-vs-binding boundary). Follow-on phase.
- **Duplicate-count negative-control axis** — a seam proving the gate catches effect-more-than-once (dedup defeat). Follow-on phase.
- **Multi-execution / count-parameterized falsification** — defeating N>1 executions to prove the Missing count is exact (not just non-zero). Only after the single-execution axis is proven.

None else — discussion stayed within phase scope.
</deferred>

---

*Phase: 79-falsification-harness-for-the-resilience-sweep-negative-cont*
*Context gathered: 2026-07-17 via in-conversation design discussion*
