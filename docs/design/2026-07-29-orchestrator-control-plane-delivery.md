# Orchestrator Control-Plane Delivery & Failure Policy

**Status:** PROPOSED 2026-07-29 — design of record, **not yet implemented**. Nothing in §4–§7 is in `src/` today.
**Amended 2026-07-29 (A1):** the split failure policy is superseded by a **uniform closed-loop model** (§5). Three consequences: the Keeper recovery channel now serves all four operations, not just `Start`; the pause-state restart gap is closed by a **boot-time posture query** (§5.4) rather than L2 persistence; and the unbounded-accumulation risk is **downgraded** to a bounded in-flight burst, with hot-looping identified as the real residual (§9). Amendment log at §11.
**Scope:** the four control messages sent to the orchestrator tier — `StartOrchestration`, `StopOrchestration` (WebApi) and `PauseAll`, `ResumeAll` (Keeper). It does **not** change the data plane (`orchestrator-result*`, `keeper-recovery`, the processor queues), which already has the properties this design gives the control plane.

---

## 1. Problem

The control plane is a **broadcast**: every orchestrator replica holds its own in-memory L1 + Quartz state, and only the leader fires (`WorkflowFireJob` gates on `leaderState.IsLeader`). Followers hold identical inert schedules so failover is instant. That is why these four messages fan out rather than load-balance.

The delivery guarantees underneath that broadcast were weaker than believed. Five loss paths were identified:

| # | Path | Cause |
|---|---|---|
| 1 | Replica down at publish | no binding exists → no copy is made |
| 2 | Pod dies with an unacked message | exclusive queue deleted with the connection |
| 3 | Connection drop without pod death | same — exclusivity is connection-scoped |
| 4 | Broker restart | queues are non-durable |
| 5 | **Consumer throws** | **message is ACKed and parked to `{queue}_error`** |

Path 5 was the surprise, and it invalidated the assumption the other four were being reasoned about with.

---

## 2. Measured facts (not assumptions)

Live measurements against the Docker-Desktop k8s broker, not inference. Evidence trail at §10.

**F1 — A throwing consumer does NOT get redelivered.** Invoked exactly once; MassTransit 8.5.5's default error transport ACKs the original and moves the body to `{queue}_error`. The input queue settles at `messages=0, messages_unacknowledged=0, message_stats.redeliver=0`. Reproduced 4×, and independently on the runtime `ConnectReceiveEndpoint` construction path the processor uses — the two bind paths behave identically.

**F2 — ~28 doc-comments across 22 `src/` files are wrong.** They assert "throw → RabbitMQ nack-requeue, no `_error`", reasoning that the absence of `ConfigureError` implies requeue. That inference is backwards: `ConfigureError` customises *where* faults go, and its absence means the **default** error transport applies.

**F3 — The redelivery that has genuinely been observed is a different mechanism.** Phase 81's TEST-06 saw real at-least-once redelivery on `orchestrator-result` after a rabbitmq PVC crash. That is transport-level requeue of messages **unacked when the connection died** — it needs no configuration and always worked. It is not the throw path. The two were conflated, which is very likely how F2 became load-bearing.

**F4 — Current queue properties.**

```
Messaging.Contracts:{PauseAll,StartOrchestration,…}   fanout  durable=true   auto_delete=false
orchestrator-{pod} / -global-pauseresume-{pod}        queue   durable=false  exclusive=true  x-expires=60000
orchestrator-result / -result-post                    queue   durable=true   exclusive=false
```

The **type exchanges are already durable**. Everything ephemeral is produced by one flag: `e.Temporary = true` in `Orchestrator/Program.cs`.

**F5 — `consumer_timeout = 1800000` ms (30 min)** on RabbitMQ 4.1.8. An unacked message held longer causes the broker to force-close the channel; MassTransit reconnects and the message is requeued.

**F6 — `Start` cannot be published while L2 is down.** `OrchestrationService.StartAsync` touches Redis *before* it publishes — the first-win root probe, the tolerant pre-clean, processor-liveness validation, and `UpsertAsync`. A Redis fault at any of those is tagged and rethrown → HTTP 500 → **no message is ever published**. This bounds accumulation (§9).

