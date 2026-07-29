# Business / Pipeline dashboard — operator runbook

**Dashboard:** `SKP Business / Pipeline` (Grafana folder `SKP`, uid `skp-business`, provisioned from
`k8s/dashboards/business.json`).
**Audience:** whoever is on maintenance duty for the Steps API pipeline and did not build it.
**Status of this document:** every row below was produced by driving a real fault against the live
`skp` cluster and reading the value off the rendered panel. Every row cites the scenario that proved
it and the numbers that scenario measured. **A row with no proving scenario is not in this table.**

The companion document is the handoff readiness verdict,
[`88-HANDOFF-VERDICT.md`](../../.planning/phases/88-pipeline-dashboard-maintenance-handoff-proof-fault-injection/88-HANDOFF-VERDICT.md).
Read it before you rely on this dashboard for anything that matters. **It says plainly that four of
the fourteen panels have never been seen in more than one state, and that thirteen of the fourteen
mislead by default.** This runbook tells you how to work around that; it does not pretend it is not
so.

---

## 1. How to reach the dashboard

```powershell
kubectl -n skp port-forward svc/grafana 3000:3000 --address 127.0.0.1
```

Then open **http://127.0.0.1:3000** → folder **SKP** → **SKP Business / Pipeline**. No login is
required: Grafana runs with anonymous access at org role `Viewer`.

**Read this before you plan around it.** That `port-forward` is the *only* way in. The Grafana
Service is a `ClusterIP` with no NodePort and no Ingress, there is no TLS and no SSO, and the
manifest sets `admin`/`admin` as plain environment values — an accepted development posture, matching
the unauthenticated in-cluster Prometheus and Elasticsearch, and explicitly out of scope to fix in
this milestone. **It is nonetheless recorded as blocking gap B1 in the handoff verdict**, because a
maintenance department cannot watch a dashboard it has to be at a specific workstation to open. You
need `kubectl` access to the cluster and a terminal open for as long as you want to look at a panel.

Grafana also has no persistent volume, by design. Deleting the pod throws away all Grafana state and
the dashboards rebuild from the repo. Nothing you do in the UI is saved; a UI "Save" is rejected with
`Cannot save provisioned dashboard`.

## 2. The two dropdowns, and the one trap in them

| Dropdown | What it filters | Populated from |
|---|---|---|
| `source` | service **class**: `webapi`, `orchestrator`, `keeper`, `processor` | `label_values(process_runtime_dotnet_gc_collections_count_total, source)` |
| `pod` | a single pod (`service_instance_id`), chained to the `source` selection | `label_values(…{source=~"$source"}, service_instance_id)` |

**The pod dropdown is a SUPERSET of the pods that are alive right now.** `label_values()` reads the
Prometheus *index*, not the cluster, so it lists recently-dead pods too — measured during Phase 87 at
four keeper pods listed while two were running, even over a 60-second window. If you select a pod
and every panel is empty, **check that the pod still exists** before you conclude the tier is broken:

```powershell
kubectl -n skp get pods
```

This is index granularity, not a Grafana setting, and it will not be fixed by changing the variable.

Every processor pod reports `source="processor"` and the same placeholder service name, so **`source`
cannot separate two processors**. Panel 3 splits by image identity (`identityName`) for exactly this
reason; panel 5 splits by `processorId`, which renders as a raw database GUID.

## 3. How far back you can look

Prometheus retention on this cluster was **measured at 225 hours (about 9.4 days)** — the oldest
sample found was `2026-07-19T07:38:06Z`, probed over a 336-hour horizon that was not the limiting
factor (`analyzer-reports/phase-88-wave0-probe.json`, PQ-07). *Phase 87 recorded roughly 12 hours;
that figure is wrong by a factor of nineteen and is superseded by the measurement.*

Two consequences:

- You can investigate an incident from **last week**, not just from this morning.
- **The healthy bands quoted in this runbook are still run-time observations, not durable facts** —
  but for a different reason than retention. Every band was captured under a particular host load,
  set of pod identities and traffic pattern. Comparing today's reading against a band measured under
  different conditions reports a change in *conditions* as drift. **If you are tuning an alert off a
  number in this table, re-measure it first.**

