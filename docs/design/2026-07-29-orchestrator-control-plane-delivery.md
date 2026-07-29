# Orchestrator Control-Plane Delivery & Failure Policy

**Status:** PROPOSED 2026-07-29 — design of record, **not yet implemented**. Nothing in §4–§6 is in `src/` today.
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

These are live measurements against the Docker-Desktop k8s broker, not inference. See §9 for the evidence trail.

**F1 — A throwing consumer does NOT get redelivered.** It is invoked exactly once; MassTransit 8.5.5's default error transport ACKs the original and moves the body to `{queue}_error`. The input queue settles at `messages=0, messages_unacknowledged=0, message_stats.redeliver=0`. Reproduced 4×, and independently on the runtime `ConnectReceiveEndpoint` construction path the processor uses — the two bind paths behave identically.

**F2 — ~28 doc-comments across 22 `src/` files are wrong.** They assert "throw → RabbitMQ nack-requeue, no `_error`", reasoning that the absence of `ConfigureError` implies requeue. That inference is backwards: `ConfigureError` customises *where* faults go, and its absence means the **default** error transport applies.

**F3 — The redelivery that has genuinely been observed is a different mechanism.** Phase 81's TEST-06 saw real at-least-once redelivery on `orchestrator-result` after a rabbitmq PVC crash. That is transport-level requeue of messages that were **unacked when the connection died** — it needs no configuration and always worked. It is not the throw path. The two were conflated, which is very likely how F2 became load-bearing.

**F4 — Current queue properties.**

```
Messaging.Contracts:{PauseAll,StartOrchestration,…}   fanout  durable=true   auto_delete=false
orchestrator-{pod} / -global-pauseresume-{pod}        queue   durable=false  exclusive=true  x-expires=60000
orchestrator-result / -result-post                    queue   durable=true   exclusive=false
```

The **type exchanges are already durable**. Everything ephemeral is produced by one flag: `e.Temporary = true` in `Orchestrator/Program.cs`.

**F5 — `consumer_timeout = 1800000` ms (30 min)** on RabbitMQ 4.1.8. An unacked message held longer than this causes the broker to force-close the channel; MassTransit reconnects and the message is requeued.

---

## 3. The governing rule

