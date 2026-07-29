# Phase 88 — Security Audit

**Phase:** 88 — pipeline-dashboard-maintenance-handoff-proof-fault-injection
**Audited:** 2026-07-29
**Register source:** `88-01-PLAN.md` .. `88-11-PLAN.md` `<threat_model>` blocks (authored at plan time)
**Threats:** 39 total — **39 CLOSED / 0 OPEN**
**ASVS level:** unset (project default)
**Verdict:** SECURED *(as of the 2026-07-29 amendment; the audit itself returned OPEN_THREATS — see the audit trail)*

Implementation files were read only. Nothing under `scripts/`, `docs/`, `k8s/`, `src/` or
`analyzer-reports/` was modified by this audit or by the amendment that closed T-88-14.

**Frontmatter for tooling:**

```yaml
status: secured
threats_found: 39
threats_closed: 39
threats_open: 0
```

---

## Method

`register_authored_at_plan_time` is true, so this audit verifies the declared mitigations exist in
the implemented code. It does not scan for new threats beyond the register.

Every SUMMARY in this phase self-reports `## Threat Flags: None`. **None of those self-reports was
accepted as evidence.** Each threat was verified against the implementation directly — grep for the
asserted literal, read of the guard code, inspection of the JSON artifact field, re-execution of the
gate where the gate was executable.

Three gates were re-executed independently during this audit:

| Gate | Command | Result |
|---|---|---|
| T-88-35 | `pwsh scripts/phase-87-dashboard-lint.ps1` | exit 0, 24 rules incl. C3 and C7, 31 panels |
| T-88-38 | plan 88-11's pre-flight one-liner | `checkpoint pre-flight ok` |
| T-88-34 | `git status --porcelain k8s/` | only the pre-existing untracked `22-otel-collector-servicemonitor.yaml` |

---

## Threat verification

### Closed (38)