The stored sample resolution is **60 s** (the .NET SDK export cadence, not the Prometheus scrape
interval), and the datasource's `timeInterval` is `60s`, which floors Grafana's `$__rate_interval`
at **240 s**. Both numbers matter below.

## 4. The shortest outage this dashboard can show you

Measured by a step-ladder of controlled processor-tier outages
(`analyzer-reports/phase-88-LADDER-01.json`):

| Panel family | Shortest fault that moved the panel | Basis |
|---|---|---|
| Rate/counter panels (1, 3, 4, 5, 9, 10, 11, 12) | **240 s** | measured; rungs driven 30 s / 60 s / 120 s / 240 s / 480 s — the first two consecutive out-of-band samples appeared at 240 s |
| Gauge panel (14) | **120 s** | measured; rungs 30 s / 60 s / 120 s. This one is arithmetic, not noise: a gauge sampled every 60 s cannot record an event shorter than two exports |
| Zero-floor counters (2, 6, 7, 8, 13) | *any duration* — but see the caveats | a `0 → 1` counter stays at 1 for the whole `$__range` window instead of decaying back |

**In plain language: a short outage can be invisible here.**

- A **30-second total outage of the processor tier produced a dip of one tenth of one percent** on
  panel 3. On the dashboard, it did not happen.
- A **120-second** total outage produced a visible 33 % drop — and still did not clear the
  two-consecutive-samples rule, missing it by exactly one sample.
- The reason is not that the panel is broken. The broker **buffers** the work while the tier is
  down and the tier **drains the backlog the moment it comes back**, inside the same 240 s rate
  window, so the window's total is largely restored and the rate barely moves. The measured dip was
  smaller than the model predicted at every single rung.
- **240 s is specific to this stack**, because the mechanism is queue depth plus drain speed. It will
  change if the queue, the prefetch or the fault class changes. Re-measure before quoting it.

If someone reports a brief outage and the dashboard looks clean, **the dashboard is not evidence that
it did not happen.** Go to the logs.

---

## 5. The runbook

Every row: the symptom you see, the panel it lives on, the band the panel holds on a healthy stack,
what the symptom means, what to do, the scenario that proved it, the values that scenario measured,
and the shortest outage that row could detect.

Scenario ids resolve to `analyzer-reports/phase-88-<ID>.json`. Bands are from
`analyzer-reports/phase-88-BASE-01.json` (DISC-01) unless the row says otherwise; each scenario
re-captures its own local band after any pod rollout, so the row quotes the band that reading was
actually scored against.

### 5.1 Rows proved by driving a fault

