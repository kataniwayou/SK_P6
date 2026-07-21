---
phase: 80
slug: deploy-full-system-to-local-kubernetes-docker-desktop-port-c
status: approved
nyquist_compliant: true
wave_0_complete: false
created: 2026-07-18
---

# Phase 80 — Validation Strategy

> Per-phase validation contract for feedback sampling during execution.
> **This phase's deliverables are YAML manifests + PowerShell scripts** — validation is structural (manifest lint via `--dry-run`) + behavioral (the k8s health gate + the reused happy-path round-trip proof), NOT new C# unit tests. The C# seeder (`~FanOutSeeder`) and analyzer (`~Analyze_Window_Yields_Pass`) already exist and are reused as-is.

---

## Test Infrastructure

| Property | Value |
|----------|-------|
| **Framework** | Structural: `kubectl apply --dry-run` (client + server). Behavioral: reused xunit.v3 seeder/analyzer under Microsoft.Testing.Platform (`tests/BaseApi.Tests`) + PowerShell sweep. No new C# tests authored this phase. |
| **Config file** | `tests/BaseApi.Tests/BaseApi.Tests.csproj` (reused); new `k8s/kustomization.yaml` (aggregator) |
| **Quick run command** | `kubectl apply -k k8s/ --dry-run=server` (schema + API-server validate) |
| **Full suite command** | `pwsh -File scripts/phase-80-up.ps1` (bring-up + Ready gate) → k8s reset → seed → `POST /start`==204 → `pwsh -File scripts/phase-68-sweep.ps1 -ScenarioIds TEST-01` (happy-path round-trip) |
| **Estimated runtime** | ~5–8 min cold (ES ~60s startup + rollout waits + seed + sweep) |

---

## Sampling Rate

- **After every task commit:** `kubectl apply -k k8s/ --dry-run=server` (manifests validate) + `kubectl -n skp get pods` sanity check.
- **After every plan wave:** `pwsh -File scripts/phase-80-up.ps1` reaches all-Ready (10 tiers, incl. keeper×2 + processor-sample×2).
- **Before `/gsd-verify-work`:** full happy-path chain green — bring-up → reset(k8s) → seed → 204 → `phase-68-sweep.ps1 -ScenarioIds TEST-01` PASS, all Prometheus + ES signals present.
- **Max feedback latency:** ~30s for the dry-run lint gate; ~8 min for the full round-trip gate.

---

## Per-Task Verification Map

> Success bar from ROADMAP §80 / CONTEXT D-16 (no REQUIREMENTS.md for this milestone).

| Success-bar item | Behavior | Test Type | Automated Command | File Exists | Status |
|------------------|----------|-----------|-------------------|-------------|--------|
| Manifests are valid k8s | Schema-valid, apply-able (incl. namespace/labels via kustomization) | lint | `kubectl apply -k k8s/ --dry-run=server` | ❌ W0 (manifests + kustomization) | ⬜ pending |
| Full stack Healthy | All 10 tiers Ready (incl. keeper×2, processor-sample×2) | smoke | `kubectl -n skp rollout status statefulset/… deployment/… --timeout=…` inside `phase-80-up.ps1` | ❌ W0 (bring-up script) | ⬜ pending |
| Images current (SourceHash) | Container assembly hash == host-built `Processor.Sample.dll` hash (D-05) | smoke | `pwsh scripts/verify-sourcehash-reproducible.ps1` + build-before-seed order + `kubectl rollout restart` | ✅ (verify script exists) | ⬜ pending |
| Reset reaches k8s | FLUSHALL redis + FK-safe graph DELETE hit k8s pods (Pitfall 1) | smoke | k8s reset via `kubectl exec` (re-targeted from `docker exec`) | ❌ W0 (reset re-target) | ⬜ pending |
| Round-trip end-to-end | seed → `POST /orchestration/start` == 204 | integration | `~FanOutSeeder` + activation gate in harness (STEP C/E) | ✅ (harness/seeder exist; DB access needs re-target) | ⬜ pending |
| Verifiable from Prometheus | `orchestrator_messages_sent_total` present (fires); conservation holds | integration | `phase-68-sweep.ps1` reads `localhost:9090/api/v1/query` via port-forward | ✅ (sweep exists) | ⬜ pending |
| Verifiable from ES | log docs present in-window (analyzer verdict PASS) | integration | analyzer `~Analyze_Window_Yields_Pass` reads `localhost:9200` via port-forward | ✅ (analyzer exists) | ⬜ pending |

*Status: ⬜ pending · ✅ green · ❌ red · ⚠️ flaky*

---

## Wave 0 Requirements

- [ ] `k8s/*.yaml` (~12 manifests: namespace, secret, 2 configmaps, 4 StatefulSets + headless services, 5 app Deployments, 3 ClusterIP services) + `k8s/kustomization.yaml` — the port itself.
- [ ] `scripts/phase-80-build.ps1` — 5× `docker build -t <name>:local` (build-before-seed ordering, D-05).
- [ ] `scripts/phase-80-up.ps1` — apply manifests → phased `kubectl rollout status` → `kubectl rollout restart` (image currency after rebuild) → start port-forwards (8080/9090/9200/15673/6380→6379/5433→5432).
- [ ] k8s reset path — re-target `phase-65-reset.ps1`'s `docker exec`/`docker compose exec` calls to `kubectl exec` (or a `phase-80-reset.ps1` sibling); re-target harness STEP B1 (`docker compose restart orchestrator` → `kubectl rollout restart`) and STEP D wf-id lookup (`docker compose exec postgres psql` → `kubectl exec … psql`).

---

## Manual-Only Verifications

| Behavior | Success-bar item | Why Manual | Test Instructions |
|----------|------------------|------------|-------------------|
| Docker Desktop k8s enabled + `hostpath` StorageClass present | Full stack Healthy | One-time host/cluster precondition, not scriptable in CI | `kubectl config current-context` == `docker-desktop`; `kubectl get storageclass` shows `hostpath` (default) |
| No live compose stack occupying forward ports | Verifiable from Prometheus/ES | Host-port mutual exclusion (Pitfall 5) — compose + k8s forwards collide on 8080/9090/9200/… | `docker compose ps` empty (or down) before `phase-80-up.ps1` |

---

## Validation Sign-Off

- [x] All tasks have an `<automated>` verify (dry-run lint or rollout/sweep signal) or a Wave 0 dependency
- [x] Sampling continuity: no 3 consecutive tasks without automated verify
- [x] Wave 0 covers all MISSING references (manifests, build/up scripts, reset re-target)
- [x] No watch-mode flags
- [x] Feedback latency < ~30s (lint) / < ~8min (full round-trip)
- [x] `nyquist_compliant: true` set in frontmatter

**Approval:** approved 2026-07-18 (plan-checker: 0 blockers)
