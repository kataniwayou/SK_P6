# Phase 82 — Deferred / Out-of-Scope Items

## OUT-OF-SCOPE: full-suite compose-backed integration failures (Plan 82-04)

**Observed during:** the Plan 82-04 full hermetic run
(`BaseApi.Tests.exe --filter-not-trait Category=RealStack`).

**Symptom:** 283 / 866 tests fail — all in the `Composition`, `Controllers`, and
`Features.Orchestration` namespaces (e.g. `RedisFixtureFacts.InitializeAsync_Connects_Multiplexer`,
`ComposeYamlFacts`, `StartOrchestrationFacts`, MassTransit `ReceiveTransport` errors).

**Root cause (environmental, pre-existing, NOT introduced by Plan 82-04):** these tests use
`RedisFixture` / `PostgresFixture`, which connect to the host-side compose stack at hardcoded
`localhost:6380` (Redis) / `localhost:5433` (Postgres) plus RabbitMQ. The fixture is designed to
fail-loud when compose is down ("if compose isn't running, the first PING fails-loud and tests
stop (correct behavior)" — `RedisFixture.cs`). Compose is not running in this session, so every
compose-backed test fails. This is the same Docker-less-sandbox infra baseline noted across prior
phases (68/72/73/74/75/79) in their own `deferred-items.md`.

**Why out of scope for 82-04:** Plan 82-04 adds three PURE hermetic unit-test files and modifies
NO SUT code, so it cannot affect Redis/compose/controller tests. The three new Phase-82 classes
(`LeaderStateTransitionTests`, `OrchestratorRoleEnricherTests`, `WorkflowFireJobGateTests` — 10
tests) are 100% green, and the test assembly builds 0-warning in Debug AND Release.

**Resolution:** No action taken (SCOPE BOUNDARY — pre-existing, infra-dependent, unrelated).
Bringing compose up (`scripts/phase-65-up.ps1` or `docker compose up`) makes these tests
runnable; that is a live-stack concern, not a Phase-82 hermetic-test concern.