| Symptom (what you see) | Panel | Healthy band | What it means | Next action | Proving scenario | Measured before → after | Shortest outage this row detects |
|---|---|---|---|---|---|---|---|
| Both lines on the conservation panel **fall together and keep falling** — no blank, just a slide toward zero | **1 · `Orchestrator consumed vs sent (conservation)`** | consumed 0.645–0.788 /s, sent 1.195–1.477 /s (envelope 0.645–1.477) | The orchestrator tier is gone or wedged. `sent` normally runs about 1.8× `consumed` because it sends to processors, to its own result-post queue and to the keeper | `kubectl -n skp get deploy orchestrator` and `kubectl -n skp get pods -l app=orchestrator`. Expect 3 replicas. Check orchestrator logs for the same window. **A 240 s total outage did NOT reach "No data"** — you will see a fall, never a blank, so do not wait for the panel to empty | **SCALE-02** (orchestrator scaled to 0 for 240 s) | consumed 0.734 → 0.139 /s and sent 1.370 → 0.257 /s over six 60 s samples, against a local band 0.632–0.776; 3 consecutive samples outside | 240 s |
| One image's `consumed`/`sent` pair **slides downward over about four minutes** and eventually goes "No data" | **3 · `Processor consumed vs sent by image (conservation)`** | consumed 0.675–0.825 /s, sent 0.645–0.789 /s | That processor image has stopped taking work. The fall is smeared over four minutes by the 240 s rate lookback | `kubectl -n skp get deploy processor-sample`; check the pod logs. **The first in-fault sample still reads inside the healthy band** — judge the slope over several minutes, not one reading | **SCALE-01** (processor-sample scaled to 0 for 420 s) | 0.737 → 0.632 → 0.447 → 0.169 → 0, then "No data", against a local band 0.675–0.825; 4 consecutive samples outside | 240 s (measured rung by rung: 30 s no, 60 s no, 120 s no, 240 s yes, 480 s yes) |
| The structural stat reads **"No data"** | **4 · `Orchestrator sends minus processor pickups (structural - read the trend)`** | 37.4–98.6 at a **120 s** window (it cannot be evaluated at all at 60 s); the shipped 1 h default window sat at a recorded level of 2.11 on a healthy stack | **Blank IS the signal.** The expression subtracts an `increase()` over processor pickups; with the processors gone that term is an *empty vector*, and `A − empty` is empty. The stat whose own title says "read the trend" renders nothing under exactly the fault it exists to reveal | Treat "No data" here as *processors are not picking up work* and go straight to panel 3 and to `kubectl -n skp get deploy processor-sample`. Do **not** read "No data" as a broken panel. Never read this number as a loss count — it is structurally non-zero on a healthy stack | **SCALE-01** | baseline 50 / 74 / 72 / 70 / 74 at 120 s sub-windows → Rendered, then **NoData, NoData**; 2 consecutive samples outside | 240 s |
| The per-processor gap **decays toward zero and then the panel empties** — it never spikes | **5 · `Per-processor dispatch gap`** | 0.512–0.654 /s (a permanent, structural, non-zero offset) | **The panel going blank IS the signal.** The obvious expectation — "processors die, orchestrator keeps sending, so the gap climbs" — is wrong here, because this pipeline is request/response: with the processors gone the orchestrator receives no completions, its own send rate falls too, and the gap *shrinks* before the series stop matching and the panel empties | Watch for the panel **disappearing**, not for a number climbing. Then panel 3, then `kubectl -n skp get deploy processor-sample`. The series are labelled by raw `processorId` GUID; map it with `kubectl -n skp exec deploy/baseapi-service` against the processors API, or read panel 3, which is labelled by image identity instead | **SCALE-01** | 0.63 → 0.588 → 0.4 → 0.234 → 0.183, then **"No data"**, against a local band 0.508–0.659; 2 consecutive samples outside | 240 s |
| The heartbeat **fades over about three minutes** and then goes "No data" | **9 · `Keeper L2 probe heartbeat`** | **0.180–0.220 /s per pod** (measured mean exactly 0.2000 on each of two keeper pods; the band is a ±10 % floor, because the true spread is about 0.0006 wide) | The keeper has stopped probing Redis: it is wedged or has lost L2, and it will not be able to recover messages if asked to. This is the first panel to check when asking "is the keeper alive" | `kubectl -n skp get deploy keeper` (expect 2 replicas) and the keeper logs. **Do not wait for the line to drop out instantly** — `rate(counter[240s])` keeps producing a shrinking value for roughly three minutes after the last real sample | **SCALE-03** (keeper scaled to 0 for 480 s) | 0.2000 → 0.197 → 0.203 → 0.168 → 0.129 → 0.0644, then **"No data"** ×3, against band 0.180–0.220; 3 consecutive samples outside | 240 s |
| The keeper conservation lines **leave zero** | **2 · `Keeper consumed vs sent (conservation)`** | exactly **0–0** on both series | A recovery event happened: the keeper took in recovery messages and sent them back out. If the two lines move **together**, recovery conserved and nothing was lost | Confirm in the keeper logs for the same window: `REINJECT sent {MessageId} reinject` means consumed **and** redispatched. Only worry if `consumed` moves and `sent` does not — and see panel 8's row, because the gap panel cannot show you that | **ZERO-02** (real keeper recovery event, reinject intact) | 0, 0, 0.00944, 0.00812, 0.00958, 0.00895 /s against a band of exactly 0–0; 4 consecutive samples outside | any duration in principle — **but see the warning below** |
| A recovery **definitely happened** (the logs say so) and this panel shows **an unbroken 0** | **2 · `Keeper consumed vs sent (conservation)`** | exactly **0–0** | The panel uses `rate()`, which differences consecutive 60 s samples. A counter series is born already carrying its final value, so a recovery burst that starts and finishes inside one 60 s export interval produces **no visible rate at all** | **Trust the keeper log, not the panel.** Search for `REINJECT sent` / `REINJECT drop` over the window. Also note: the `or vector(0)` guard row does **not** disappear when the real series arrives, so you can see a phantom `consumed` at 0 rendered beside the real `consumed keeper` with nothing to say which is which | **ZERO-02** (run 1, kept on disk as `analyzer-reports/phase-88-ZERO-02-run1-discarded.json`) | a genuine event — both replicas claimed, three `KeeperReinject`s consumed and sent — rendered as **0 throughout** | a burst inside one 60 s interval can be invisible at **any** duration |
| A route's request rate **climbs**, or an unfamiliar row appears | **10 · `WebApi request rate by route`** | 0–0.118 /s across 13 route series; **7 of the 13 sit at exactly 0** on a healthy stack, and the pipeline runs on a cron rather than on HTTP, so near-zero is normal | Someone or something is driving HTTP traffic at the WebApi | Identify the caller. **The row labelled `Value` is not a bug** — it is traffic to an *unmatched* path, which carries no `http_route` label, and it is the row that moves most cleanly. Read panel 12 beside this one for the status mix | **WEB-01** (about 10 parallel requesters, 2456 requests over 480 s) | 0 → 0.0556 → 0.18 → 0.314 → 0.408 → 0.436 /s against a band of exactly 0–0; 6 consecutive samples outside | 240 s |
| p95 latency **spikes at the moment load arrives** and then settles back | **11 · `WebApi p95 request duration by route`** | 0.32–12.08 ms across six route series (one series banded −5.06 to 21.83 ms — the band is wide and partly negative) | **The leading edge is the signal, not the level.** The API is genuinely fast once warm, so a sustained-load watcher sees nothing | Read the **onset**, not the plateau. If p95 stays high for more than a few minutes, that is a real regression — check the runtime dashboard's GC and thread-pool panels next, since every route slowing together points at resource pressure rather than at one code path | **WEB-01** | 22.0 → 19.7 → 11.2 → 4.88 → 4.78 → 4.78 ms; back **inside** the healthy band within three minutes; 2 consecutive samples outside | 240 s |
| A **4xx or 5xx** series carries a non-zero value | **12 · `WebApi status-code mix`** | `200` 0.191–0.576 /s; `201`, `400`, `404`, `422` all banded at exactly **0–0** | Callers are getting errors. 4xx usually means a caller is sending something malformed; sustained 5xx is a server-side fault | **Read the value, never the legend.** All six status series were present and rendering zero before anything drove them, and a code that has occurred once never leaves the legend — Prometheus retains the series for as long as the WebApi process lives, and that pod has been up since Phase 80. Then read panel 13 for severity, and the WebApi logs | **WEB-01** (drove 404s and 400s) | two exactly-`0–0` bands → 0.0516 → 0.168 → 0.299 → 0.402 → 0.439 → 0.436 /s; 6 consecutive samples outside | 240 s |
| `kestrel active` rises off zero and stays there | **14 · `WebApi in-flight requests and Kestrel connections`** | envelope −0.207 to 1.207; in detail: `in-flight` **exactly 0**, `kestrel queued` **exactly 0**, `kestrel active` **exactly 0 on a genuinely idle stack** *(amended 2026-07-29 — BASE-01 recorded a mean of 0.5, but that capture ran while the phase was itself driving traffic at `baseapi-service`, so 0.5 was residual load, not a resting value. A fresh idle render on 2026-07-29 read `0.0000` across six pinned 60 s sub-windows. The envelope is unchanged and still holds.)* | Clients are holding connections open. **Only `kestrel active` is a working instrument on this panel** — see the warning below | Read it as a *connection* count, which is not the same quantity as the panel title's "in-flight requests". Cross-check panel 10 for the actual request rate. **Do not treat a reading of 0 as a fault** — 0 is the healthy resting value | **WEB-01** | `kestrel active` 0 → 2.5 → 4.5 → 5 → 5 → 3.5 → 4.5 while `http_server_active_requests` stayed at exactly 0 for all six export ticks under 2456 requests; 6 consecutive samples outside | **120 s** (gauge regime — measured 30 s no, 60 s no, 120 s yes) |

