# Business dashboard — handover briefing

**Read this before you rely on the dashboard. Two minutes.**

This is the spoken half of the handover. The detail lives in
[`business-dashboard-runbook.md`](./business-dashboard-runbook.md); this page exists to make sure
one specific idea lands, because it is the idea most likely to get someone hurt.

The dashboard was proven by driving real faults at a live cluster and asserting, through the
rendered Grafana panel, that each panel moved — and that the panels it should not affect stayed
put. Ten of fourteen panels passed that test. Four did not, and they do not *look* different
from the ten that did.

---

## 1. The one thing you must take away

> **A green `0` is not a health claim. On four panels it means "nothing was measured".**

Grafana renders `0` identically whether the number is a measurement or a placeholder. Four panels
can show you a confident, calm, green zero while the thing they are named after is happening.

| Panel | Title | Why its zero is not evidence |
|---|---|---|
| **8** | `Keeper consumed - sent gap in range` | **The worst one — see below.** |
| **6** | `Orchestrator unresolved steps in range` | The fault it exists to reveal cannot be produced: the API refuses a dangling edge with a 422 at step-create, so no unresolved step can be created to test it. |
| **7** | `Processor dropped spawns in range` | Never driven — a standing decision, not a measurement. |
| **13** | `WebApi 5xx ratio` | The fault it exists to reveal cannot be produced: no safe input across seven endpoints yields a 5xx. Observed at `0,0,0,0,0,0` throughout a 2456-request drive. |

If someone points at one of these four and says "we're fine, it's zero" — they have not read this
page.

---

## 2. Panel 8 in detail, because it is the sharp edge

`Keeper consumed - sent gap in range` promises to show you messages the keeper consumed but never
sent. That is the definition of silent message loss. It is the single most valuable question the
dashboard could answer, and **the panel cannot answer it.** Three independent reasons, all measured:

1. **At a 60-second range, both `increase()` terms return zero points**, and the panel's
   `or vector(0)` guard supplies the `0` outright. Nothing measured it. Below a 120-second range
   the panel renders the guard whatever the stack is doing.
2. **The arithmetic cannot express the failure.** The keeper counts a send it did not make —
   `ReinjectConsumer.cs:106-116` calls `CountSent` on *both* branches, by design, because the
   component was built to be telemetry-invisible. Consumed and sent move together even when they
   should diverge.
3. During the ZERO-03 scenario, with the fault firing exactly as designed, the consumed and sent
   terms came back **byte-identical term for term while messages were being dropped**:
   consumed `0, 2.0001, 0, 0, 2.0024`, sent `0, 2.0001, 0, 0, 2.0024`.

**What to do instead.** Search the keeper logs, not the panel:

- `REINJECT drop {MessageId} drop` — **this is the failure the panel's title promises.** Consumed, never sent.
- `REINJECT sent {MessageId} reinject` — conserved, nothing lost.
- **No keeper lines at all** — no recovery traffic happened. That is *not* the same as nothing being lost.

Read **panel 9** (`Keeper L2 probe heartbeat`) beside the logs to know whether the keeper was even
alive during the window you are asking about.

---

## 3. A short outage is invisible

Detection thresholds were measured, not estimated, and they differ by panel type:

| Panel type | Minimum detectable outage |
|---|---|
| Counter / rate panels | **240 seconds** |
| Gauge panels | **120 seconds** |

**A 30-second total processor outage produced a 0.1% dip.** Nobody would ever see it.

The cause is structural and worth understanding: the broker buffers while a tier is down, and the
tier drains the backlog on recovery *inside the same rate window*, so the window total is largely
restored. A queued pipeline hides short outages from a rate panel. This falsified the project's own
prior estimate of 120 seconds for counters — the real number is twice that.

**Consequence:** the dashboard is not an alerting surface for brief faults. Absence of a dip is not
evidence of absence of an outage.

---

## 4. Three panels where blank is the signal, not a bug

Operators reflexively read "No data" as a broken panel. On these it is the finding:

- **Panel 5** (`Per-processor dispatch gap`) — **empties without ever spiking.** This pipeline is
  request/response, so the orchestrator's own send rate falls along with the processors. Do not
  wait for a spike that will not come.
- **Panel 4** (`Orchestrator sends minus processor pickups`) — renders **"No data" under exactly the
  fault it exists to reveal**, because subtracting an empty vector yields empty. Also: it needs at
  least a 120-second range to render at all; the stored resolution is 60 seconds and `increase()`
  needs two samples.
- **Panel 9** (`Keeper L2 probe heartbeat`) — **fades over about three minutes** before going blank,
  because `rate(counter[240s])` keeps producing a shrinking value after the last real sample. Do
  not wait for the line to drop out instantly.

---

## 5. Two shipped tooltips contradict their own panels

Found during the proof, recorded and deliberately **not fixed** — changing the dashboard belongs to
a phase that owns it. Until then, do not trust these two descriptions:

- **Panel 9's tooltip quotes the two-pod sum (0.4); the panel renders per pod (0.2).**
- **Panel 14's tooltip blames idleness for a zero** that was measured flat under 2456 sustained
  requests. The real cause is the 60-second gauge cadence.

On **panel 14** more generally: only `kestrel active` is a working instrument. `http_server_active_requests`
read **exactly 0 at every export tick under 2456 requests** — it is a dead instrument. Read the panel
as a *connection* count, which is not the quantity its title claims, and cross-check panel 10 for
the actual request rate.

> **Open item (UAT 2026-07-29):** a live render on an idle stack showed `kestrel active` resting at
> exactly `0`, not the `0.5` the runbook states as its healthy value. The stated envelope still
> holds and a real climb (measured 2.5–5) clears either threshold, but treat "usual half-connection"
> in the runbook as unconfirmed.

---

## 6. What the dashboard is honestly good for

Ten panels were proven to discriminate — they moved when the fault they exist to reveal was driven,
and held still when it was not. **13 of 14 panels are flagged misleading-by-default**, meaning they
need the context in the runbook to read correctly; panel 1 is the only one that does not.

Use it as a **diagnostic surface once you already suspect a problem**, with the runbook open beside
it. Do not use it as an unattended health signal, and do not let anyone build alerting on the four
unproven panels.

---

## 7. The acknowledgement to obtain

Before the handover is complete, the receiver should be able to say back, unprompted:

- [ ] "A green `0` on panels 6, 7, 8 and 13 means no evidence, not healthy."
- [ ] "For real message loss I search the keeper logs for `REINJECT drop`, not panel 8."
- [ ] "An outage under 240 seconds (120 for gauges) may not show up at all."
- [ ] "Blank on panels 4, 5 and 9 is a finding, not a broken panel."
- [ ] "The dashboard is not currently reachable in a production sense — no ingress, no real authentication."

If they cannot, the conversation has not happened yet — regardless of what any sign-off record says.

---

**Status:** This briefing is *material for* the handover conversation. It is not evidence that the
conversation took place. Verdict condition 5 remains open until a named receiver acknowledges the
five points above. See `88-HUMAN-UAT.md` item 5.
