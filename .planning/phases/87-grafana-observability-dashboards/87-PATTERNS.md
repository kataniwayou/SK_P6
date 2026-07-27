# Phase 87: Grafana Observability Dashboards - Pattern Map

**Mapped:** 2026-07-27
**Files analyzed:** 7 (5 new authored, 1 modified, 1 generated artifact)
**Analogs found:** 5 / 7 (2 dashboard JSONs have NO analog — see § No Analog Found)

**Project instructions:** no `./CLAUDE.md`, no `.claude/skills/`, no `.agents/skills/` (verified this session — matches `87-RESEARCH.md:35`). All conventions below are derived from the repo itself.

---

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `k8s/23-grafana.yaml` (NEW) | config / k8s manifest | request-response (HTTP service) | `k8s/21-prometheus.yaml` | **exact** (same observability tier, ClusterIP + Deployment + ConfigMap-mounted config, no PVC) |
| `k8s/kustomization.yaml` (MODIFY) | config / aggregator | batch (declarative apply) | **itself** — `k8s/kustomization.yaml:5-10,44-60` | **exact** (the file pre-authorizes the exact change being made) |
| `k8s/dashboards/runtime.json` (NEW) | data artifact / dashboard definition | transform (query→render) | *(none)* | **no analog** → use `87-RESEARCH.md:555-613` skeleton |
| `k8s/dashboards/business.json` (NEW) | data artifact / dashboard definition | transform (query→render) | *(none)* | **no analog** → use `87-RESEARCH.md:555-613` skeleton |
| `scripts/phase-87-dashboard-lint.ps1` (NEW) | test / hermetic gate | batch (parse + assert + exit) | `scripts/verify-sourcehash-reproducible.ps1` | **role-match** (the repo's only standalone hermetic PowerShell gate that touches no cluster) |
| `scripts/phase-87-dashboards-verify.ps1` (NEW) | test / live harness | request-response + orchestration | `scripts/phase-83-ha-failover.ps1` | **exact** for skeleton; **see the report-writing correction below** |
| `analyzer-reports/phase-87-dashboards.json` (GENERATED) | data artifact / verdict | — | `analyzer-reports/phase-83-ha.json` | **exact** schema |

---

## Pattern Assignments

### `k8s/23-grafana.yaml` (config, request-response)

**Analog:** `k8s/21-prometheus.yaml` (85 lines — read in full)

**House header-comment convention** (`k8s/21-prometheus.yaml:1-19`) — a boxed banner naming the workload and its shape, then a prose block explaining *every* deviation and *why*, with `T-8x-nn` threat ids and `D-nn` decision ids inline:

```yaml
# ============================================================================
# prometheus — metrics scrape (Deployment + ClusterIP Service + ConfigMap subPath mount).
# ============================================================================
# Port of the compose `prometheus` block (compose.yaml L86-114). Prometheus scrapes
# `otel-collector:8889` in-cluster (target configured in prometheus.yml, carried
# ...
# The compose wget /-/healthy healthcheck (10s/5s/retries 3/start 10s) becomes a
# kubelet-side `httpGet: {path: /-/healthy, port: 9090}` readiness probe (200 == process
# up + config loaded).
```
Phase 87 has no compose ancestor, so the header should instead record: the Grafana version pin rationale, the `emptyDir` = DASH-03 statement, the leaf-directory mount rule (`87-RESEARCH.md:305`), and the 1 MiB ConfigMap ceiling note (`87-RESEARCH.md:351`).

**Service-first ordering + label pair** (`k8s/21-prometheus.yaml:20-34`) — Service is declared BEFORE the Deployment in the same file, separated by `---`:

```yaml
apiVersion: v1
kind: Service
metadata:
  name: prometheus
  namespace: skp
  labels:
    app: prometheus
    app.kubernetes.io/part-of: skp
spec:
  # ClusterIP (default) — in-cluster only; host reach is 127.0.0.1 port-forward (T-80-08, accept).
  selector:
    app: prometheus
  ports:
    - port: 9090
      targetPort: 9090
```
Note: **no `type:` key** — ClusterIP is left implicit and the comment states the intent. Copy that exactly for `grafana` / port `3000`.

**Deployment shape** (`k8s/21-prometheus.yaml:36-62`):

```yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: prometheus
  namespace: skp
  labels:
    app: prometheus
    app.kubernetes.io/part-of: skp
spec:
  replicas: 1
  selector:
    matchLabels:
      app: prometheus
  template:
    metadata:
      labels:
        app: prometheus          # <- pod template carries ONLY `app:`, not the part-of label
    spec:
      containers:
        - name: prometheus
          image: prom/prometheus:v3.11.3        # verbatim (public image)
```
`image:` carries a **pinned patch version + an inline justification comment** — never `:latest`. This is the precedent for `grafana/grafana:12.3.9`. Public images carry **no** `imagePullPolicy` (contrast `k8s/32-keeper.yaml:47-48`, where local `:local` images pin `IfNotPresent`).

**Volume mounting** (`k8s/21-prometheus.yaml:63-67, 82-85`) — the prometheus analog uses a **single-file `subPath`** mount:

```yaml
          volumeMounts:
            # single-file subPath (not a whole-dir mount) → image console libs preserved (T-80-07)
            - name: prom-config
              mountPath: /etc/prometheus/prometheus.yml
              subPath: prometheus.yml
      volumes:
        - name: prom-config
          configMap:
            name: prometheus-config    # ConfigMap authored in plan 80-01
```
**DIVERGENCE the planner must handle:** Grafana needs three *whole-directory* mounts, not `subPath`. The `T-80-07` rationale (don't shadow sibling files in the image dir) still applies and is exactly why `87-RESEARCH.md:305,809` mandates mounting at the **leaf** dirs (`/etc/grafana/provisioning/datasources`, `.../dashboards`) and never at `/etc/grafana/provisioning`. Carry the T-80-07 comment style, invert the mechanism, and say why. The verified volume block is `87-RESEARCH.md:906-917`.

**Probe convention.** `21-prometheus.yaml:68-76` has readiness only (pre-Phase-86). The **current** house shape is the Phase-86 startup/readiness/liveness trio at `k8s/32-keeper.yaml:74-98`:

```yaml
          startupProbe:
            httpGet:
              path: /health/startup
              port: 8083
            initialDelaySeconds: 5
            periodSeconds: 5
            timeoutSeconds: 3
            failureThreshold: 30
          readinessProbe:
            httpGet:
              path: /health/ready
              port: 8083
            initialDelaySeconds: 30
            periodSeconds: 10
            timeoutSeconds: 3
            failureThreshold: 5
          livenessProbe:
            httpGet:
              path: /health/live
              port: 8083
            initialDelaySeconds: 45
            periodSeconds: 15
            failureThreshold: 6
```
Prefer this trio (all three pointing at Grafana's single `/api/health`, per `87-RESEARCH.md:338-342`) over the prometheus readiness-only shape — 32-keeper is the more recent convention.

**Resources** (`k8s/21-prometheus.yaml:77-81`, identical shape at `k8s/32-keeper.yaml:99-103`):

```yaml
          resources:
            requests:
              memory: "128Mi"
            limits:
              memory: "256Mi"
```
**House style is memory-only — no `cpu` key anywhere in `k8s/`.** `87-RESEARCH.md:344` proposes `requests: {memory: "128Mi", cpu: "50m"}`. Adding `cpu` would be the first CPU request in the repo; the planner should either drop it (match the house shape) or comment why it diverges.

**ConfigMap metadata shape for the two provisioning ConfigMaps** — copy `k8s/02-configmaps.yaml:11-17`:

```yaml
kind: ConfigMap
metadata:
  name: otel-collector-config
  namespace: skp
  labels:
    app.kubernetes.io/part-of: skp     # <- ConfigMaps carry ONLY part-of, no `app:` label
data:
  # Mounted (subPath) at /etc/otel-collector-config.yaml — compose.yaml L63.
  otel-collector-config.yaml: |
```
The `data:` key carries an inline comment naming its in-container mount path. Do the same for `prometheus.yaml` (datasource) and `skp.yaml` (dashboard provider). Bodies are at `87-RESEARCH.md:851-881`.

**NO analog in `k8s/` for:** `securityContext` (grep: zero occurrences across all 15 manifests) and `emptyDir` (zero occurrences). Both are new to this repo. Author them with an explanatory comment — `emptyDir` especially, since its absence-of-persistence *is* the DASH-03 requirement.

---

### `k8s/kustomization.yaml` (config, batch)

**Analog:** itself. The file already documents the exact change Phase 87 makes.

**The pre-authorization** (`k8s/kustomization.yaml:5-10`) — this comment is load-bearing and **must be updated**, not left stale:

```yaml
# It ONLY (a) lists every manifest as a resource so `kubectl apply -k k8s/` is a
# single command, and (b) stamps the `skp` namespace + two common labels. No
# templating, no patches, no configMapGenerator (the ConfigMaps are the
# hand-authored 02-configmaps.yaml — swapping to a generator would rename them
# with a content-hash suffix and break the Deployment volume `configMap.name`
# references, so it is deliberately NOT used here — RESEARCH Open-Q4 default).
```
Phase 87 introduces the first `configMapGenerator`. `k8s/02-configmaps.yaml:5-9` states the condition under which that is safe:

```yaml
# NOTE for kustomization plan (80-07): these hand-authored ConfigMaps are the
# canonical drop-in. If 80-07 uses a kustomize configMapGenerator it MUST set
# disableNameSuffixHash: true AND reuse these SAME names (otel-collector-config,
# prometheus-config) so the Deployment volume refs (80-04) do not break
```
→ `disableNameSuffixHash: true` is **mandatory** (also `87-RESEARCH.md:888`). The plan must amend the `:5-10` comment from "No … configMapGenerator" to "one configMapGenerator, for the dashboard JSONs only, with `disableNameSuffixHash: true`".

**Resources list — tier order, numeric prefix** (`k8s/kustomization.yaml:44-60`):

```yaml
# All 13 manifests, in tier order (namespace → secret/config → stateful backing
# services → observability → app tiers). Kind-sort at apply time still applies.
resources:
  - 00-namespace.yaml
  ...
  - 20-otel-collector.yaml
  - 21-prometheus.yaml
  - 30-baseapi-service.yaml
```
Insert `- 23-grafana.yaml` between `21-prometheus.yaml` and `30-baseapi-service.yaml`, and update the count in the "All 13 manifests" comment (→ 14). Note `22-otel-collector-servicemonitor.yaml` is **deliberately absent** (OCP-only CRD, `87-RESEARCH.md:42`) — do not "fix" that while editing this list.

**Existing directives to leave untouched** (`k8s/kustomization.yaml:28,38-42`):

```yaml
namespace: skp

labels:
  - pairs:
      app.kubernetes.io/part-of: skp
      app.kubernetes.io/managed-by: kustomize
    includeSelectors: false
```
New `generatorOptions:` must not disturb this. Note `generatorOptions.labels` (proposed at `87-RESEARCH.md:889-890`) is *redundant* with the top-level `labels:` block — the planner should verify with `kubectl kustomize k8s/` before adding it.

**The lint gate this file already declares** (`k8s/kustomization.yaml:18-20`) is the per-wave sampling command in `87-VALIDATION.md:34`:

```yaml
# WHOLE-STACK LINT GATE: `kubectl apply -k k8s/ --dry-run=server` validates the
# entire aggregated stack against the live API server (schema + admission) before
# anything is created — the T-80-14 malformed-manifest guard.
```

---

### `scripts/phase-87-dashboards-verify.ps1` (test, live request-response)

**Analog:** `scripts/phase-83-ha-failover.ps1` (378 lines — read in full)

**Comment-based help block** (`scripts/phase-83-ha-failover.ps1:1-68`) — `.SYNOPSIS` / `.DESCRIPTION` with a STEP-flow table / an **EXIT-CODE TABLE** / `.PARAMETER` per switch / `.NOTES`:

```powershell
<#
.SYNOPSIS
    Phase 83 HA-failover proof harness (HA-07 — the leader-kill sequencer + verdict driver).

.DESCRIPTION
    ...
    Flow:

        STEP A0  phase-80-build.ps1                  build the 4 app images :local (SourceHash currency)
        STEP A   phase-80-up.ps1                     kubectl apply -k + rollout + port-forwards + readiness
        ...
        STEP Z   stop port-forwards                   teardown in the outer finally (keep the stack + PVCs)

    EXIT-CODE TABLE (mirrors phase-80-harness.ps1; the 0/1/2 classes are owned by exit-code-resolution.ps1):
        0   HA verdict PASS         (Resolve-AnalyzerExitCode Pass)
        1   HA verdict FAIL         (Resolve-AnalyzerExitCode Fail / unknown fail-closed)
        2   HA verdict INCONCLUSIVE (Resolve-AnalyzerExitCode Inconclusive - e.g. gap tick never landed)
        10  bring-up failed
        ...
.NOTES
    Dev/ops-only tooling. No product source touched. Fully automated — no interactive prompt anywhere.
#>
```
Every infra step gets its own **distinct** non-zero code so an infra abort is never mistaken for a verdict (`:35`). Phase 87 needs the same discipline, e.g. `0/1/2` verdict, `10` bring-up/rollout, `15` port-forward never carried traffic, `20` Grafana `/api/health` never green, `30` datasource health non-OK, `40` dashboards not provisioned, `50` traffic drive failed.

**Preamble** (`scripts/phase-83-ha-failover.ps1:70-79`):

```powershell
param(
    [switch]$SkipBringUp,
    [switch]$TearDownCluster
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repoRoot
try {
```
`Push-Location $repoRoot` + a whole-body `try { … } finally { … Pop-Location }` is the frame. Copy verbatim, including the `-SkipBringUp` switch (Phase 87 needs it — the stack is usually already up).

**Prefixed trace helper** (`scripts/phase-83-ha-failover.ps1:85-87`) — every script defines its own, prefix = script name:

```powershell
    function Write-Phase([string]$msg, [string]$color = 'Cyan') {
        Write-Host "[phase-83-ha-failover] $msg" -ForegroundColor $color
    }
```
Colour convention observed across the repo: `Cyan` = step banner, `Gray` = sub-detail/skip, `Yellow` = warning/non-fatal, `Red` = fatal-before-exit, `Green` = success.

**Shared exit-code lib, dot-sourced** (`scripts/phase-83-ha-failover.ps1:94`):

```powershell
    . (Join-Path $PSScriptRoot 'lib/exit-code-resolution.ps1')
```
`scripts/lib/exit-code-resolution.ps1:28-42` — reuse this rather than an inline switch:

```powershell
function Resolve-AnalyzerExitCode {
    param([Parameter(Mandatory)] $Report)
    $verdict = "$($Report.Verdict)"
    switch ($verdict) {
        'Inconclusive' { return 2 }
        'Fail'         { return 1 }
        'Pass'         { return 0 }
        default        { return 1 }   # unknown/absent verdict is fail-closed (never a false green)
    }
}
```
→ Phase 87's report **must** use the field name `Verdict` with values exactly `Pass` / `Fail` / `Inconclusive` so this lib maps it. `Resolve-SweepClass` (`:44-58`) gives the human class string for the final trace line.

**The pin-exit-code-BEFORE-trim idiom** (`scripts/phase-83-ha-failover.ps1:198-206`) — a repo-wide safety rule, appears at `:134-139`, `:198-206`, `:235-240`:

```powershell
    $wfIdRaw = kubectl -n skp exec statefulset/postgres -- psql -U postgres -d stepsdb -tA `
              -c "SELECT id FROM workflows WHERE name = 'v8-fanout-proof'"
    # Pin the exit code BEFORE the trim: on a failed exec stdout is empty, so `.Trim()` on the raw capture
    # (string-cast, never $null) cannot throw and bypass exit 40.
    $wfIdExit = $LASTEXITCODE
    $wfId = ("$wfIdRaw").Trim()
    if ($wfIdExit -ne 0 -or [string]::IsNullOrWhiteSpace($wfId)) {
        Write-Phase "could not resolve v8-fanout-proof workflow id (psql exit $wfIdExit). Aborting." 'Red'; exit 40
    }
```
Applies to every `kubectl get … -o jsonpath` Phase 87 makes (e.g. reading the grafana pod name for the VER-02 delete).

**HTTP call + status assertion** (`scripts/phase-83-ha-failover.ps1:213-222`):

```powershell
    $startBody = ConvertTo-Json @($wfId)
    try {
        $resp = Invoke-WebRequest -Method Post -Uri 'http://localhost:8080/api/v1/orchestration/start' `
                  -ContentType 'application/json' -Body $startBody -TimeoutSec 15 -ErrorAction Stop
    } catch { Write-Phase "POST /orchestration/start threw: $($_.Exception.Message)" 'Red'; exit 50 }
    if ($resp.StatusCode -ne 204) {
        Write-Phase "activation gate failed — expected 204, got $($resp.StatusCode). Aborting." 'Red'; exit 50
    }
```
This is the `POST /api/dashboards/db` tamper-assertion shape too (`87-RESEARCH.md:772-774`) — but Grafana returns **HTTP 400** with a JSON body, so wrap in `try/catch` and read `$_.ErrorDetails.Message | ConvertFrom-Json` rather than `$resp`.

**Bounded-poll helper with a deadline** (`scripts/phase-83-ha-failover.ps1:250-276`) — the shape for "wait for Grafana `/api/health` to go green":

```powershell
    function Get-CleanFireCount([DateTimeOffset]$since) {
        ...
        try {
            $r = Invoke-RestMethod -Method Post -Uri 'http://localhost:9200/logs-generic.otel-default/_count' `
                   -ContentType 'application/json' -Body $body -TimeoutSec 10 -ErrorAction Stop
            return [int]$r.count
        } catch {
            # 404 lazy-index / transient backend blip => treat as 0 clean fires (keep polling to the deadline).
            return 0
        }
    }
    $preKillDeadline = (Get-Date).AddSeconds(90)
    $cleanFires = 0
    while ((Get-Date) -lt $preKillDeadline) {
        $cleanFires = Get-CleanFireCount $windowStart
        if ($cleanFires -ge 1) { break }
        Start-Sleep -Seconds 5
    }
    if ($cleanFires -lt 1) {
        Write-Phase "no clean pre-kill fire observed within 90s (sequencer precondition failed). Aborting." 'Red'; exit 60
    }
```

**Basic auth over HTTP** (`scripts/phase-67-harness.ps1:323-330`) — the repo's **only** basic-auth call; copy it for `admin:admin` against Grafana:

```powershell
    function Get-ProcQueueDepth([string]$queueName) {
        $b64 = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes('guest:guest'))
        try {
            $r = Invoke-RestMethod -Uri "http://localhost:15673/api/queues/%2F/$queueName" `
                -Headers @{ Authorization = "Basic $b64" } -TimeoutSec 5
            return [int]$r.messages
        } catch { return 0 }
    }
```

#### CORRECTION the planner must carry: phase-83 does NOT write its own report

`scripts/phase-83-ha-failover.ps1:326-337` only **reads** a report that a `dotnet test` analyzer produced, then resolves an exit code:

```powershell
    $report = Get-ChildItem -Path (Join-Path $repoRoot 'tests/BaseApi.Tests/bin') -Recurse -Filter 'phase-83-ha.json' `
              -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match 'analyzer-reports' } | Select-Object -First 1
    if ($report) {
        Write-Phase "HA report: $($report.FullName)" 'Green'
        try {
            $analyzerExit = Resolve-AnalyzerExitCode (Get-Content $report.FullName -Raw | ConvertFrom-Json)
        } catch { ... }
    }
    $verdictClass = (Resolve-SweepClass $analyzerExit).Class
    Write-Phase "HA verdict exit = $analyzerExit ($verdictClass; 0=PASS 1=FAIL 2=INCONCLUSIVE)" ...
    exit $analyzerExit
```
Phase 87 adds **zero C#** (`87-VALIDATION.md:21,27`), so there is no analyzer to write the JSON. The **report-authoring** analog is `scripts/phase-81-sweep.ps1:122-131,141-145`:

```powershell
        $rows += [pscustomobject]@{
            scenarioId   = $id
            verdict      = if ($json) { $json.Verdict } else { 'NO_REPORT' }
            ...
        }
    ...
    $summaryDir = Join-Path $repoRoot 'analyzer-reports'
    New-Item -ItemType Directory -Force -Path $summaryDir | Out-Null
    $summaryPath = Join-Path $summaryDir 'phase-81-summary.json'
    $rows | ConvertTo-Json -Depth 5 | Set-Content -Path $summaryPath -Encoding utf8
    Write-Phase "roll-up artifact: $summaryPath" 'Green'
```
**Composite pattern for Phase 87:** build a `[pscustomobject]` in the phase-83-ha.json field shape (below) → `ConvertTo-Json -Depth 5 | Set-Content -Path analyzer-reports/phase-87-dashboards.json -Encoding utf8` → then feed the *same in-memory object* to `Resolve-AnalyzerExitCode` and `exit` on it. Write the JSON **before** the final assertion/exit, mirroring the "written BEFORE the assert, so its Verdict is authoritative" rule stated at `scripts/phase-83-ha-failover.ps1:308-309` and `scripts/lib/exit-code-resolution.ps1:30-31`.

---

### `analyzer-reports/phase-87-dashboards.json` (data artifact, generated)

**Analog:** `analyzer-reports/phase-83-ha.json` (16 lines — whole file):

```json
{
  "ScenarioId": "phase-83-ha",
  "Verdict": "Pass",
  "ZeroDuplicate": true,
  "GapObserved": true,
  "NotBackfilled": true,
  "RoleFlipVisible": true,
  "BoundedRecovery": true,
  "RecoverySeconds": 15.4390265,
  "KillUtc": "2026-07-19T05:25:27.1969735+00:00",
  "WindowStart": "2026-07-19T05:24:35.3778727+00:00",
  "WindowEnd": "2026-07-19T05:27:27.2129457+00:00",
  "SendCorrIdCount": 4,
  "BucketCount": 4,
  "HumanSummary": "HA-07 verdict=Pass: all five HA-07 claims green: ≤1 corrId/bucket, one empty non-backfilled gap tick, bounded role flip. [zeroDuplicate=True, gapObserved=True, notBackfilled=True, roleFlipVisible=True, boundedRecovery=True, recovery=00:00:15.4390265, buckets=4, gapTicks=1]"
}
```
Schema rules to mirror exactly:
- **Single object** (not an array — contrast `analyzer-reports/phase-85-summary.json`, which is a per-scenario array from the sweep roll-up).
- **PascalCase** field names. (`phase-81-summary.json` / `phase-85-summary.json` rows are camelCase — that's the *sweep* convention, not the *single-proof* convention. Phase 87 is a single proof → PascalCase.)
- `ScenarioId` first, `Verdict` second, then one **boolean per claim** (`ZeroDuplicate`, `GapObserved`, …) — Phase 87's booleans are the requirement ids: `DashboardsProvisioned`, `DatasourceDefaultReadOnly`, `NoPvc`, `UiSaveRejected`, `AllFourClassesPresent`, `AllPanelsNonEmpty`, `FaultPanelsGuarded`, `PodDropdownSuperset`, `ReproducedAfterPodDelete`.
- Measured scalars and UTC timestamps after the booleans (`RecoverySeconds`, `WindowStart`/`WindowEnd` in round-trip `o` format).
- **`HumanSummary` last** — one sentence stating the verdict + why, followed by a bracketed `[key=Value, key=Value, …]` dump of every claim. This is what a reviewer reads first; make it self-contained.

---

### `scripts/phase-87-dashboard-lint.ps1` (test, batch/transform)

**Analog:** `scripts/verify-sourcehash-reproducible.ps1` (145 lines — read in full). This is the repo's only standalone hermetic PowerShell gate that touches no cluster and emits no report.

**Preamble** (`scripts/verify-sourcehash-reproducible.ps1:1-37`) — note this one adds `#!/usr/bin/env pwsh` + `#requires`, uses `[CmdletBinding()]`, and has an `.OUTPUTS` section instead of an exit-code table:

```powershell
#!/usr/bin/env pwsh
#requires -Version 7.0
<#
.SYNOPSIS
    Phase 28 (IDENT-02 / Pitfall 1) — the HIGHEST-RISK cross-OS SourceHash reproducibility gate.

.DESCRIPTION
    Proves the SourceHash embedded by the WINDOWS host SDK build EQUALS the SourceHash embedded by
    the LINUX Docker build of Processor.Sample. This is load-bearing: ...

.OUTPUTS
    Prints "HOST  SourceHash = <64-hex>", "DOCKER SourceHash = <64-hex>", and "MATCH" or "DIVERGED".
    Exit 0 when the two hashes are byte-equal; exit 1 on divergence or build failure.
#>
[CmdletBinding()]
param(
    [string]$ImageTag = "processor-sample:hashcheck"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Repo root = two levels up from this script (scripts/ -> repo root).
$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
```

**Named helper functions above the linear script body** (`:45-89`) — `Read-SerString`, `Get-EmbeddedSourceHash`, each with a block comment explaining the mechanism. Phase 87's equivalents: `Get-PanelExprs($dashboardJson)`, `Assert-VariableShape($templating, $name)`, `Assert-MetricAllowlisted($expr)`.

**Numbered section banners + final verdict block** (`:91-144`):

```powershell
# ===========================================================================
# (1) HOST build — Windows SDK publish, then reflect.
# ===========================================================================
Write-Host "==> HOST build: dotnet publish src/Processor.Sample -c Release" -ForegroundColor Cyan
...
if ($hostHash -eq $dockerHash) {
    Write-Host "MATCH — cross-OS SourceHash is reproducible (host == docker)." -ForegroundColor Green
    exit 0
} else {
    Write-Host "DIVERGED — host and docker SourceHash differ." -ForegroundColor Red
    Write-Host "Remediation (Pitfall 1, SourceHash.targets): confirm forward-slash path normalization" -ForegroundColor Yellow
    ...
    exit 1
}
```
**The remediation block on failure is the pattern worth copying most.** A failing lint must not just say "expr missing `$pod`" — it must print the offending dashboard file, panel title, the expression, and the fix (per `87-VALIDATION.md:79`).

**JSON parsing — no `jq`.** Use `Get-Content <file> -Raw | ConvertFrom-Json` (the idiom at `scripts/phase-83-ha-failover.ps1:331` and `scripts/phase-81-sweep.ps1:117`). For the DASH-04 "no hardcoded UID" check, `Select-String -SimpleMatch 'skp-prometheus'` replaces `grep -q`.

---

## Shared Patterns

### Port-forward lifecycle (applies to `scripts/phase-87-dashboards-verify.ps1`)

**Source:** `scripts/phase-80-up.ps1:116-140` (the 127.0.0.1 discipline + the 8-forward table) and `:167-178` (start + PID capture).

```powershell
# EVERY forward binds --address 127.0.0.1 explicitly — never 0.0.0.0 — so the bridged host ports stay
# loopback-only (threat T-80-17: a 0.0.0.0 bind would expose the cluster to the LAN). Services stay
# ClusterIP (no NodePort/ingress); port-forward is the ONLY host->cluster ingress.
$forwards = @(
    @{ svc = 'svc/baseapi-service'; map = '8080:8080'   },   # WebApi — the only HTTP tier the harness talks to
    @{ svc = 'svc/prometheus';      map = '9090:9090'   },   # analyzer counter scrape
    ...
)
...
$pfProcs = foreach ($f in $forwards) {
    Write-Phase "  port-forward $($f.svc) $($f.map) --address 127.0.0.1"
    Start-Process kubectl -PassThru -WindowStyle Hidden `
        -ArgumentList @('port-forward', $f.svc, $f.map, '-n', 'skp', '--address', '127.0.0.1')
}
```

**Teardown with a recycled-PID guard** — `scripts/phase-83-ha-failover.ps1:350-366`, duplicated verbatim at `scripts/phase-81-sweep.ps1:159-172` and `scripts/phase-80-up.ps1:150-165`:

```powershell
        $pidFile = Join-Path $repoRoot '.k8s-portforward-pids'
        if (Test-Path $pidFile) {
            $pfPids = @(Get-Content $pidFile -ErrorAction SilentlyContinue | Where-Object { $_ -match '\S' })
            foreach ($p in $pfPids) {
                # Recycled-PID guard: only kill if the PID is STILL a live kubectl process. Best-effort — never throws.
                try {
                    $proc = Get-Process -Id ([int]$p) -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -eq 'kubectl' }
                    if ($proc) { Stop-Process -Id ([int]$p) -Force -ErrorAction SilentlyContinue }
                } catch { }
            }
            Remove-Item $pidFile -ErrorAction SilentlyContinue
        }