### 5.2 Rows for the four panels this phase could NOT prove

These four panels have **only ever been observed at zero**. Their reasons are recorded in
[`88-FINDINGS.md`](../../.planning/phases/88-pipeline-dashboard-maintenance-handoff-proof-fault-injection/88-FINDINGS.md)
§1. Each row tells you what to do **instead of** trusting the panel.

| Symptom (what you see) | Panel | Healthy band | What it means | Next action | Proving scenario | Measured before → after | Shortest outage this row detects |
|---|---|---|---|---|---|---|---|
| A green **`0`** | **6 · `Orchestrator unresolved steps in range`** | exactly **0–0** | **`0` here means "either no dangling next-step edge occurred, OR this counter has never been emitted at all".** The panel is `… or vector(0)`-guarded and nothing on it distinguishes the two. This phase could not drive it: the API refuses a dangling `nextStepIds` entry with **422 at stage `step-create`**, and deleting the L2 step key does not work either — the orchestrator rewrites the whole snapshot from Postgres on any stop/start | **If it reads 1 or more:** each count is a step that entered the pipeline and never reached a processor — work has been silently lost. Search the orchestrator logs over the panel's range for `Dangling next-step id` (one log line per increment, from `OrchestratorPrePipeline.cs:184`) to identify which step. The increment site never throws and always acks, so **a non-zero reading is credible** | accepted-unproven — **ZERO-01** (recorded, not driven) and `88-FINDINGS.md` §1 record 1 | band exactly 0–0 → observed `0, 0, 0, 0, 0, 0` over six 60 s windows; also read at exactly `0` as a cross-talk control during **ZERO-03** while a real fault fired elsewhere | any duration, once the counter fires |
| A green **`0`** | **7 · `Processor dropped spawns in range`** | exactly **0–0** | **`0` here means "no evidence", not "no dropped spawns".** Same guard, same ambiguity. This panel is accepted-unproven **by user decision, not by measurement** — driving it needs the broker unreachable at the instant a fan-out entry spawns, the riskiest injection in the set, and it was deliberately not attempted | **If it reads 1 or more:** open the **processor logs for the same window** and search **`SpawnSendExhaustedException`**. The hook that increments this counter logs `SpawnToPost drop: send to -post exhausted ExecutionId={ExecutionId}` (`ProcessorPipeline.cs:309`) immediately before the increment and the throw — **one log line per increment**. The entry will have been **nack-requeued**, so expect a **matching re-fire of the same entry**. **Check broker health in the same window** — an exhausted retry chain almost always means RabbitMQ was unreachable. **Panel 7 has never been observed non-zero on this stack: treat a first non-zero reading as credible, but confirm against the logs before acting** | accepted-unproven — user decision, recorded in `88-PROBE-DECISIONS.md`; zero **measured** as an exactly-zero cross-talk control in **ZERO-03** | band exactly 0–0 → exactly `0` across every sub-window of **ZERO-03**, while a real fault was firing elsewhere on the stack | any duration, once the counter fires |
| A green **`0`** | **8 · `Keeper consumed - sent gap in range`** | exactly **0–0** | **This is the least trustworthy panel on the dashboard. Do not use it.** Three measured reasons: (1) at a 60 s range **both `increase()` terms return zero points** and the `or vector(0)` guard supplies the `0` outright — nothing measured it; (2) the arithmetic cannot show a suppressed redispatch at all, because the keeper counts a send it did not make; (3) the only code path that *does* open a gap — the keeper's drop branch — could not be reached by any lever available here | **Search the keeper logs, not this panel.** `REINJECT drop {MessageId} drop` (`ReinjectConsumer.cs:77`) **is the failure this panel's title promises** — consumed, never sent. `REINJECT sent {MessageId} reinject` means conserved. **No keeper lines at all** means no recovery traffic happened, which is not the same as nothing being lost. Read panel 9 beside it to know whether the keeper was even alive. If you must read this panel, **set the dashboard range to at least 120 s** — below that it renders guard whatever the stack is doing | accepted-unproven — **ZERO-03** (fault fired exactly as designed; the gap still could not open) and `88-FINDINGS.md` §1 record 3 | band exactly 0–0 → `0, 0, 0, 0, 0, 0`. Decomposed over the same window at 120 s: consumed `0, 2.0001, 0, 0, 2.0024` and sent `0, 2.0001, 0, 0, 2.0024` — **byte-identical term for term** while messages were being dropped | any duration in principle; **nothing at all below a 120 s range** |
| A green **`0 %`** | **13 · `WebApi 5xx ratio`** | exactly **0–0** (threshold turns red above `0.0001`) | **`0 %` here means "either no server errors, OR the 5xx counter has never been emitted at all".** This phase enumerated **seven** endpoints and found **no 5xx on safe input** — five 404s, one 400, one 422. That is a good result about the product and a blocking one for this panel | **If it goes above 0:** corroborate against the **WebApi logs for the same window** before acting, and read panel 12 beside it to see which code. One property changes over time: **once one 5xx has ever occurred the numerator series exists permanently**, and from that date the panel computes honestly. Nothing on the panel tells you which side of that date you are on | accepted-unproven — **WEB-02** (recorded, not driven) and `88-FINDINGS.md` §1 record 4 | band exactly 0–0 → `0, 0, 0, 0, 0, 0` over six 60 s windows, under real traffic **including 404s and 400s**; also exactly `0` as a cross-talk control in **ZERO-01** and **ZERO-03** | any duration, once the counter fires |

