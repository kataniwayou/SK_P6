# Phase 79 — Deferred / Out-of-Scope Items

## Out-of-scope (pre-existing, NOT caused by this phase)

### Docker-less sandbox infra test failures (baseline)
- **Where:** `BaseApi.Tests.exe --filter-not-trait Category=RealStack` in this sandbox.
- **Observed (79-01):** total 853, failed 282, succeeded 571. All failures are broker/DB
  connection errors (`RabbitMQ.Client ... No such host is known`, Postgres `127.0.0.1:5433`
  refused, Redis) — the Docker-less-sandbox baseline documented across phases 68/72/75/78
  (~272 pre-existing). None are analyzer/keeper LOGIC facts.
- **Proof the 79-01 seam is clean:** `*ReinjectConsumerFacts` 8/8 GREEN (seam inert when
  `KEEPER_DEFEAT_REINJECT` unset), `*PassFailEngineFacts` 29/29 GREEN. Keeper + test project
  both build 0-warning / 0-error.
- **Disposition:** environmental, out of scope for Plan 79-01. The green hermetic-logic gate
  is proven by the targeted class runs above; the full-suite exit 0 is a Docker-up concern
  (Plan 79-05 live gate).