| ID | Category | Component | Disp. | Evidence |
|---|---|---|---|---|
| T-88-01 | Tampering | workload/seam selection | mitigate | `scripts/lib/phase-88-cluster-ops.ps1:118` `$Phase88Tiers`, `:130` `$Phase88Seams`, `:139` `$Phase88SeamRemoveArg` are static tables; `Resolve-Phase88Tier:149-158` and `Resolve-Phase88Seam:164-177` return the **table's own string** by `StringComparison::Ordinal` or `$null`. `redis`/`prometheus`/mismatched pairs resolve `$null` → `FailureCode 61/62`, "no command issued" (`:379`, `:499`, `:560`). |
| T-88-02 | Tampering / DoS | replica restore | mitigate | `Invoke-TierScaleFault:385` reads `Get-LiveReplicas` before any mutation; refuses to scale if `< 1` (`:386-390`). Restore at `:433` and teardown at `phase-88-panel-discriminate.ps1:4694` both use `--replicas=$replicasBefore`. `ReplicasRestored` is an explicit re-read claim (`:448-456`). Literals `TierReplicas` and `--replicas=1`: **0 matches** across all 8 phase-88 files. `spec.replicas` present at `cluster-ops:239`. |
| T-88-03 | Tampering | left-armed seam | mitigate | `Clear-Phase88Seam:540-581` idempotent (static `'<NAME>-'` removal arg, safe on an unarmed seam). Called from the OUTER `finally` at `phase-88-panel-discriminate.ps1:4652-4687` (both seam slots + the Redis arm slot) and `phase-88-wave0-probe.ps1:1293-1309`. `Assert-StackRestored:705-712` re-reads live Deployment env and matches `'DEFEAT\|REINJECT_DELAY'`; `FailureCode 65` at `:748`, surfaced as `exit 65` at `discriminate:4647`. |
| T-88-04 | Info Disclosure | namespace + forward binding | mitigate | Single invocation site `Invoke-Phase88Ctl:201` — `& kubectl -n skp @Arguments`. Direct calls at `wave0-probe:1057,1074` and `discriminate:2673,4020,4037` all carry `-n skp`. Forwards bind `--address 127.0.0.1` (`wave0-probe:253`, `discriminate:611`). `0.0.0.0`: **0 matches**. *Deviation disclosed:* `kubectl config current-context` (`wave0-probe:460`, `discriminate:1209`) carries no `-n` — a local kubeconfig read touching no cluster resource. Not a disclosure path. |
| T-88-05 | DoS | shared forward PID file | mitigate | `.k8s-portforward-pids`: **0 matches** across all 8 phase-88 files. Each script owns one in-memory `$gfForwardPid` behind a recycled-PID guard (`discriminate:615-622`) and stops only a forward it started (`:4715`). |
| T-88-06 | Injection | `psql -c` | mitigate | All 5 `psql` sites (`wave0-probe:1057,1074`; `discriminate:2673,4020,4037`) are the identical static double-quoted literal `"SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"`. No `$` interpolation in any of them. |
| T-88-07 | Repudiation | abort-vs-verdict | mitigate | `Resolve-AnalyzerExitCode` dot-sourced from `scripts/lib/exit-code-resolution.ps1:28` at `discriminate:206`, `wave0-probe:166`, `rollup:86`; fail-closed `default → 1` at `:40`. Inline `switch ($verdict)`: **0 matches** in any phase-88 file. Distinct abort codes present: 15, 20, 50, 60, 61, 62, 63, 64, 65 (+66), legend at `discriminate:74-90`. *Inconsistency disclosed below — not a gap.* |
| T-88-08 | Tampering | playwright-skill dir | mitigate | Reader authored only at `scripts/phase-88-panel-read.js`; resolved to an ABSOLUTE path at `lib/phase-88-panel-read.ps1:197-204` and passed to `node <skillDir>/run.js <absPath>` at `:257`. `Resolve-PanelReaderSkillDir:104-115` only `Test-Path`/`Resolve-Path` — read-only. The reader's sole filesystem write is `fs.mkdirSync(screenshotDir)` (`phase-88-panel-read.js:439`), guarded absolute. |
| T-88-09 | Info Disclosure | artifact paths + runbook | mitigate | `SCREENSHOT_DIR` absolute enforced at `phase-88-panel-read.js:432-436` — a relative value writes **no** screenshot and emits a warning. All 141 `ScreenshotPaths` in `phase-88-BASE-01.json` are absolute under `analyzer-reports\phase-88-screenshots\`. No credential string in any `analyzer-reports/phase-88-*.json`. Runbook frames loopback access as a limitation and a blocking gap (`business-dashboard-runbook.md:28-34`), not an invitation. |
| T-88-10 | Tampering | container images | mitigate | `Assert-StackRestored:731-743` compares `Get-LiveImage` to caller-supplied `$ExpectedImages` read pre-scenario (`discriminate:1244,1251`); missing expectation is a **failed** claim (`:732-734`). `apply -k`: **0 matches** across all 8 phase-88 files. `ImagesUnchanged` true with empty `ImageMismatches` on every scenario artifact. |
| T-88-11 | Tampering | workflow lifecycle | mitigate | `Remove-ProbeArtifacts` (`wave0-probe:805-838`) issues `POST /api/v1/orchestration/stop` **before** `DELETE /api/v1/workflows/{id}`; called from the inner `finally:998` and again from the outer `finally:1312-1317`. Probe workflow carries a daily 04:00 cron (`:802`) so it cannot fire during the probe. `phase-88-panel-discriminate.ps1` deletes **no** workflow row and **no** Redis key — it only starts the pre-existing `v8-fanout-proof`. |
| T-88-12 | Spoofing/Repudiation | verdict channel + parsing | mitigate | `DiagnosticQueryValues` + `DiagnosticAgreesWithPanel` + `DiagnosticDisagreements` present on every scenario artifact; `phase-88-SCALE-01.json` records `DiagnosticAgreesWithPanel=False` with 1 disagreement **and** 1 `Findings` entry — disagreement recorded, not reconciled. Parsing: per-line sentinel extraction at `lib/phase-88-panel-read.ps1:275-281` (`^##PANEL-JSON##`), completeness check on `##PANEL-BATCH-END##` at `:283-323` with `Truncated`/`Unreadable` states. No whole-stdout `ConvertFrom-Json`. |
| T-88-13 | Tampering (V5) | parameter validation | mitigate | `-ScenarioId` guarded at `discriminate:485-489` → `exit 64`, before any cluster access; canonical key re-derived ordinally at `:496-498` so a case variant cannot mint a mismatched artifact name. `-Rungs` validated against static `$LadderAllRungDurations` (`:476-479`) at `:537-557` → `exit 64`. HTTP route lists are static literals inside `Start-Phase88HttpLoad` (`:842-847`). |
| T-88-15 | Tampering | uncommitted delay seam | mitigate | `KEEPER_REINJECT_DELAY_MS` appears in **0** `set env` lines. Its only 3 occurrences are prose refusals: `cluster-ops:98`, `rollup:184`, `zero03-classify:77`. It is absent from `$Phase88Seams`, so `Resolve-Phase88Seam` cannot return it. `Assert-StackRestored:709` still scans for `REINJECT_DELAY` residue. |
| T-88-16 | DoS | dependency outage | mitigate | `scale statefulset`: **0 matches**. `statefulset/` appears only as `exec` targets for redis/postgres reads. `DependencyOutageDriven=False` on `WEB-01`, `SCALE-01`, `ZERO-03`. |
| T-88-17 | Repudiation | unfalsifiable baseline | mitigate | `phase-88-BASE-01.json`: `BaselineBands[]` entries carry `Values` (10) and `States` (10), `SubWindowSeconds=60`, `SubWindowCount=10`, `BaselineWindowStart/End`, `ViewportWidth=1920`/`Height=1080`, `RateIntervalSeconds=240`. Discarded runs RECORDED: 3 `*-run1-discarded.json` artifacts are git-tracked, and the matrix carries 26 `discardedRuns` entries each naming the DEFECT (e.g. BASE-01 run 1's typed-parameter collision). |
| T-88-18 | Tampering | shallow serialisation | mitigate | `ConvertTo-Json -Depth 10` at `discriminate:1190,4625`, `wave0-probe:1279`, `rollup:559`. Post-write guard at `discriminate:4633-4637` is **structural**: `'(?m)(:\s*"System\.Object\[\]"\|^\s*"System\.Object\[\]"\s*,?\s*$)'` — matches only a JSON *value* position, not a substring, so diagnostic text mentioning the type does not false-positive. Failure → `exit 66`. |
| T-88-19 | DoS | HTTP load driver | mitigate | `Start-Phase88HttpLoad:832-901`: routes are static literals; concurrency 8 tight readers + 2 status drivers ≈ 10; every job bounded by `$DurationSeconds`. **No `/health/` route is driven** (`:827-828`; `/health/ready` appears only in the one-shot forward liveness probe at `:1224`). Jobs reaped in the outer `finally:4700-4711`. Load is read-only (`:840-842`). |
| T-88-20 | Repudiation | rollout ≠ fault | mitigate | `BaselineRecaptured`, `RolloutOldInstanceIds`, `RolloutNewInstanceIds`, `RolloutUtc`, `BandSource`, `RolloutNote` present on every scenario artifact. All five rollout-bearing scenarios verified non-empty and **disjoint**: SCALE-01 2/2, SCALE-02 3/3, SCALE-03 2/2, ZERO-02 2/2, ZERO-03 4/4; each `BaselineRecaptured=True` with `BandSource` naming the post-rollout window. WEB-01 (lever `http`) correctly carries empty pairs with `BaselineRecaptureNote` stating *"no rollout occurs… stated rather than omitted so that 'no rollout happened' and 'nobody checked' cannot look alike."* |
| T-88-21 | Repudiation | cross-talk control | mitigate | Startup self-validation at `discriminate:560-561` — *"A scenario with an EMPTY cross-talk list is a defect"* → `exit 64`. `SCALE-01.json` carries 4 `CrossTalkPanels` objects, each with `BandLow/BandHigh/BandSource/AfterValues/StayedPut/MaxExcursion` and per-series breakdown. Drift sets `StayedPut`/`CrossTalkHeld` false with `MaxExcursion` rather than reclassifying — `CrossTalkHeld` is a recorded boolean, not a gate. |
| T-88-22 | DoS | telemetry path | mitigate | `$Phase88Tiers` (`cluster-ops:118`) contains exactly `baseapi-service`, `keeper`, `orchestrator`, `processor-sample`. `otel-collector`/`prometheus`/`grafana` are **not expressible**: `Invoke-TierScaleFault:376-381` returns `FailureCode 62` with "no command issued" for any unresolved tier. |
| T-88-23 | Repudiation | NoData ≠ read failure | mitigate | Panel 9's `predictedDirection = @{ '9' = 'nodata' }` at `discriminate:356`. `Test-PanelMoved:613-624` scores `nodata` by counting the longest run of consecutive `'NoData'` entries in `AfterStates`, `MinConsecutive` default 2 — never against a silently shortened `AfterValues` array (`lib/phase-88-panel-read.ps1:593-596`). |
| T-88-24 | Repudiation | rung contamination | mitigate | `phase-88-LADDER-01.json` `Rungs[]` entries each carry `RungStartUtc`, `RungEndUtc`, their own `BaselineBand` + `BaselineValues`, `FaultStartUtc/EndUtc`, `AfterWindowStart/End`, `SubWindowSeconds`, `SubWindowCount`, and per-rung `ReplicasBefore/After/Restored`. `IndependenceNote` present at top level. |
| T-88-25 | Repudiation | theory fitted to data | mitigate | `Rungs[]` carry `DipFraction`, `DipFractionKind`, `DipFractionBasis`, `TheoreticalDipFraction`, `TheoreticalDipFractionKind`, `TheoreticalTroughFraction`, `PredictionDivergence`, `PredictionDivergenceComparable` — both KINDs stated side by side. Top level: `PredictionAgreesWithMeasurement=False`, `PredictedCounterSeconds=120` vs `MinDetectableCounterSeconds=240`, with `PredictionAgreementDetail` and `Findings`. The prediction was **falsified and left falsified**. |
| T-88-26 | Repudiation | roll-up re-scoring | mitigate | `kubectl`: **0 matches** in `scripts/phase-88-rollup.ps1`. Every matrix row carries `sourceArtifact`/`sourceArtifacts`; every `Proven` row carries `provenBy`; `AcceptedUnproven` with a blank reason appends a defect at `rollup:464-466` and `exit 1` at `:595`. |
| T-88-27 | Repudiation | missing proof as absence | mitigate | Two independent count guards — `rollup:212` (`$PanelFacts.Keys.Count -ne 14`) and `:550` (`@($rows).Count -ne 14`). `analyzer-reports/phase-88-discrimination.json` reads **exactly 14** rows. |
| T-88-28 | Repudiation | register/matrix drift | mitigate | Matrix `AcceptedUnproven` set = panels **{6, 7, 8, 13}**. `88-FINDINGS.md:30` states *"Panels AcceptedUnproven — 4 of 14 (ids 6, 7, 8, 13)"* and §1 holds exactly four records for those panels. No `Proven` panel appears in the register. `REQUIREMENTS.md:67` records the two as one set partitioned two ways. |
| T-88-29 | Repudiation | Complete on optimism | mitigate | `.planning/REQUIREMENTS.md:113` — **DISC-04 = `Partial`**, not Complete and not Pending; shortfall named at `:122` (panel 8 undrivable; SC5 met, SC4 not). DISC-02 = Complete with the computed 10 Proven / 4 AcceptedUnproven split matching `phase-88-discrimination.json`. The DISC-04 escalation text is generated by `rollup:179,184`, not asserted in prose. |
| T-88-30 | Tampering | requirement rewriting | mitigate | Inline italic dated markers present: `***Amended 2026-07-28 (Wave-0 probe PQ-03 and PQ-05)***` (DISC-02), `***Amended 2026-07-29 (measured in ZERO-01 and LADDER-01)***` (DISC-03), `***Amended 2026-07-29: HALF MET***` (DISC-04). `git diff 4b78869..HEAD -- .planning/REQUIREMENTS.md` shows the **sixteen Phase-87 rows** (DASH-01..04, RTD-01..03, BPD-01..03, VAR-01..04, VER-01..02) **untouched** — the only line referencing them is an added Phase-88 SC-mapping sentence. |
| T-88-31 | Repudiation | rubric inflation | mitigate | `88-FINDINGS.md:352-357` — six fixed questions (nameable/falsifiable/discriminating/actionable/non-misleading/reachable). `:344-345` states *"No weights and no aggregate score."* Rows `:371-384` = 14 × 6 = 84 cells, every one `yes`/`partial`/`no`. No score/total/weight column. |
| T-88-32 | Repudiation | unbacked runbook row | mitigate | `business-dashboard-runbook.md:126-136` — 10 driven rows each citing a scenario id matching `(BASE\|WEB\|SCALE\|ZERO\|LADDER)-\d\d` (SCALE-02, SCALE-01 ×3, SCALE-03, ZERO-02 ×2, WEB-01 ×4) with measured before/after values. `:146-149` — 4 accepted-unproven rows (panels 6, 7, 8, 13) each citing `88-FINDINGS.md` §1 and naming the log line to search instead. |
| T-88-33 | Repudiation | softened readiness | mitigate | `88-HANDOFF-VERDICT.md:54-62` — gap **B1** present and unsoftened: ClusterIP, no NodePort, no Ingress, loopback `port-forward` only, anonymous `Viewer`, `admin`/`admin` plain manifest env, no TLS, no SSO. `:253-265` — nine-criterion table with nine tokens: **7 PASS · 1 PARTIAL (criterion 1) · 1 FAIL (criterion 4)**, the FAIL stated as blocked. |
| T-88-34 | Tampering | out-of-scope fix | mitigate | `git status --porcelain k8s/` → only `?? k8s/22-otel-collector-servicemonitor.yaml`, which the register itself designates PRE-EXISTING and untracked, predating this phase. No dashboard JSON modified: `k8s/dashboards/` has zero entries in the porcelain output. |
| T-88-35 | Repudiation | silent re-classification | mitigate | `pwsh scripts/phase-87-dashboard-lint.ps1` re-executed → **exit 0**, `DASHBOARD LINT PASSED`, 2 files / 31 panels / 36 targets / 24 rules including **C3** and **C7**. C3/C7 partition the nine domain counters on the `or vector(0)` token (`phase-87-dashboard-lint.ps1:816-874`), so adding or removing a guard would flip a panel's class and fail the lint. |
| T-88-36 | Repudiation | unattributed sign-off | mitigate | `88-HANDOFF-VERDICT.md:312-380` — **Date** 2026-07-29; **Signed by** *"the GSD continuous-execution chain — an automated agent, not a person"*; **Class** *"machine-approved / unattended."* The by-instruction nature is stated explicitly: *"No human read [either deliverable] before this section was written… A standing instruction to keep running is not a reading of what was produced."* Seven-check table records **3 of 7 performed, all mechanical**; checks 3, 4, 6, 7 marked `NO — NOT PERFORMED` by name. Reservations section: *"None were raised, because no reviewer was present to raise any. That is not 'no reservations' — it is an absence of review."* |
| T-88-37 | Repudiation | finding edited away | mitigate | Commit `dab0782` (`docs(88-11): record unattended machine sign-off`) — **1 file changed, 72 insertions(+), 0 deletions(-)**. Append-only, single file, no document under review altered. Corroborated by the sign-off's own claim at `:371`. No rejection occurred, so the no-sign-off-on-rejection branch is untriggered rather than unimplemented. |
| T-88-38 | Repudiation | incomplete record | mitigate | Automated pre-flight at `88-11-PLAN.md:127` asserts all four reviewer inputs exist, the matrix is exactly 14 rows, and no non-`Proven` row has a blank reason. **Independently re-executed during this audit → `checkpoint pre-flight ok`.** |
| T-88-SC | Tampering | supply chain | **accept** | Accept condition **HOLDS**. Zero package-manager install tasks across all 11 PLAN files (`npm install`/`npm ci`/`yarn add`/`pnpm add`/`Install-Module`/`pip install` → 0 matches). Zero in the 8 implementation files (`npm`/`npx`/`yarn`/`pnpm`/`package.json` → 0 matches). No `package.json` added. The reader is executed by the pre-installed skill's own `run.js` (`lib/phase-88-panel-read.ps1:257`), consuming only that skill's existing `node_modules`. |

### Open (0) — resolved 2026-07-29

| ID | Category | Component | Disp. | Status | Detail |
|---|---|---|---|---|---|
| T-88-14 | Spoofing | Grafana credentials | **accept** | **CLOSED** | Accept condition **amended** to match the implemented code (option 1 below), on the user's decision of 2026-07-29. The register no longer asserts a property the checked-in files lack. Source register amended in place at `88-01-PLAN.md:373` with a dated inline italic marker, per this phase's own T-88-30 convention. No implementation file was changed. |

---

## RESOLVED — T-88-14: accept condition amended, not the code

**Resolution taken:** option 1 (amend the register). Decided by the user on 2026-07-29 after the
exposure analysis below was presented. Option 2 (amend the code) was considered and declined —
because any fallback literal still bakes in a credential, making the original wording true would
have required the drivers to *fail* without `GRAFANA_BASIC_AUTH` set, which is a behavioural
regression against a credential already published in a tracked manifest.

**What changed:** the accept condition in `88-01-PLAN.md:373` now states the true grounds for the
acceptance — no credential reaches any artifact, access is loopback-only, and the same credential is
already plaintext in `k8s/23-grafana.yaml` on an established Phase-87 precedent — instead of the
falsified claim that no credential is baked into a checked-in file. Nothing under `scripts/`,
`docs/`, `k8s/`, `src/` or `analyzer-reports/` was modified.

**The three hardcoding sites remain, by decision, and are now documented rather than denied.**

The original finding is preserved verbatim below so the amendment is auditable rather than a
silent rewrite.

---

## Original finding — T-88-14: the accept condition did not hold in the implemented code

**Declared accept condition** (verbatim, `<threat_model>` of the phase plans):

> Grafana runs `admin`/`admin` over loopback as the deliberate v13.0.0 dev posture, reachable only
> via a `--address 127.0.0.1` forward. `GRAFANA_BASIC_AUTH` stays optional and is never defaulted to
> a literal, **so no credential is baked into a checked-in file.**

**What the code does.** The first clause holds. `lib/phase-88-panel-read.ps1:178` defaults
`$BasicAuthBase64 = ''` and passes it through to `$env:GRAFANA_BASIC_AUTH` at `:253`; the reader
applies an `Authorization` header only when non-empty (`phase-88-panel-read.js:463-464`). The
library bakes in nothing.

The second clause does not hold. Three **git-tracked** phase-88 scripts construct the credential from
a hardcoded literal and pass it unconditionally, with no environment override at the call site:

| File | Line | Code |
|---|---|---|
| `scripts/phase-88-panel-discriminate.ps1` | 212 | `$adminB64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('admin:admin'))` |
| `scripts/phase-88-wave0-probe.ps1` | 172 | *(identical)* |
| `scripts/phase-88-relegend.ps1` | 63 | *(identical)* |

`$adminB64` is then passed at `discriminate:966,983,3084,4176,4321,4379` and `wave0-probe:658,677,1131`.
All three files are confirmed tracked (`git ls-files --error-unmatch`). A credential **is** baked into
a checked-in file.

**Honest assessment of exposure.** The practical delta is close to zero, and this is stated so the
finding is not read as larger than it is:

- The same `admin`/`admin` is already plain text in `k8s/23-grafana.yaml:102-105`
  (`GF_SECURITY_ADMIN_USER` / `GF_SECURITY_ADMIN_PASSWORD`) — pre-existing, from Phase 87.
- `scripts/phase-87-dashboards-verify.ps1` already established the identical pattern before Phase 88.
- Reachable only over a loopback `--address 127.0.0.1` forward (T-88-04 verified).
- No credential reaches any artifact (T-88-09 verified — zero matches across `analyzer-reports/phase-88-*.json`).
- The posture is disclosed, not hidden: `88-HANDOFF-VERDICT.md:54-62` names it in blocking gap B1 and
  `business-dashboard-runbook.md:30` repeats it.

**Why it is still OPEN.** An accepted risk is only as good as its stated premise. This register entry
asserts a property of the checked-in files that the checked-in files do not have, so anyone reading
the register to decide whether a secret-scan finding here is expected would be misinformed. The gap is
between the register and the code, not between the code and safety.

**Resolution options** (implementation is out of scope for this audit — none of these was applied):

1. **Amend the register** — restate T-88-14's accept condition to match reality: *"the shared reader
   library never defaults the credential; the phase-88 drivers hardcode the same dev `admin:admin`
   already present in `k8s/23-grafana.yaml`, on the `phase-87-dashboards-verify.ps1` precedent."*
   Cheapest, and arguably the correct fix given the exposure analysis above.
2. **Amend the code** — read `$env:GRAFANA_BASIC_AUTH` at the three call sites and fall back to the
   literal only when unset. Requires a live re-run to confirm the readers still authenticate.

Option 1 is a documentation change and closes the discrepancy without touching proven code.

---

## Accepted risks log

| ID | Risk | Accept condition | Condition verified? |
|---|---|---|---|
| T-88-SC | Supply chain — no new dependency introduced | zero package-manager installs across all 11 plans and the implementation; reader uses only the pre-installed skill's `node_modules` | **YES** — 0 matches for every install form in both plans and code; no `package.json` added |
| T-88-14 | Grafana `admin`/`admin` over loopback as the deliberate v13.0.0 dev posture | *(amended 2026-07-29)* the shared reader library never defaults the credential; the three phase-88 drivers hardcode the same dev `admin:admin` already plaintext in `k8s/23-grafana.yaml:103-105`, on the `phase-87-dashboards-verify.ps1` precedent; access is loopback-only and **no credential reaches any artifact** | **YES** — every clause of the amended condition verified: library default `''` at `panel-read.ps1:178`; header sent only when non-empty at `panel-read.js:463`; 3 driver sites enumerated at `discriminate:212`, `wave0-probe:172`, `relegend:63`; loopback-only confirmed under T-88-04; zero credential matches across `analyzer-reports/phase-88-*.json` under T-88-09 |

---

## Unregistered flags

**None.**

All 11 SUMMARY files report `## Threat Flags: None`. Per the audit constraint these self-reports were
**not** accepted as evidence; each was checked against the code instead. No new attack surface was
found that lacks a register mapping:

- No product `src/` change (the phase's locked constraint; corroborated — the only `src/` reference in
  phase-88 scripts is read-only citation of `ProcessorPipeline.cs` / `ReinjectConsumer.cs` line numbers).
- No new network endpoint, listener or auth path.
- No schema change.
- No dependency added (T-88-SC).
- The one new credential-handling site is already registered as T-88-14 and is reported OPEN above,
  not as an unregistered flag.

---

## Observations (not threat gaps — no action required)

Disclosed for completeness. Neither weakens a declared mitigation.

1. **T-88-07 — exit-code legend vs pre-flight path.** `discriminate:78-90` assigns code **62** to
   *"could not read live replicas or images"*, but the driver's own pre-flight folds that condition
   into `exit 64` at `:1245-1278` ("stack not at rest"). Code 62 is emitted by `wave0-probe:1101` and
   returned as a `FailureCode` from `cluster-ops:378,387,395`. The threat's actual property is intact:
   every abort code is distinct from the verdict codes 0/1/2, `Resolve-SweepClass` maps 64 → `BAD_ARG`
   and everything else → `INFRA_ABORT`, so no abort can be read as a verdict. This is an internal
   legend/code inconsistency, not a mitigation gap.

2. **T-88-04 — `kubectl config current-context`.** Two call sites (`wave0-probe:460`,
   `discriminate:1209`) carry no `-n skp`. They read the local kubeconfig and touch no cluster
   resource, so no cross-namespace disclosure is reachable through them. Recorded because the threat
   text says "every kubectl invocation".

3. **Phase context.** This phase deliberately produced a qualified-negative handoff verdict and left
   DISC-04 blocked. The audit treated those as the honest findings they are. The sign-off's disclosure
   that no human read the deliverables (T-88-36) is the *correct* behaviour under the threat model, not
   a defect — the threat was that an unattended approval would be presented as a human review, and the
   record states plainly that it was not. The outstanding human review remains genuinely outstanding
   and is tracked in `88-VERIFICATION.md`'s `human_verification` frontmatter, outside this audit's scope.

---

## Result

**SECURED — 39 CLOSED / 0 OPEN.**

No `mitigate` disposition is unimplemented: all 37 mitigate threats verified present in the code,
artifacts or re-executed gates, and both `accept` dispositions now have verified accept conditions.

## Security Audit 2026-07-29

| Metric | Count |
|--------|-------|
| Threats found | 39 |
| Closed | 39 |
| Open | 0 |

**Audit trail**

| # | Event | Outcome |
|---|---|---|
| 1 | `gsd-security-auditor` verified 39 plan-time threats against the implementation. All 11 SUMMARY `## Threat Flags: None` self-reports were **refused as evidence** — each mitigation checked against code, artifact fields, or a re-executed gate. Three gates re-run live: dashboard lint (exit 0, C3+C7 present), 88-11 pre-flight (`checkpoint pre-flight ok`), `git status --porcelain k8s/`. | **OPEN_THREATS** — 38 CLOSED / 1 OPEN (T-88-14) |
| 2 | Auditor output renamed `SECURITY.md` → `88-SECURITY.md` so it matches the `*-SECURITY.md` glob the phase gate globs for. Without this, a later `/gsd:secure-phase 88` would re-detect State B and re-audit from scratch. | filename corrected |
| 3 | T-88-14 resolved by **option 1 (amend the register)** on the user's decision. Source register amended in place at `88-01-PLAN.md:373` with a dated inline italic marker per this phase's own T-88-30 convention; the original finding preserved verbatim in this file. Option 2 (amend the code) declined — any fallback literal still bakes in a credential, so satisfying the original wording would have required the drivers to fail without `GRAFANA_BASIC_AUTH`, a behavioural regression against a credential already published in a tracked manifest. | **SECURED** — 39 CLOSED / 0 OPEN |

**Non-blocking observations recorded by the auditor** (no action required, carried for a future reader):

1. `discriminate:78-90` assigns exit **62** to "could not read live replicas or images", but the
   pre-flight folds that into `exit 64` at `:1245-1278`. Legend/code inconsistency only — T-88-07's
   real property is intact, every abort code stays distinct from verdicts 0/1/2.
2. `kubectl config current-context` (`wave0-probe:460`, `discriminate:1209`) carries no `-n skp`.
   This is a local kubeconfig read touching no cluster resource, so T-88-04 is not violated.
3. The qualified-negative handoff verdict and the blocked DISC-04 are the honest findings this phase
   was designed to produce, not security defects.