---

## 6. Panels that mislead by default

**Thirteen of the fourteen panels on this dashboard will mislead you in at least one specific way.**
Panel 1 is the only exception. This is the short operator version; the mechanism for each is in the
handoff verdict, section (b).

**The four you are most likely to act on wrongly:**

- **Panel 4 (`Orchestrator sends minus processor pickups (structural - read the trend)`) — blank is
  the alarm.** It renders **"No data"** under exactly the fault it exists to reveal. It is also
  structurally non-zero on a healthy stack and grows with the window width, so **it is not a loss
  count** and "it should be zero" is a guaranteed false positive.
- **Panel 5 (`Per-processor dispatch gap`) — watch it disappear, not climb.** It decays toward zero
  and then empties. It never spikes. Anyone waiting for the gap to grow will see the panel vanish
  instead.
- **Panel 9 (`Keeper L2 probe heartbeat`) — the y-axis lies about the variance.** This is a
  metronome: a mean of exactly 0.2000 per pod with a true spread around 0.0006 wide. Grafana
  auto-scales the axis to the data, so that hair-thin band fills the whole panel and a perfectly
  steady heartbeat renders as dramatic volatility. **Read the numbers in the legend, not the shape of
  the line.** Note also that the panel's own tooltip says *"a flat 0.4 probes/sec"* — that is the
  **two-pod sum**, while the panel renders **per pod at 0.2**. The tooltip is not wrong about the
  system; it is describing something other than what you are looking at.
