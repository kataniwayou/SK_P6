---
phase: 84
slug: byte-data-channel-widening
status: draft
nyquist_compliant: true
wave_0_complete: false
created: 2026-07-21
---

# Phase 84 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> **Unusual gate (locked D-05):** the SOLE acceptance gate is the existing 7-scenario fault-recovery sweep reproducing its all-PASS baseline. No new test suite is added as the gate. The existing hermetic facts must stay green (they are the fast feedback loop), but the phase *verdict* is the sweep + the byte-for-byte equivalence invariant.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | xUnit (hermetic facts) + PowerShell sweep harness |
| **Hermetic runner** | `tests/BaseApi.Tests/bin/{Debug,Release}/net8.0/BaseApi.Tests.exe` run DIRECTLY (NOT `dotnet test` — hangs on Windows MTP) |
| **Hermetic filter** | `--filter-not-trait Category=RealStack` |
| **Terminal gate** | `scripts/phase-68-sweep.ps1` (7 scenarios TEST-01..TEST-07) |
| **Estimated runtime** | hermetic ~seconds; sweep ~2h (run DETACHED via Start-Process + watcher) |

---

## Sampling Rate

- **After every task commit:** build 0-warning Debug + Release; run the affected hermetic subset directly (`BaseApi.Tests.exe --filter-not-trait Category=RealStack`).
- **After every wave:** full hermetic run — confirm zero NEW failures vs the Docker-less baseline (~272 pre-existing infra failures: RabbitMQ/Postgres/Redis connection; 0 analyzer/keeper LOGIC facts among them).
- **Before phase verification:** the 7-scenario sweep reproduces all-PASS (every `Missing==0`, every `Duplicates==0`) — see runbook notes below.
- **Max feedback latency:** hermetic seconds; sweep is the terminal (once) gate.

---

## Validation Architecture (from RESEARCH.md)

**Invariant under test (DATA-03):** the existing string/JSON path is byte-for-byte equivalent — a value written as a string equals its UTF-8 bytes read back. `Processor.Sample` (UTF-8 JSON only) stays green, so every sweep scenario reproduces its exact prior baseline.

**What proves the widening changed nothing observable:**
1. **Existing hermetic facts stay green** (compile + pass after the byte[] migration, using the D-02 helpers / `SequenceEqual` where a fact asserts on `.Data`). This is the fast, per-commit loop.
2. **The 7-scenario sweep reproduces all-PASS** (DATA-04) — the terminal gate. TEST-02..07 (fault scenarios) exercise the recovery round-trip, proving `byte[]` Data survives `KeeperInject`/`OrchestratorInject` over the RabbitMQ wire losslessly (DATA-05).

**Optional Wave-0 hermetic facts (from research, non-blocking — convert two assumptions to VERIFIED):**
- A1: a fact asserting a `DataResult` with `byte[]` Data round-trips losslessly through the MassTransit/STJ serializer (base64 on the wire).
- A2: a fact asserting `JsonDocument.Parse(byte[])` throws `JsonException` on invalid-UTF8 bytes (confirms D-04 routes bad bytes → business `Failed` via the existing catch).

---

## Per-Task Verification Map

> Filled during planning/execution. Each task's acceptance is: (a) affected hermetic subset green + 0-warning Debug/Release build; the terminal DATA-04 sweep task carries the phase gate.

| Task ID | Plan | Wave | Requirement | Test Type | Automated Command | Status |
|---------|------|------|-------------|-----------|-------------------|--------|
| _(planner fills)_ | | | DATA-01..05 | hermetic + sweep | `BaseApi.Tests.exe --filter-not-trait Category=RealStack` / `scripts/phase-68-sweep.ps1` | pending |

---

## Sweep Runbook Notes (project memory)

- **Detached execution:** the ~2h sweep gets reaped if run as a tracked bg task — run via `Start-Process` + a separate short watcher loop; clear stale reports first.
- **Reseed-FIRST:** after a rebuild the new SourceHash has no processor row — graph-delete → seed → then start, or the first-scenario reset heal-wait traps.
- **Trust the harness exit code** over a possibly-stale `analyzer-reports/phase-68-summary.json`; TEST-01 can flake on cold ES.
- **Baseline oracle:** gate verdicts against the last-good all-PASS baseline commit, not a possibly-clobbered HEAD report.