---

## 3. Failure classes and the convergence axis

The operative distinction is **not** transient vs deterministic — it is whether a retry can **converge**.

| | Converging | Non-converging |
|---|---|---|
| Typical cause | Redis down, broker unreachable, connection reset | logic bug, malformed data, `ObjectAlreadyExists` — **or a permanently broken dependency** |
| Retry outcome | succeeds when the condition clears | identical failure, indefinitely |

"Transient" is a **prediction** that a failure will clear, not a property observable at failure time. A permanently defective Redis turns an infra fault — still classified `IsInfra` — into a non-converging one. The classification is structural (did it perform I/O?); the behaviour is temporal (will it clear?); they diverge exactly in that case.

Under the closed-loop rule (§5) non-convergence is **acceptable but not free**: the operation is preserved rather than lost, and the operator closes the loop. What that requires is capped backoff so a non-converging retry does not hot-loop, and an alert that actually reaches a human.

---

## 4. Topology

**Deployment:** the orchestrator becomes a **StatefulSet** at a fixed 3 replicas, giving stable identities `orchestrator-0/1/2`. Requires a headless Service (`serviceName`); the orchestrator has none today by design. No code change to queue naming — `Program.cs` already reads `POD_NAME` from the downward API and composes it through `OrchestratorFanoutEndpoints.PerInstance`.

**Queues:** drop `e.Temporary = true`. Each replica's endpoint exchange and queue become `durable=true, exclusive=false, auto_delete=false`, **no `x-expires`**.

**Publish path (unchanged):** Keeper/WebApi `Publish` → the durable fanout type exchange → one copy into each of the 3 durable queues. The copy is created and held whether the target replica is up, down, or restarting, because the binding and queue are independent of the consumer.

**Recovery path (new):** the Keeper `Send`s a targeted repair to `queue:orchestrator-{ordinal}`. `Send` addresses one endpoint by URI and never touches the type exchange, so targeting and broadcast coexist on the same bus with no topology change.

### 4.1 Addressing modes

The Keeper already uses both and chooses by the nature of the work. This design adds the third row.

| Work is… | Addressing | Example |
|---|---|---|
| replica-**agnostic** — any one replica can do it | `Send` → shared queue | `queue:orchestrator-result` (`OrchestratorReinjectConsumer`) |
| replica-**universal** — every replica must do it | `Publish` → fanout | `PauseAll` / `ResumeAll` (`BitHealthLoop`) |
| replica-**specific** — one particular replica must do it | `Send` → per-replica queue | **new:** the targeted repair |

Targeted, not fanned out: a fan-out retry would drag two healthy replicas through a redundant reload — a full L2 re-read plus a **cron phase reset**, since `ScheduleAsync` mints a fresh from-now trigger — and would triple L2 read load exactly when Redis has just recovered.

The Keeper stays topology-unaware: the **failing replica stamps its own `POD_NAME`** on the report, and the Keeper echoes that address back. This is why targeting requires StatefulSet — under a Deployment the identity is ephemeral, and a `Send` to a dead address would declare an orphan queue and fill it.

---

## 5. The closed-loop rule (A1 — supersedes the earlier split policy)

> **Never park a message as the result of a defect.** Ack the original, move the unfinished operation to a dedicated recovery channel, and retry there indefinitely with a counter. The **operator is part of the loop**: a non-converging retry escalates to a human, who fixes the defect, after which the retry converges.

### 5.1 Ack-and-hand-off, not hold

This is the detail that makes the rule workable, and it is already the data plane's model. `ProcessorPipeline` does **not** hold its message unacked while things are broken — infra outcomes emit a Keeper-state message and then **ack**. The unfinished work moves to a *parallel* channel (`keeper-recovery`) and the working queue keeps flowing.

Holding the original unacked would give the same no-loss guarantee **plus** permanent head-of-line blocking — and with `ConcurrentMessageLimit = 1`, one stuck message would deafen the endpoint forever.

**Non-ack survives only as a last resort:** when the recovery channel itself is unreachable, there is nowhere else to put the work.