- **Panels 2, 6, 7, 8 and 13 — a green `0` is not an assurance.** All five carry an `or vector(0)`
  guard, which means the panel renders `0` when the underlying counter has **never been emitted at
  all**. Nothing on the dashboard distinguishes "healthy" from "never instrumented" for any of them.
  **Four of the five have never once been observed non-zero on this stack.** Corroborate against the
  logs, as the rows in §5.2 describe.

**The rest, in one line each:**

- **Panel 2** — `rate()` is blind to a recovery burst confined to one 60 s export interval, and the
  guard row does not disappear when the real series arrives, so a phantom `consumed` at 0 renders
  beside the real one.
- **Panel 3** — the fall under a *total* processor outage is a four-minute **slope, not a cliff**,
  and its first in-fault sample still reads inside the healthy band.
- **Panel 8** — the worst of the fourteen, measured three ways. Its green `0` at a short range is
  supplied by the guard and is not a measurement of anything.
- **Panel 10** — the row labelled **`Value`** is real traffic to an unmatched path, and it is the row
  that moves most cleanly; 7 of the 13 route series are permanently flat at zero.
- **Panel 11** — the signal is the leading edge, not the level; the band is wide and partly negative.
- **Panel 12** — status series **never leave the legend**. Read values, never legend membership.
- **Panel 13** — becomes honest only *after* the first 5xx ever occurs.
- **Panel 14** — **one of its three series is a dead instrument.** `http_server_active_requests` read
  exactly 0 at every export tick under 2456 sustained requests: requests finish in single-digit
  milliseconds and the gauge is sampled once every 60 s, so in-flight work is essentially never
  caught. Its tooltip blames idleness for that zero; **the cause is the export cadence, not
  idleness**, and the tooltip's advice to "investigate in-flight requests staying above zero" is
  about a series that essentially cannot be sampled above zero. `kestrel queued` is likewise flat at
  zero. The only readable series is `kestrel active`.