```

**HARD CONSTRAINT for Phase 87 — do not touch `.k8s-portforward-pids`.** That file is owned by `phase-80-up.ps1:149,175-176` and holds the 8 shared harness forwards. If the Phase-87 script writes to it, the next teardown clobbers/kills the wrong set; if it *reads* it for teardown, it kills all 8 and breaks a concurrent sweep. Keep the grafana forward's `Start-Process -PassThru` handle in an **in-memory variable only** and stop that one PID (with the same recycled-PID guard) in the outer `finally`. This realises `87-RESEARCH.md:360` option (a) and Pitfall 10 (`:843-845`). Grafana's `3000` does not collide with any of the 8 existing host ports (8080/9090/9200/15673/6380/5433/5673/4317).

**Confirm the forward actually carries traffic before proceeding** — `scripts/phase-80-up.ps1:180-201`:

```powershell
# A port-forward reports success on start even before the tunnel carries traffic (Pattern 5 reliability
# caveat), so gate on a REAL 200 from baseapi /health/ready before returning.
$deadline = (Get-Date).AddSeconds(60)
$ready = $false
do {
    try {
        $resp = Invoke-WebRequest -Uri 'http://localhost:8080/health/ready' -UseBasicParsing -TimeoutSec 5
        if ($resp.StatusCode -eq 200) { $ready = $true }
    } catch { }
    if (-not $ready) {
        if ((Get-Date) -ge $deadline) { Write-Phase "... Aborting." 'Red'; exit 11 }
        Start-Sleep -Seconds 2
    }
} while (-not $ready)
```
Phase 87's equivalent gate: poll `http://127.0.0.1:3000/api/health` until `.database -eq 'ok'`. This same gate must be **re-run after the VER-02 pod delete** (the forward dies with the pod and must be restarted).

