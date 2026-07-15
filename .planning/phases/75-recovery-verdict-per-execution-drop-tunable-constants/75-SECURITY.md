---
phase: 75
slug: recovery-verdict-per-execution-drop-tunable-constants
status: verified
threats_open: 0
asvs_level: 1
created: 2026-07-15
---

# Phase 75 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

Phase 75 reworks an internal test-harness verdict (`PassFailEngine`), widens keeper observability log fields, raises test-env L2 TTLs, and adds an optional harness env seam. It introduces **no new external attack surface** — no network endpoint, no untrusted input, no new persistence. All logged/emitted data is correlation/execution/entry/message GUIDs plus static outcome labels; no payloads, secrets, or PII cross any boundary.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| keeper → Elasticsearch (telemetry) | `ReinjectConsumer` structured log fields cross into the ES log store via the MEL→OTLP bridge (fields widened, not a new path) | correlation/execution/entry/message GUIDs + `"drop"`/`"reinject"` label — no payload/secret/PII |
| Elasticsearch → analyzer | `AnalyzerE2ETests` reads keeper log docs from ES (internal test-stack log store) via one added `_search` + a defensive JSON parse | GUID keys + outcome label; read-only, no write, no Redis |
| config → live service (test-env) | L2 TTL floor values read from Options/appsettings into live services (magnitudes raised only) | integer TTL literals — no key shape change, no external input |
| K_EXECUTIONS env seam | optional harness env var bounding an observe loop | single integer count; cleared symmetrically in `finally` |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-75-01 | Information Disclosure | `AnalyzerReport.UnrecoverableLossDetail` detail strings | accept | Detail line is `[corr|exec] keeper clean-absent DROP (…)` — two GUIDs + static label; `RunTrace` model carries no payload (`PassFailEngine.cs:175,196`; grep of `Observability/Analysis` for payload/secret/token → none) | closed |
| T-75-02 | Tampering | verdict logic | accept | `Analyze` (`PassFailEngine.cs:111-333`) is a pure function over read-only collections returning a fresh `AnalyzerReport`; no static/instance mutation, no external write surface; D75-2 absolute bound removed | closed |
| T-75-03 | Information Disclosure | keeper REINJECT drop/success logs → ES | mitigate | Both templates (`ReinjectConsumer.cs:49-51`, `:78-80`) log ONLY `{CorrelationId} {ExecutionId} {EntryId} {MessageId} {ReinjectOutcome}` as placeholder args with a static outcome literal; no `$"…"` interpolation, no `m.Payload` (grep-confirmed) | closed |
| T-75-04 | Tampering | log field names | accept | Templates are compile-time string literals (`ReinjectConsumer.cs:50,79`); no external input controls them | closed |
| T-75-05 | Denial of Service | L2 (Redis) memory footprint under longer TTLs | accept | TTL 300→900 in **test-env config only**; the sweep fixture actively DELETEs `skp:data:*`/`skp:msg:*` rather than waiting for TTL expiry — no residual-key accumulation; no production impact | closed |
| T-75-06 | Tampering | TTL config values | accept | Options defaults + appsettings literals (`= 900`); no external input controls them | closed |
| T-75-07 | Tampering / malformed input | `BuildKeeperOutcomeMap` JSON parse | mitigate | Every attribute read guarded (`AnalyzerE2ETests.cs:375-383` — `TryGetProperty` + `ValueKind != String → continue`, never throws); ES query body (`:329-343`) is a static raw-string over DIRECT-path field consts with only validated window timestamps interpolated; ES-read-only, no injection surface | closed |
| T-75-08 | Information Disclosure | keeper-outcome map | accept | Map keyed `corr|exec` GUIDs → `"drop"`/`"reinject"` label only (`AnalyzerE2ETests.cs:385-392`); no secrets/PII | closed |
| T-75-09 | Denial of Service | observe loop termination | mitigate | Loop bounded UNCONDITIONALLY by the 300s `$windowSeconds`/`$windowDeadline` wall-clock cap (`phase-67-harness.ps1:363`); the `K_EXECUTIONS` early-break (`:364-370`) fires only when `> 0` and can only break sooner — never extends the loop | closed |
| T-75-10 | Information Disclosure | `K_EXECUTIONS` env seam | accept | Single integer count (`phase-67-harness.ps1:433`); cleared symmetrically in `finally` (`:449`) alongside existing seams | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-75-01 | T-75-01 | Report detail strings carry only correlation/execution GUIDs + a static English label; the analyzer model never holds payloads | User | 2026-07-15 |
| AR-75-02 | T-75-02 | Pure in-process verdict function; no external mutation/attack surface | User | 2026-07-15 |
| AR-75-03 | T-75-04 | Log field names are compile-time literals, not attacker-controlled | User | 2026-07-15 |
| AR-75-04 | T-75-05 | TTL raise is **test-env only**; sweep actively deletes keys, so longer TTLs cannot accumulate residual state; no production reachability | User | 2026-07-15 |
| AR-75-05 | T-75-06 | TTL values are compile-time/appsettings literals, not attacker-controlled | User | 2026-07-15 |
| AR-75-06 | T-75-08 | Outcome map holds only GUID keys + a `"drop"`/`"reinject"` label | User | 2026-07-15 |
| AR-75-07 | T-75-10 | Env seam carries a single integer, cleared symmetrically in `finally` | User | 2026-07-15 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-07-15 | 10 | 10 | 0 | gsd-security-auditor |

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-07-15