---

## 7. Quick reference — which panel do I open?

| Question | Panel | Caveat |
|---|---|---|
| Is the keeper alive? | **9 · `Keeper L2 probe heartbeat`** | fades for ~3 min before going blank |
| Is the orchestrator keeping up? | **1 · `Orchestrator consumed vs sent (conservation)`** | the only panel on this dashboard with no misleading-by-default flag |
| Is a processor image stuck? | **3 · `Processor consumed vs sent by image (conservation)`** | judge the slope over minutes, not one sample |
| Have the processors stopped picking up work? | **4** blank, **5** blank | blank is the signal on both |
| Did a recovery event happen? | keeper logs (`REINJECT sent` / `REINJECT drop`), then **2** | the panels can render 0 through a real event |
| Was anything lost in recovery? | keeper logs — **not panel 8** | panel 8's arithmetic cannot show it |
| Is the API erroring? | **12 · `WebApi status-code mix`**, then **13 · `WebApi 5xx ratio`** | read values, not the legend |
| Is the API slow? | **11 · `WebApi p95 request duration by route`** | read the onset |
| Who is calling the API? | **10 · `WebApi request rate by route`** | `Value` = unmatched path |
| Is the API saturated? | **14 · `WebApi in-flight requests and Kestrel connections`** | only `kestrel active` works |
| Did work get silently dropped? | orchestrator logs (`Dangling next-step id`) and processor logs (`SpawnSendExhaustedException`) — panels **6** and **7** cannot tell you on their own | both guarded zeros |

---

*Evidence base: `analyzer-reports/phase-88-discrimination.json` (the fourteen-row discrimination
matrix), `analyzer-reports/phase-88-BASE-01.json` (42 healthy bands),
`analyzer-reports/phase-88-LADDER-01.json` (both minimum detectable durations), and the eight
scenario artifacts `phase-88-{BASE-01,WEB-01,WEB-02,SCALE-01,SCALE-02,SCALE-03,ZERO-01,ZERO-02,ZERO-03}.json`.
Written by Phase 88 (`.planning/phases/88-pipeline-dashboard-maintenance-handoff-proof-fault-injection/`),
2026-07-29. Phase 88 changed no dashboard JSON and no `src/` file — every number here was read off
the dashboard as shipped.*