### 5.2 All four operations take the same shape

| | Failure classes | Recovery channel | What the Keeper re-sends | Reconcilable from L2? |
|---|---|---|---|---|
| **Start** | infra (converging or not) + logic | Keeper | `ReconcileWorkflow(id)` | **yes** — root present ⇒ hydrate |
| **Stop** | logic only (no I/O) | Keeper | `ReconcileWorkflow(id)` | **yes** — root absent ⇒ unschedule |
| **PauseAll** | logic only (no I/O) | Keeper | **current posture** | no |
| **ResumeAll** | logic only (no I/O) | Keeper | **current posture** | no |

Which consumers can fail at all is asymmetric, and structurally so — Quartz runs on the default **RAMJobStore** (`AddQuartz()`, no persistent store), so only `Start` performs I/O:

| Consumer | Call chain | I/O |
|---|---|---|
| `Start` | `HydrateAndScheduleAsync` → Redis root GET + one GET per step | **yes** |
| `Stop` | `store.TryGet` → Quartz `DeleteJob` | none |
| `PauseAll` | Quartz `PauseAll()` | none |
| `ResumeAll` | `store.WorkflowIds` → Quartz state/unschedule/schedule → `ResumeAll()` | none |

### 5.3 What differs is only the repair message

**`Start` / `Stop` → `ReconcileWorkflow(workflowId)`.** Never a replay of the original: replaying `StartOrchestration` could resurrect a workflow Stopped while the retry sat queued. A reconcile re-reads L2 and is always current — root present ⇒ hydrate, root absent ⇒ unschedule. One message repairs both operations.

**`PauseAll` / `ResumeAll` → re-assert the current posture.** The Keeper is the *authority* on pause posture; it owns `prevHealthy`. A targeted "apply the current posture" is correct by construction, cannot go stale, and collapses both messages into one repair.

### 5.4 Boot-time posture query

Pause state exists only in each replica's RAMJobStore and is therefore lost on restart. Rather than persisting it in L2 — circular, since you would need to read L2 to learn you should be paused precisely when L2 is unavailable — **a replica asks the Keeper for the current posture on boot**.

This closes the restart gap with no new durable state and no dependency on the store that is down when you need it.

### 5.5 The policy

| Failure | Ack? | Action |
|---|---|---|
| Any of the four, recovery report **succeeds** | ACK | Keeper retries via the appropriate repair message, BIT-gated where L2 is implicated |
| Any of the four, recovery report **fails** | **NO ACK** | preserved in the durable queue; retried on the next connection cycle or the 30 min `consumer_timeout` (F5) |

Every failure increments a counter (§8) and emits an Error-level log carrying the operation, the workflowId where one exists, and the exception. The counter is **load-bearing** under this rule — it is the only mechanism that closes the loop to a human.

---

## 6. Coverage

| # | Loss path | Status |
|---|---|---|
| 1 | Replica down at publish | **Closed** — durable binding + queue holds the copy |
| 2 | Pod dies with unacked message | **Closed** — queue survives, same ordinal reattaches |
| 3 | Connection drop without pod death | **Closed** — same mechanism |
| 4 | Broker restart | **Closed if** messages publish persistent — §9 item 1 |
| 5 | Throw → `_error` park | **Closed** — nothing propagates, so nothing parks |
| 6 | Pause state lost on restart | **Closed** — boot-time posture query (§5.4) |

Two related defects were fixed in `src/` on the same day and are prerequisites rather than parts of this design:

- **Start teardown-first gap** (`87bdf42`) — the reload was destroy-then-rebuild, leaving L1 absent for `1+N` Redis round-trips and unrecoverable on a mid-hydration fault. Now build-then-commit.
- **Dead per-workflow `PauseWorkflow`/`ResumeWorkflow` pair removed** (`7db43d6`) — no publisher since Phase 48; dropped the third per-pod queue.

### A property that works in our favour

