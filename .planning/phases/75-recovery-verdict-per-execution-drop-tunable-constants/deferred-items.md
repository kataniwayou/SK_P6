# Phase 75 — Deferred Items

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
