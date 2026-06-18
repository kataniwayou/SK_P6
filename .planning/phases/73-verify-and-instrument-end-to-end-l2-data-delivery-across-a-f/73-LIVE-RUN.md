---
phase: 73
slug: verify-and-instrument-end-to-end-l2-data-delivery-across-a-f
doc: LIVE-RUN
status: passed
ran: 2026-06-18
scenario: TEST-01 (no-fault baseline)
harness: scripts/phase-67-harness.ps1
verdict: PASS (analyzer exit 0)
---

# Phase 73 — Live RealStack Run (Deferred-Automated Gate, Executed)

The phase locked the live Docker round-trip as **deferred-automated** (the planning sandbox had no
Docker). It has now been **executed on a real local Docker stack** and **passes end-to-end**. This doc
records the run, the result, and the three live-only auditor bugs the run surfaced and fixed.

## How it was run

```
pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-01
```

The phase-67 harness is the canonical live driver (the phase-73 seeder + auditor fixtures are the
phase-73-extended ones, so TEST-01's no-fault baseline IS the phase-73 happy-path value-chain + fan-in
proof). Full pipeline, single command: `docker compose build` → `up -d --force-recreate` →
`phase-65-up.ps1` health gate → `phase-65-reset.ps1` (FLUSHALL + FK-safe graph DELETE) → orchestrator
clean-restart → seed the G-extended DAG → `POST /orchestration/start` (204) → 5-min observation window
(cron fires every 30 s) → ES-read-only auditor (window-pinned) → `docker compose down` (volumes kept).

**Image rebuild was essential:** the long-running containers predated the phase-73 `SampleProcessor`
change, so STEP A0 (`docker compose build`) + force-recreate were required for the live processors to
emit the deterministic seeds + the `received → produced` log the auditor expects.

## Result — PASS (analyzer exit 0)

Canonical run report: `analyzer-reports/phase-73-canonical-harness-TEST-01.json`

```
[TEST-01] Pass: 18/18 started runs complete — every started run complete,
          no illegitimate duplicate, value-chain intact (ES-binding)
```

| Property (live, real stack: RabbitMQ / Redis / Postgres / Elasticsearch / 2× processor) | Result |
|------|--------|
| Cron triggers in window | **10 fires** (10 distinct correlationIds) |
| Started executions (2 spawned per fire) | **18 in-window**, all complete (2 straddled the window edge) |
| Completeness | **18/18**, 0 missing — zero silent loss |
| Deterministic value chain | **`ValueChainOk: true`** — seed `100 → Step_G 106`, seed `200 → Step_G 206` (every hop `seed + hop-count`) |
| Convergent `Step_G` fan-in | reached **×2 per execution**, both arrivals, `Duplicates: []` (correctly not flagged) |
| Trip duration (ES `@timestamp` span, `Step_B → Step_G`) | per-exec **~38 ms median** (32–62 ms); per-corr **~39 ms median** |
| Prom corroboration | non-binding WARNING (counter-reset artifact — metrics deferred this phase) |

A second **window-pinned** re-run over live data independently confirmed PASS
(`analyzer-reports/phase-73-live-TEST-01-PASS.json`: 16/16 complete, `ValueChainOk: true`, seeds 100/200,
trip duration populated).

## Live-only bugs found & fixed

The live run exposed **three real auditor-model bugs** that the hermetic synthetic suite structurally
could not catch (it uses all-10 labels and never exercises ES `@timestamp`). None were in the
data-delivery path — all were in the auditor / instrumentation layer. Each was diagnosed against the
live ES `_source` directly, not guessed.

| # | Symptom | Root cause | Fix | Commit |
|---|---------|-----------|-----|--------|
| 1 | Audit aborted (`DispatchSentDelta = -2337`) | Harness orchestrator-restart resets `orchestrator_dispatch_sent_total`; the pre-restart container's series lingers in Prometheus's ~5-min instant-query lookback → the windowed delta sums **negative** | Anchor the fire-precondition on the **ES started-run count** (`traces`), not the raw Prom delta — per the guard's own documented intent (only a true no-fire = no Prom delta AND no ES traces) | `5f9b3d6` |
| 2 | `0/N` runs complete | Completeness required all 10 labels incl. `Step_A` per `(corr, exec)`, but `Step_A` is the **shared Mode-2 entry/seed** — logged once per correlationId with `executionId = Guid.Empty` (no per-execution log, no `Produced`), so it is filtered out of the per-execution `Step_*` cohort and never appears in a per-`(corr,exec)` trace | Per-execution completeness = the **9 hops `B…G`** (`IsComplete` strips the optional `Step_A`), consistent with the value-chain side, which already treats `Step_A` as the optional offset-0 seed source (`ResolveSeed` falls back to `Produced[Step_B] - 1`). Refines D-10's "10-label DAG" to its 9 observable per-execution hops | `a4e6e8e` |
| 3 | Trip duration maps empty | Live `@timestamp` is epoch-**milliseconds rendered as a numeric string** (e.g. `"1781754330089.184100"`), which `DateTimeOffset.TryParse` rejects → every span dropped | Parse the numeric string as epoch-ms first; ISO-8601 fallback retained | `a4e6e8e` |

**Investigation note (bug 2):** confirmed *not* a regression by querying live ES — every `Step_A` hit
carries an empty `ExecutionId` and no `Produced`, while `Step_B..Step_G` carry minted executionIds +
`Produced` values (`Step_B`: `received 100 produced 101` / `received 200 produced 201`). `Step_A` is the
shared entry by design; its correctness is proven via the value-chain seed, not the hop set.

## Verification after fixes

- **Live:** PASS twice (canonical full harness exit 0; window-pinned re-run).
- **Hermetic suite (RealStack excluded):** **665 / 665 passed, 0 failed** in ~3 min. Affected facts:
  `PassFailEngineFacts 9/9`, `PassFailEngineValueChainFacts 10/10`, `FanInHermeticHarnessFacts 5/5`,
  `SampleProcessorFacts 4/4`. Builds 0-warning (`-warnaserror`, Debug + Release).
- **Single-`src`-edit invariant** held throughout — all live-run fixes are in test/auditor code; no
  `src/` change beyond the original `SampleProcessor.cs` edit.

## Reproduce

```
# Full live proof (needs Docker):
pwsh -File scripts/phase-67-harness.ps1 -ScenarioId TEST-01        # exit 0 == PASS

# Hermetic suite (no infra) — run the EXE directly to avoid the flaky dotnet-test MTP pipe on Windows:
tests/BaseApi.Tests/bin/Debug/net8.0/BaseApi.Tests.exe --filter-not-trait "Category=RealStack" --timeout 8m
```

## Known follow-up (out of this phase's scope)

- **WR-01 (open by design):** the live value-chain pins inter-hop `+1` **deltas**, not the absolute base
  (the seed is recovered as `Produced[Step_B] - 1`); the absolute-value anchor (`Step_G` at 106/206 vs the
  fixed 100/200 seeds) is owned by the hermetic harness. Surfacing the seed on the `Step_A` entry log would
  give the live path an independent absolute anchor — but that needs a `SampleProcessor` `src/` change,
  out of this phase's single-edit scope.

---

*Phase 73 deferred-automated live gate: executed 2026-06-18, verdict PASS.*