On restart, a queued `PauseAll` may be consumed before or after `HydrationBackgroundService` finishes scheduling. **Both orderings converge correctly**, because Quartz's `pausedTriggerGroups` persists — triggers added after a `PauseAll()` are born `Paused`. This is the GAP-49-2 behaviour already documented in `ResumeAllConsumer`. A backlog of pause/resume messages replays to the correct final state.

Additionally, `HydrationBackgroundService` calls `gate.MarkReady()` only after a *complete* successful hydration, so a replica restarting during an L2 outage schedules nothing at all and cannot fire — a second, independent guard.

---

## 7. Deliberate non-goals

Considered and **rejected**, with reasons, so they are not re-proposed:

**Persisting pause state in L2.** Circular — you would need to read L2 to learn you should be paused, precisely when L2 is unavailable. Superseded by the boot-time posture query (§5.4), which achieves the same result with no durable state.

**Fan-out retry.** Perturbs healthy replicas (cron phase reset, full L2 re-read) and triples L2 load during recovery. See §4.1.

**Send-3× instead of Publish.** Delivers identical per-queue guarantees — durability buys the redelivery, not the addressing — while making both publishers topology-aware, making the multi-send non-atomic, and having the publisher actively re-declare orphan queues. Keep `Publish` for the broadcast.

**A dedicated dead-letter tier.** The `skp-dlq-1` tier was deleted in quick task `260614-9jd` on the justification that endpoints "fall through to RabbitMQ nack-requeue — no error transport". That premise is F2, now disproven. It is nonetheless not re-introduced, because this design's answer is that nothing should reach the error transport at all.

**Swallowing the scheduler-only operations.** *Rejected by A1.* An earlier draft swallowed `Stop`/`PauseAll`/`ResumeAll` failures on the grounds that their failures are deterministic and a retry cannot converge. That optimised for *machine* convergence. The closed-loop rule puts the operator in the loop, so preserving the operation is both possible and correct.

---

## 8. Metrics

New counters must extend the existing surface, not sit beside it. The convention is already uniform across both services:

```
KeeperMetrics        MeterName "Keeper"        keeper_messages_consumed / keeper_messages_sent / keeper_l2_probe
OrchestratorMetrics  MeterName "Orchestrator"  orchestrator_messages_consumed / orchestrator_messages_sent / orchestrator_step_unresolved
ConsoleMetricTags    workflowId, processorId
```

Per-service meter with a `MeterName` const; `{service}_{noun}` snake_case; **no `_total` suffix** (the collector appends it); registered via `ConfigureOpenTelemetryMeterProvider(mp => mp.AddMeter(X.MeterName))`. `{service}_messages_consumed`/`_sent` already exist symmetrically on both meters — that is the precedent for a cross-service pair.

Proposed additions, as siblings:

| Meter | Counter | Increments when |
|---|---|---|
| Orchestrator | `orchestrator_control_failed` | a control consumer catches a fault and reports it |
| Keeper | `keeper_repair_sent` | a targeted repair is dispatched to a replica |
| Keeper | `keeper_repair_failed` | a repair could not be dispatched |

**Cardinality rules.** Tag by **operation** only — `start` / `stop` / `pauseAll` / `resumeAll`, a fixed set of four, needing one new const in `ConsoleMetricTags`. Do **not** tag with the exception type (unbounded); that belongs in the log. Do **not** add a replica tag without first verifying Phase 38's uniform `service_name` + instance labels — the identity likely already arrives as a resource attribute.

---

## 9. Open items