> **Hold (don't-ack) only where the failure class is transient.**

Holding a message means keeping it so someone can try again. That is worth something only if trying again can produce a **different** outcome.

| | Transient (external state can change) | Deterministic (message + code only) |
|---|---|---|
| Examples | Redis down, broker unreachable, connection reset | null-ref, malformed data, logic bug, `ObjectAlreadyExists` |
| Retry converges? | yes, when the condition clears | **never** — identical input, identical failure |
| Cost of holding | a delivery slot for the outage | a delivery slot **forever** |
| With `ConcurrentMessageLimit = 1` | endpoint resumes after the outage | endpoint **blocked permanently** |

A non-converging retry is worse than a loss: it is the loss **plus** permanent head-of-line blockage of everything behind it. One poison `PauseAll` held forever would render a replica deaf to every subsequent pause and resume.

The codebase already draws this line — `WorkflowLifecycle.IsBusiness` / `IsInfra` — and it maps exactly onto which consumers perform I/O at all (§5).

---

## 4. Topology

**Deployment:** the orchestrator becomes a **StatefulSet** at a fixed 3 replicas, giving stable pod identities `orchestrator-0/1/2`. Requires a headless Service (`serviceName`); the orchestrator has none today by design. No code change to queue naming — `Program.cs` already reads `POD_NAME` from the downward API and composes it through `OrchestratorFanoutEndpoints.PerInstance`.

**Queues:** drop `e.Temporary = true`. Each replica's endpoint exchange and queue become `durable=true, exclusive=false, auto_delete=false`, **no `x-expires`**.

**Publish path (unchanged):** Keeper/WebApi `Publish` → the durable fanout type exchange → one copy into each of the 3 durable queues. The copy is created and held whether the target replica is up, down, or restarting, because the binding and queue are independent of the consumer.

**Recovery path (new):** the Keeper `Send`s a targeted repair to `queue:orchestrator-{ordinal}`. `Send` addresses one endpoint by URI and never touches the type exchange, so targeting and broadcast coexist on the same bus with no topology change.

### Addressing modes

The Keeper already uses both and chooses by the nature of the work. This design adds the third row.

| Work is… | Addressing | Example |
|---|---|---|
| replica-**agnostic** — any one replica can do it | `Send` → shared queue | `queue:orchestrator-result` (`OrchestratorReinjectConsumer`) |
| replica-**universal** — every replica must do it | `Publish` → fanout | `PauseAll` / `ResumeAll` (`BitHealthLoop`) |
| replica-**specific** — one particular replica must do it | `Send` → per-replica queue | **new:** the targeted repair |

Targeted, not fanned out, because a fan-out retry would drag two healthy replicas through a redundant reload — a full L2 re-read plus a **cron phase reset**, since `ScheduleAsync` mints a fresh from-now trigger — and would triple L2 read load exactly when Redis has just recovered.

The Keeper stays topology-unaware: the **failing replica stamps its own `POD_NAME`** on the failure report, and the Keeper echoes that address back. This is why the targeting requires StatefulSet — under a Deployment the identity is ephemeral, and a `Send` to a dead address would declare an orphan queue and fill it.

---

## 5. Failure policy

Which consumers can fail, and how, is asymmetric — and the asymmetry is structural, not probabilistic:

| Consumer | Full call chain | I/O |
|---|---|---|
| `Start` | `HydrateAndScheduleAsync` → Redis root GET + one GET per step | **yes** |
| `Stop` | `store.TryGet` (in-mem) → Quartz `DeleteJob` | none |
| `PauseAll` | Quartz `PauseAll()` | none |
| `ResumeAll` | `store.WorkflowIds` → Quartz state/unschedule/schedule → `ResumeAll()` | none |

Quartz runs on the default **RAMJobStore** (`Program.cs`, `AddQuartz()`, no persistent store configured). With no I/O there is no transient failure class, so retry cannot help the bottom three rows.

### The policy

| Failure | Ack? | Action |
|---|---|---|
| **`Start`**, L2 fault, Keeper report **succeeds** | ACK | Keeper BIT-gates and resends a targeted repair to `orchestrator-{ordinal}` |
| **`Start`**, L2 fault, Keeper report **fails** | **NO ACK** | preserved in the durable queue; retried on the next connection cycle or the 30 min `consumer_timeout` (F5) |
| **`Stop` / `PauseAll` / `ResumeAll`**, any fault | ACK | **swallow** + Error-level log with the workflowId + a counter |

The log and counter on the last row are **not optional**. They are the only signal that a replica's control path misfired, and they are what makes the swallow a calculated risk rather than an unmeasurable one.

### Notes on the policy

- **The report to the Keeper must itself be guarded.** Wrap it in the bounded `Guard(...)` retry the Keeper already uses for its own sends. Without that, the recovery path has a hole exactly where it is most likely to be exercised — the broker is often unhealthy at the same moment L2 is.
- **The two `Start` branches must not both fire.** No-ack is the *fallback* when reporting fails, not a parallel path; otherwise the Keeper's resend and the still-outstanding original both arrive. Idempotent, so harmless, but be deliberate.
- **Send a reconcile instruction, not the original message.** Replaying `StartOrchestration` risks resurrecting a workflow Stopped while the retry sat queued. A targeted `ReconcileWorkflow(workflowId)` re-reads L2 and is always current — absent root, no action. It also generalises to a periodic sweep (§8).

---

## 6. Coverage

| # | Loss path | Status |
|---|---|---|
| 1 | Replica down at publish | **Closed** — durable binding + queue holds the copy |
| 2 | Pod dies with unacked message | **Closed** — queue survives, same ordinal reattaches |
| 3 | Connection drop without pod death | **Closed** — same mechanism |
| 4 | Broker restart | **Closed if** messages publish persistent — see §8 |
| 5 | Throw → `_error` park | **Closed** — nothing propagates, so nothing parks |

Two related defects were fixed in `src/` earlier the same day and are prerequisites rather than parts of this design:

- **Start teardown-first gap** (`87bdf42`) — the reload was destroy-then-rebuild, leaving L1 absent for `1+N` Redis round-trips and unrecoverable on a mid-hydration fault. Now build-then-commit.
- **Dead per-workflow `PauseWorkflow`/`ResumeWorkflow` pair removed** (`7db43d6`) — no publisher since Phase 48; dropped the third per-pod queue.

### A property that works in our favour

On restart, a queued `PauseAll` may be consumed before or after `HydrationBackgroundService` finishes scheduling. **Both orderings converge correctly**, because Quartz's `pausedTriggerGroups` persists — triggers added after a `PauseAll()` are born `Paused`. This is the GAP-49-2 behaviour already documented in `ResumeAllConsumer`. A backlog of pause/resume messages therefore replays to the correct final state.

---

## 7. Deliberate non-goals

Considered and **rejected**, with reasons, so they are not re-proposed:

**Persisting pause state in L2.** Circular — you would need to read L2 to learn you should be paused, precisely when L2 is unavailable. It is also largely unnecessary: `HydrationBackgroundService` calls `gate.MarkReady()` only after a *complete* successful hydration, so a replica restarting during an L2 outage schedules nothing at all and cannot fire. `WorkflowFireJob` additionally gates on `startupGate.IsReady`. The residual is narrow — Redis readable-but-not-writable, where the Keeper's BIT probe (read + write-then-delete) declares unhealthy while hydration (read-only) succeeds.

**Keeper retry for `Stop`/`PauseAll`/`ResumeAll`.** The BIT gate's value is "wait until L2 is healthy, then retry", which is meaningless for operations that never touched L2. Their failures are deterministic; a retry loop around them cannot converge.

**Fan-out retry.** Perturbs healthy replicas (cron phase reset) and triples L2 load during recovery. See §4.

**Send-3× instead of Publish.** Delivers identical per-queue guarantees — durability is what buys the redelivery, not the addressing — while making both publishers topology-aware, making the multi-send non-atomic, and having the publisher actively re-declare orphan queues. Keep `Publish` for the broadcast.

**A dedicated dead-letter tier.** The `skp-dlq-1` tier was deleted in quick task `260614-9jd` on the justification that endpoints "fall through to RabbitMQ nack-requeue — no error transport". That premise is F2, now disproven. It is nonetheless not re-introduced here, because this design's answer is to stop anything reaching the error transport at all.

---

## 8. Open items

1. **Verify message persistence.** A durable queue holding non-persistent messages still loses everything on a broker restart. MassTransit publishes persistent by default, but path 4 depends on it and it has not been measured.
2. **`ConcurrentMessageLimit = 1` on the lifecycle endpoint.** `Start` and `Stop` share `orchestrator-{ordinal}` with no limit, so a Stop and a newer Start can process concurrently and interleave. The pause/resume endpoints already have this. On a control queue serialization is free.
3. **Unanticipated exceptions still park.** The policy catches the *expected* failures. A null-ref or OOM still escapes to `{queue}_error` — which under this design is **durable** and accumulates, where today it dies with the pod. Either make the catch a genuine catch-all or monitor `*_error` depth.
4. **Periodic reconciliation.** `HydrationBackgroundService` does `SMEMBERS` once at boot and returns; there is no reconcile loop, and leader promotion does not re-hydrate. A low-frequency reconcile against the L2 parent index would converge divergence from **any** cause — dropped broadcast, wedged consumer, swallowed failure, pod death — and the `ReconcileWorkflow` message from §5 makes it nearly free. This is the single highest-leverage follow-on.
5. **Unbounded queue growth.** No TTL is what makes recovery work; it also means nothing bounds a queue whose replica is down for a long period.
6. **Correct the ~28 wrong doc-comments** (F2). Enumerated and classified FALSIFIED / STILL-TRUE / NOT-A-CLAIM in `.planning/quick/260729-kpl-…/260729-kpl-FINDINGS.md`.

---

## 9. Evidence trail

| Claim | Evidence |
|---|---|
| F1, F2 — throw parks, no redelivery | `tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs` (commits `2fecc00`, `458c699`); findings in `.planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-FINDINGS.md` |
| F1 on the processor's bind path | same test, `ThrowingConsumer_OnRuntimeConnectedEndpoint_…` — FINDINGS §9 |
| F3 — connection-loss redelivery is real | Phase 81 TEST-06, MG-1 non-binding exception |
| F4 — queue/exchange properties | `rabbitmqctl list_queues/list_exchanges` on the live k8s broker, 2026-07-29 |
| F5 — `consumer_timeout` | `rabbitmqctl eval 'application:get_env(rabbit, consumer_timeout).'` → `{ok,1800000}` |
| Start teardown-first fix | `87bdf42`, `.planning/quick/260729-fg3-…` |
| Dead fan-out pair removal | `7db43d6`, `.planning/quick/260729-cfj-…` |