### `jq` → PowerShell translation table (applies to BOTH new scripts)

`87-RESEARCH.md:716-775` and `87-VALIDATION.md:49-71` are written in bash + `jq`. **`jq` is NOT installed** (`87-RESEARCH.md:1012`). Every excerpt copied from those sections must be translated:

| RESEARCH excerpt (`jq`) | PowerShell equivalent (this repo's idiom) |
|---|---|
| `curl -sf $GF/api/health \| jq -e '.database == "ok"'` | `(Invoke-RestMethod "$GF/api/health" -TimeoutSec 10).database -eq 'ok'` |
| `curl -sf $AUTH $GF/api/datasources/uid/... \| jq -e '.isDefault and .readOnly'` | `$ds = Invoke-RestMethod -Uri … -Headers @{Authorization="Basic $b64"}; $ds.isDefault -and $ds.readOnly` |
| `jq -e '[.[].uid] \| contains(["skp-runtime","skp-business"])'` | `$uids = @((Invoke-RestMethod …).uid); 'skp-runtime','skp-business' \| ForEach-Object { $uids -contains $_ }` |
| `--data-urlencode 'query=…'` | `Invoke-RestMethod -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{ query = $expr }` (POST matches the datasource's `httpMethod: POST`) |
| `jq -e '.data.result \| length > 0'` | `@($r.data.result).Count -gt 0` — **always `@()`-wrap**; a single-element result is not an array in PowerShell (the bug guarded at `scripts/phase-81-sweep.ps1:126`) |
| `jq -S '.dashboard'` byte-compare (VER-02) | `($x \| ConvertTo-Json -Depth 100 -Compress)` on both sides — `ConvertFrom-Json` does not preserve key order, so canonicalise via a recursive sorted re-serialise, or compare the raw response strings |
| `! grep -q 'skp-prometheus' k8s/dashboards/*.json` | `Select-String -Path k8s/dashboards/*.json -SimpleMatch 'skp-prometheus'` → assert no match |
| `date +%s` arithmetic for `start=`/`end=` | `[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()` |
| `python -m json.tool "$f"` | `Get-Content $f -Raw \| ConvertFrom-Json` (fails loud under `$ErrorActionPreference='Stop'`) |

### Legacy comment text — do NOT copy

`scripts/phase-83-ha-failover.ps1:112` describes STEP A as *"phase-80-up.ps1: compose-down (port-collision guard) -> kubectl apply -k k8s/ -> …"*. **There is no compose-down step in `phase-80-up.ps1`** — its STEP 1 is the kube-context guard (`:37-55`) and STEP 2 is `kubectl apply -k k8s/` (`:57-63`). The comment is stale. The live stack is Docker-Desktop k8s **only** (MEMORY `stack-bringup-k8s-only`, `87-RESEARCH.md:40`). Any docker-compose mention in a Phase-87 script or manifest is a bug.

### Doc-comment discipline

Every analog reads the same way: **the comment explains the decision, not the syntax**, and cites the decision/threat/requirement id (`D-03`, `T-80-17`, `HA-07`, `MLBL-03`). Phase 87 should cite `DASH-01..04`, `RTD-01..03`, `BPD-01..03`, `VAR-01..04`, `VER-01..02`, plus the research findings `F-1` (`process_runtime_dotnet_*` not `dotnet_*`), `F-2` (no CPU/uptime/working-set), `F-4`/`F-5` (guarded fault panels), `F-6` (`identityName` not `service_name`), `F-7`/`F-8` (label_values staleness, `allValue: null`).

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `k8s/dashboards/runtime.json` | data artifact | transform | **No Grafana dashboard, and no `.json` config artifact of any kind, exists in this repo.** `k8s/` is 100% YAML; the only checked-in JSON is `analyzer-reports/*.json` (machine-generated verdicts, a completely different shape). There is no `k8s/dashboards/` directory yet. |
| `k8s/dashboards/business.json` | data artifact | transform | Same. |

**Use instead of a false analog:** `87-RESEARCH.md:551-613` — *"Minimal but complete, copy-pasteable skeleton"*. That exact JSON was live-provisioned into real `grafana/grafana:12.3.9` **and** `13.1.1` containers this session (`87-RESEARCH.md:553`), including the `datasource`-type variable, both `label_values()` query variables, and one full `timeseries` panel with `gridPos` / `fieldConfig` / `options.legend` / `targets[]`. It is a higher-confidence starting point than any repo file could be. Supporting sections:

- Top-level field list + the "omit `id`, never include `__inputs`/`__requires`" rule: `87-RESEARCH.md:429-449`
- Variable JSON blocks (a) datasource / (b) `source` / (c) chained `pod`: `87-RESEARCH.md:451-508`
- Runtime panel PromQL table (16 panels, all metric names live-verified): `87-RESEARCH.md:622-641`
- Business panel PromQL incl. `or vector(0)` fault guards and `identityName` grouping: `87-RESEARCH.md:645-696`
- Panel-type rules (`timeseries` / `stat`, never `graph`; object-form `datasource`): `87-RESEARCH.md:541-549`

**Two secondary gaps** (no repo precedent, author fresh with an explanatory comment): `securityContext` and `emptyDir` — neither appears anywhere in `k8s/*.yaml` (verified by grep across all 15 manifests).

---

## Metadata

**Analog search scope:** `k8s/` (all 15 manifests enumerated; 3 read in depth), `scripts/` (all 33 `.ps1` enumerated; 5 read in depth + 1 shared lib), `analyzer-reports/` (all 6 tracked reports enumerated; 2 read), repo root (`CLAUDE.md` / skills dirs — absent).
**Files scanned:** ~60 enumerated, 12 read.
**Pattern extraction date:** 2026-07-27