1. **Verify message persistence.** A durable queue holding non-persistent messages still loses everything on a broker restart. MassTransit publishes persistent by default, but loss path 4 depends on it and it has not been measured.
2. **Capped backoff — the real residual.** *(Revised by A1.)* Accumulation is **not** the failure mode. During an L2 outage `Start` is never published at all (F6), so only the in-flight burst from the seconds before the outage can queue; `PauseAll`/`ResumeAll` are edge-triggered and contribute a couple of messages per cycle. With L2 healthy the gate is open and repairs are consumed immediately, so depth stays shallow. What replaces unbounded depth is a **hot cycle** — report → repair → fail → report — on a non-converging failure. The mitigation is capped exponential backoff, **not** a queue-depth alarm, which would never fire.
3. **`ConcurrentMessageLimit = 1` on the lifecycle endpoint.** `Start` and `Stop` share `orchestrator-{ordinal}` with no limit, so a Stop and a newer Start can process concurrently and interleave. The pause/resume endpoints already have this.
4. **Unanticipated exceptions still park.** The policy catches expected failures. A null-ref or OOM still escapes to `{queue}_error` — **durable** under this design, where today it dies with the pod. Either make the catch a genuine catch-all or monitor `*_error` depth.
5. **Periodic reconciliation.** `HydrationBackgroundService` does `SMEMBERS` once at boot and returns; leader promotion does not re-hydrate. A low-frequency reconcile against the L2 parent index would converge divergence from **any** cause, and `ReconcileWorkflow` (§5.3) makes it nearly free.
6. **Correct the ~28 wrong doc-comments** (F2), enumerated and classified in `260729-kpl-FINDINGS.md`.
7. **Dead config knob.** `ProbeOptions.MaxAttempts` is read by nothing — `BitHealthLoop` uses only `DelaySeconds`. The Phase-48 teardown kept it believing `BitHealthLoop` read it: the same class of stale-belief error as F2. It is the natural home for the backoff bound in item 2.

---

## 10. Implementation scope

The blast radius is beyond a quick task; this ships as **GSD phases**. Beyond §4–§8, two additional work streams:

**Pipeline dashboard.** The Phase-87 business/pipeline dashboards must gain panels for the §8 counters, and those panels must satisfy the Phase-88 discrimination standard — a panel that cannot distinguish healthy from faulted is an accepted-unproven row, not coverage. Since `orchestrator_control_failed` is expected to rest at zero, it needs a guarded-zero treatment: prove it moves under an injected fault rather than asserting a flat line means health.

**The 7-scenario sweep.** `phase-80-harness.ps1` / `phase-81-sweep.ps1` currently cover TEST-01..07 on the data path. The control plane's new failure paths need their own scenarios — at minimum: a control-consumer fault repaired via the Keeper round-trip, a replica killed with an unacked control message and recovering it from its durable queue, and a boot-time posture query after a restart during a pause window. These are the live proof that the closed loop actually closes; the hermetic suite cannot demonstrate broker-literal behaviour (F1's whole lesson).

---

## 11. Amendment log

**A1 — 2026-07-29.** Uniform closed-loop model supersedes the split policy. Specifically: (a) all four operations use the Keeper recovery channel, superseding the earlier swallow-the-scheduler-ops policy — the earlier reasoning optimised for machine convergence and ignored the operator being in the loop; (b) the pause-state restart gap is closed by a boot-time posture query rather than L2 persistence; (c) the unbounded-accumulation open item is downgraded to a bounded in-flight burst on the strength of F6, with hot-looping identified as the real residual and capped backoff as its mitigation; (d) metrics section added, requiring the new counters to extend the existing per-service meters rather than sit beside them; (e) implementation scope added — GSD phases, dashboard panels, sweep scenarios.

---

## 12. Evidence trail

| Claim | Evidence |
|---|---|
| F1, F2 — throw parks, no redelivery | `tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs` (commits `2fecc00`, `458c699`); `.planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-FINDINGS.md` |
| F1 on the processor's bind path | same test, `ThrowingConsumer_OnRuntimeConnectedEndpoint_…` — FINDINGS §9 |
| F3 — connection-loss redelivery is real | Phase 81 TEST-06, MG-1 non-binding exception |
| F4 — queue/exchange properties | `rabbitmqctl list_queues/list_exchanges` on the live k8s broker, 2026-07-29 |
| F5 — `consumer_timeout` | `rabbitmqctl eval 'application:get_env(rabbit, consumer_timeout).'` → `{ok,1800000}` |
| F6 — Start blocked at source during L2 outage | `OrchestrationService.StartAsync` — Redis ops precede the publish; fault ⇒ 500 |
| Start teardown-first fix | `87bdf42`, `.planning/quick/260729-fg3-…` |
| Dead fan-out pair removal | `7db43d6`, `.planning/quick/260729-cfj-…` |
