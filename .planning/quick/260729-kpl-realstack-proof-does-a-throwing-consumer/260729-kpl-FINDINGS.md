# Findings — broker-literal consumer-fault disposition (quick-260729-kpl)

**Date:** 2026-07-29 · **Branch:** `feat/resilience-metric-binding` · **Stack:** Docker-Desktop k8s, host-mapped port-forwards

---

## 1. Question

On this project's console messaging configuration
(`src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:45-63` —
`AddMassTransit` → `UsingRabbitMq` → `Host` + four correlation filters + optional `configureBus` seam +
`ConfigureEndpoints(ctx)`, with **no** `UseMessageRetry`, **no** `ConfigureError`, **no** redelivery),
when a consumer's `Consume` throws, does **RabbitMQ redeliver the message to the same queue**
(nack-requeue), or does **MassTransit's default error transport move it to `<queue>_error` and ACK the
original**?

---

## 2. Method

- **Instrument:** `tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs`
  (`Category=E2E` + `Category=RealStack`).
- **Bus construction — the primary route WORKED.** The bus was built by the production
  `AddBaseConsoleMessaging` extension, **byte-unchanged**. The host-mapped broker port was reached
  through the unmodified production `c.Host(rabbitHost, h => …)` call site by supplying the
  **config value** `RabbitMq:Host = "rabbitmq://localhost:5673/"` (URI-host form — a documented
  overload behavior of `RabbitMqHostConfigurationExtensions.Host`). The `configureBus` host-override
  **fallback was NOT needed and was NOT used**. This is the stronger claim: the production path was
  exercised with zero source change and only two seam inputs (the consumer registration and the
  endpoint name).
- **Consumer:** `ProbeFaultConsumer` — deterministic, throws `InvalidOperationException` on **every**
  delivery after a 250 ms `Task.Delay(…, CancellationToken.None)` (the consume token is deliberately
  *not* threaded in: a cancellation-shaped exception is not the fault under measurement).
- **Counting:** a **static** `Interlocked` recorder, because MassTransit builds a fresh *scoped*
  consumer per delivery — an instance counter would always read 1 and would have silently fabricated
  the "invoked once" answer.
- **Endpoint:** one GUID-unique scratch endpoint `skp-probe-fault-{guid:N}`,
  `PrefetchCount=1`, `ConcurrentMessageLimit=1`. No production queue, exchange, binding, deployment or
  replica count was touched.
- **Stimulus:** exactly **one** message, `Send` (not `Publish`) to `queue:{scratch}`.
- **Observation:** RabbitMQ management API on `127.0.0.1:15673` (guest:guest), ~500 ms cadence,
  25 s cap, early break on an unambiguous answer, then a 2 s settle snapshot, then `host.StopAsync`,
  then a 6 s pause and a final post-stop snapshot.
- **Discriminator:** written **before** the first run, enumerating all four outcomes
  (`NackRequeueRedelivery`, `ErrorTransportPark`, `BoundedRetryThenPark`, `Inconclusive`);
  `PinnedDisposition` started at `Unpinned` so the test **could not pass** before a measurement
  existed. The discriminator was **not** edited after observing.

**Not controlled for / honest caveats:**

- The RabbitMQ management API refreshes queue statistics on a ~5 s interval, so `messages` counts lag
  reality by up to one interval. This is visible in the timeline (the `_error` queue shows
  `messages=0` for several ticks after it already exists) and is why a 6 s post-stop settle was added
  before the final ACK-evidence snapshot.
- The scratch endpoint is a normal durable endpoint. The Orchestrator's Start/Stop/PauseAll/ResumeAll
  endpoints are `Temporary = true` (exclusive/auto-delete). That variant was **not** separately
  measured — see §8.
- A single broker, a single MassTransit version, a single run shape (n=3 runs, identical).

---

## 3. Raw evidence

### RUN 1 — `Unpinned`, expected FAIL (`run1-observe.out`)

```
run id        : a52a8e5c-c5b0-4531-bfb3-6e9943e6b018
scratch queue : skp-probe-fault-a52a8e5cc5b04531bfb36e9943e6b018
bus built by  : BaseConsole.Core AddBaseConsoleMessaging (production, unmodified)
broker        : rabbitmq://localhost:5673/  (mgmt http://127.0.0.1:15673)
break reason  : error queue exists with messages >= 1 (the message has been parked)

label               |  elapsed |  inv | scratch.msgs | scratch.unack | scratch.redeliver | error? | error.msgs
--------------------+----------+------+--------------+---------------+-------------------+--------+-----------
pre-send            |    0.00s |    0 |            0 |             0 |                 0 |     no |          0
observe             |    0.51s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    1.03s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    1.56s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    2.09s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    2.62s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    3.15s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    3.68s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    4.22s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    4.76s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    5.31s |    1 |            0 |             0 |                 0 |    YES |          1
settle              |    7.33s |    1 |            0 |             0 |                 0 |    YES |          1
final(bus stopped)  |   13.46s |    1 |            0 |             0 |                 0 |    YES |          1

total invocations : 1
invocation offsets: 3.09s

COMPUTED DISPOSITION : ErrorTransportPark
PINNED DISPOSITION   : Unpinned
```

Test outcome: `failed … fault disposition changed: pinned=Unpinned, observed=ErrorTransportPark.`
— i.e. the instrument was RED-until-pinned exactly as designed.

The broker-side fault log line from the same run (independent corroboration that exactly **one**
delivery was attempted — one `R-FAULT` record, no repeats):

```
fail: MassTransit.ReceiveTransport[0]
      R-FAULT rabbitmq://localhost:5673/skp-probe-fault-a52a8e5cc5b04531bfb36e9943e6b018
      00ca0000-421d-19be-2f03-08deed6f0d95 BaseApi.Tests.Console.ProbeFaultMessage
      BaseApi.Tests.Console.ProbeFaultConsumer(00:00:00.2584588)
      System.InvalidOperationException: PROBE-FAULT a52a8e5cc5b04531bfb36e9943e6b018 - deliberate, measurement only
```

### RUN 3 — pinned, PASS, with the green evidence block (`run3-pinned-detailed.out`)

```
run id        : ffc993bd-edeb-4abb-9246-d833a5a856dd
scratch queue : skp-probe-fault-ffc993bdedeb4abb9246d833a5a856dd
break reason  : error queue exists with messages >= 1 (the message has been parked)

label               |  elapsed |  inv | scratch.msgs | scratch.unack | scratch.redeliver | error? | error.msgs
--------------------+----------+------+--------------+---------------+-------------------+--------+-----------
pre-send            |    0.00s |    0 |            0 |             0 |                 0 |     no |          0
observe             |    0.50s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    1.02s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    1.54s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    2.06s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    2.59s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    3.11s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    3.64s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    4.17s |    1 |            0 |             0 |                 0 |    YES |          0
observe             |    4.70s |    1 |            0 |             0 |                 0 |    YES |          1
settle              |    6.72s |    1 |            0 |             0 |                 0 |    YES |          1
final(bus stopped)  |   12.79s |    1 |            0 |             0 |                 0 |    YES |          1

total invocations : 1
invocation offsets: 2.99s

COMPUTED DISPOSITION : ErrorTransportPark
PINNED DISPOSITION   : ErrorTransportPark
```

### Run ledger

| Run | Pin state | Result | Run id |
|-----|-----------|--------|--------|
| 1 (`run1-observe.out`) | `Unpinned` | **FAIL** (by design) — evidence captured | `a52a8e5c-…` |
| 2 (`run2-pinned.out`) | `ErrorTransportPark` | **PASS** | `59ddd3fc-…` |
| 3 (`run3-pinned-detailed.out`) | `ErrorTransportPark` | **PASS** (`--output Detailed`, green evidence shown) | `ffc993bd-…` |

Three independent runs, three GUID-unique endpoints, identical shape every time.

### The ACK evidence, stated explicitly

| Fact | Value |
|------|-------|
| Consumer invocations | **1** (single offset; no second delivery in a 25 s window) |
| `{scratch}_error` before the send | **ABSENT** |
| `{scratch}_error` after the fault | **PRESENT**, `messages = 1` |
| `{scratch}` final `messages` | **0** |
| `{scratch}` final `messages_unacknowledged` | **0** |
| `{scratch}` `message_stats.redeliver` | **0** throughout |

`messages=0` **and** `messages_unacknowledged=0` on the input queue, while the body sits in `_error`,
is the ACK proof: the original delivery was neither left unacked (which would show as
`messages_unacknowledged ≥ 1`) nor requeued (which would show as `messages ≥ 1` and a rising
`redeliver` stat). It was **acknowledged and removed**.

### Broker cleanliness (independent of the test's own residue assertion)

```
probe queues remaining   (grep -c 'skp-probe-fault')  : 0
probe exchanges remaining(grep -c 'ProbeFaultMessage'): 0
diff queues-before.txt queues-after.txt               : IDENTICAL   (38 production queues, unchanged)
```

The production queue name-set captured before the first run is byte-identical to the set captured
after the last run. No production topology was created, mutated or destroyed.

---

## 4. Observed behavior

The consumer threw once and was **never called again**. RabbitMQ did **not** redeliver anything.
MassTransit acknowledged the original delivery, created a `{scratch}_error` queue on demand (it did
not exist before the fault — error-queue topology here is genuinely lazy, declared at first fault
rather than at endpoint start), and moved the message body into it with the exception details attached
as headers. The input queue was left completely empty — zero ready, zero unacknowledged — and its
`redeliver` statistic never moved off zero. A `Fault<ProbeFaultMessage>` was additionally published
into a fanout exchange with no bindings (so that copy was dropped, but the exchange persisted until
cleanup removed it).

In one sentence: **on this configuration, a thrown consumer exception parks the message in
`<queue>_error` and ACKs the original. There is no broker redelivery.**

---

## 5. MassTransit version + documented default

**Pinned version** — `Directory.Packages.props:137-138` (Central Package Management, re-confirmed, not
taken from the plan):

```
<PackageVersion Include="MassTransit" Version="8.5.5" />
<PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.5" />
```

**Documented default** — https://masstransit.io/documentation/concepts/exceptions (fetched
2026-07-29), on a consumer that simply throws:

> "When a message is delivered to the consumer and the consumer throws an exception, the following
> happens: **The message is moved to the `_error` queue (prefixed by the queue name).** The exception
> details are stored as headers with the message for analysis and to assist in troubleshooting the
> exception."

and, further down the same page:

> "**By default, MassTransit will move faulted messages to the `_error` queue.** This behavior can be
> customized for each Receive endpoint. To discard faulted messages so that they are not moved to the
> `_error` queue: `ec.DiscardFaultedMessages();`"

**Version-specific corroboration** (the docs site is now v9-branded, so the claim was independently
confirmed against the 8.5.5 package's *own* XML documentation,
`~/.nuget/packages/masstransit/8.5.5/lib/net8.0/MassTransit.xml`):

> `M:MassTransit.ReceivePipeConfigurationExtensions.DiscardFaultedMessages` —
> "Messages that fault should be discarded **instead of being moved to the `_error` queue**. Fault
> events will still be published."

The existence of an opt-*out* named `DiscardFaultedMessages`, documented in 8.5.5 itself as the way to
avoid `_error`, establishes that moving to `_error` is the 8.5.5 **default**.

**AGREES / DISAGREES:** **AGREES.** The documented default and the empirical result are the same. The
disagreement in this project is not between MassTransit's docs and MassTransit's behavior — it is
between MassTransit's behavior and 22 files of this repository's own doc-comments.

---

## 6. VERDICT

> **VERDICT: the ~15 "throw -> nack-requeue, no _error, no dead-letter" doc-comments in src/ are WRONG.**
>
> (Real count: **28** `nack-requeue` lines across **22** files, plus **22** further lines across **24**
> files asserting "no `_error`" / "no error transport" / "no dead-letter" — **28 distinct files** in the
> union.)

Those comments reason from an absence: "no `ConfigureError` is called, therefore there is no error
transport." That inference is backwards. `ConfigureError` **customizes where faults go**; its absence
means the **default** error transport applies, and the default is `<queue>_error`. The measurement
above shows the default firing on a queue built by the very extension method those endpoints use, with
the message ACKed and moved after a single attempt and `redeliver` never leaving zero. Independently,
a grep for actual call sites confirms there is **not one** `UseMessageRetry`, `ConfigureError`,
`DiscardFaultedMessages`, `UseDelayedRedelivery` or `UseScheduledRedelivery` invocation anywhere in
`src/` — so every console endpoint runs exactly the bare configuration that was measured, and the
finding generalizes across all of them.

**Impact on the stated requirement.** The user's requirement that **Start / Stop / PauseAll /
ResumeAll be redelivered by the broker when a consumer never acks** is **SILENTLY VIOLATED** on the
current configuration. All four are registered through `AddBaseConsoleMessaging`
(`src/Orchestrator/Program.cs:45`) with definitions that explicitly register nothing
(`StartOrchestrationConsumerDefinition`, `StopOrchestrationConsumerDefinition`,
`PauseAllConsumerDefinition`, `ResumeAllConsumerDefinition`). On the measured behavior, a throw out of
any of those consumers is ACKed and parked in `<endpoint>_error` — it is **not** redelivered, and
nothing retries it. "Silently" is the operative word: because error queues are created lazily, an
endpoint that has never faulted has no `_error` queue, so the live broker currently *looks* consistent
with the comments. It is not — it is merely untested. The one endpoint on the live stack that *has*
faulted, `processor-identity-query`, does carry a `processor-identity-query_error` queue.

**Corroborating evidence already inside the repo:** `src/Orchestrator/Dispatch/StepAdvancement.cs:44`
records, in past tense, that a null `NextStepIds` "previously NRE'd and **dead-lettered**". That is a
first-hand report of this exact default firing in production code — written by the same codebase that
elsewhere asserts it cannot happen.

---

## 7. Wrong-comment enumeration (the verdict is WRONG, so this section applies)

Produced from real greps run on 2026-07-29, not from memory:

```
grep -rn "nack-requeue" src/ --include=*.cs                                             # 28 hits / 22 files
grep -rni "no _error\|no dead-letter\|dead-letter\|error transport\|ConfigureError" \
     src/ --include=*.cs                                                                # +22 further hits / 24 files
```

**Classification key** — `FALSIFIED` = asserts a transport disposition (nack-requeue / broker
redelivery / no `_error` / no error transport) that the measurement contradicts. `STILL-TRUE` = an
accurate statement about the *code* (e.g. "no `ConfigureError` is called") or about the deleted
`skp-dlq-1` tier, which remains correct even though the inference drawn from it does not.
`NOT-A-CLAIM` = mentions the vocabulary without asserting the current disposition.

### 7a. Pass 1 — `nack-requeue` (28 hits / 22 files)

| file:line | claim (quoted, trimmed) | classification |
|---|---|---|
| `src/BaseConsole.Core/Resilience/RetryLoop.cs:6-7` | "send-exhaust → re-throw → broker nack-requeue (no `_error`, no dead-letter — the retired Phase-53 D-01 model)" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs:94` | "propagates → nack-requeue instead of being silently swallowed" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/BaseProcessor.cs:121` | "propagates through ProcessAsync → ProcessorPipeline (Plan 02) → nack-requeue" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/EntryStepDispatchConsumer.cs:23` ⚑ | "PROPAGATES (throws → RabbitMQ nack-requeue redelivery, no `_error`, Phase-53 D-01 …)" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs:17` | "out of the tail → RabbitMQ nack-requeue (broker redelivery)" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:46` ⚑ | "the dispatch endpoint the default is RabbitMQ nack-requeue (broker redelivery) — no dead-letter, no …" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:190` | "nack-requeue (broker redelivery re-fires the whole seed — the exact SendKeeper :304 escape …)" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/SpawnSendExhaustedException.cs:6` ⚑ | "MassTransit default RabbitMQ nack-requeue (broker redelivery — no dead-letter, no `_error`)" | **FALSIFIED** |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:53` | "send-exhaustion throws → broker nack-requeue redelivery, no `_error`" | **FALSIFIED** |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:273-274` | "with neither retry nor an error filter on this endpoint, the default is RabbitMQ nack-requeue (broker redelivery) — no `_error`, no dead-letter" | **FALSIFIED** (the inference-from-absence in its purest form) |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:281` | "the in-code OutputTail RetryLoop owns retries, send-exhaust throws → broker nack-requeue" | **FALSIFIED** |
| `src/Keeper/Recovery/DeleteConsumer.cs:15` | "re-throws on exhaustion to broker nack-requeue (D-04)" | **FALSIFIED** |
| `src/Keeper/Recovery/OrchestratorDeleteConsumer.cs:16` | "exhaustion to broker nack-requeue (D-04)" | **FALSIFIED** |
| `src/Keeper/Recovery/RecoveryConsumerBase.cs:18` | "`Consume` to RabbitMQ nack-requeue (broker redelivery) — no dead-letter, no skp-dlq-1" | **FALSIFIED** (the "no skp-dlq-1" sub-clause alone is STILL-TRUE) |
| `src/Keeper/Recovery/RecoveryConsumerBase.cs:54` | "the give-up falls out of `Consume` to broker nack-requeue" | **FALSIFIED** |
| `src/Keeper/Recovery/RecoveryEndpointBinder.cs:26` ⚑ | "through to RabbitMQ nack-requeue (broker redelivery) — no `_error`, no `skp-dlq-1` dead-letter" | **FALSIFIED** |
| `src/Keeper/Recovery/RecoveryEndpointBinder.cs:59` | "throw falls through to broker nack-requeue (no in-process retry, no dead-letter, no skp-dlq-1)" | **FALSIFIED** |
| `src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs:14` | "→ RabbitMQ nack-requeue (broker redelivery)" | **FALSIFIED** |
| `src/Orchestrator/Consumers/OrchestratorPostProcessConsumerDefinition.cs:11-12` ⚑ | "A send that exhausts the in-code RetryLoop throws → RabbitMQ nack-requeue (broker redelivery); there is no `_error` and no dead-letter (Phase-53 D-01)" | **FALSIFIED** |
| `src/Orchestrator/Consumers/PauseAllConsumerDefinition.cs:17` | "RetryLoop throws → RabbitMQ nack-requeue (broker redelivery); no `_error`, no dead-letter" | **FALSIFIED** — *governs the user's requirement* |
| `src/Orchestrator/Consumers/ResumeAllConsumerDefinition.cs:13` | "throws → RabbitMQ nack-requeue (broker redelivery), no `_error`, no dead-letter" | **FALSIFIED** — *governs the user's requirement* |
| `src/Orchestrator/Consumers/StartOrchestrationConsumerDefinition.cs:14` ⚑ | "RabbitMQ nack-requeue (broker redelivery); there is no `_error` and no dead-letter on this …" | **FALSIFIED** — *governs the user's requirement* |
| `src/Orchestrator/Consumers/StepCancelledConsumerDefinition.cs:12` | "a send that exhausts the in-code RetryLoop throws → RabbitMQ nack-requeue (broker redelivery)" | **FALSIFIED** |
| `src/Orchestrator/Consumers/StepCompletedConsumerDefinition.cs:14` | "RetryLoop throws → RabbitMQ nack-requeue (broker redelivery); there is no `_error` and no dead-letter" | **FALSIFIED** |
| `src/Orchestrator/Consumers/StepFailedConsumerDefinition.cs:12` | "a send that exhausts the in-code RetryLoop throws → RabbitMQ nack-requeue (broker redelivery)" | **FALSIFIED** |
| `src/Orchestrator/Consumers/StepProcessingConsumerDefinition.cs:12` | "a send that exhausts the in-code RetryLoop throws → RabbitMQ nack-requeue (broker redelivery)" | **FALSIFIED** |
| `src/Orchestrator/Consumers/StopOrchestrationConsumerDefinition.cs:15` | "RabbitMQ nack-requeue (broker redelivery); there is no `_error` and no dead-letter on this …" | **FALSIFIED** — *governs the user's requirement* |
| `src/Processor.Sample/SampleProcessor.cs:83` | "catch → RabbitMQ nack-requeue → the whole seed is redelivered + retried (no-loss recovery restored)" | **FALSIFIED** (note: the adjacent line 85 "generic catch → StepFailed+ack (NO redelivery)" is STILL-TRUE) |

⚑ = anchor explicitly named in the task brief. All seven brief anchors are accounted for; note two had
drifted by one line since the brief was written (`ProcessorPipeline.cs` 47→46,
`OrchestratorPostProcessConsumerDefinition.cs` 12→11).

### 7b. Pass 2 — widened claim shapes (22 further hits)

| file:line | claim (quoted, trimmed) | classification |
|---|---|---|
| `src/BaseProcessor.Core/Processing/OutputTail.cs:29` | "a send-exhaust THROWS (→ broker redelivery, no `_error`)" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/OutputTail.cs:149` | "redelivery (no `_error`)" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/OutputTail.cs:158` ⚑ | "propagate → throw → broker redelivery (no `_error`)" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/ProcessorPipeline.cs:323` | "send-exhaustion PROPAGATES (throw → broker redelivery, no `_error`)" | **FALSIFIED** |
| `src/BaseProcessor.Core/Processing/PostProcessConsumer.cs:13` | "(startup-bound, no bus retry, no error transport)" | **FALSIFIED** (a default error transport *is* active) |
| `src/BaseProcessor.Core/Startup/ProcessorStartupOrchestrator.cs:280` | "configure NOTHING but ConfigureConsumer (no UseMessageRetry, no ConfigureError; …" | **STILL-TRUE** (verified: zero call sites) |
| `src/Keeper/Program.cs:46` | "no error transport, so no exhaustion-policy choice" | **FALSIFIED** |
| `src/Keeper/Recovery/RecoveryConsumerBase.cs:17` | "NO bus retry / NO ConfigureError (symmetric with the exec path)" | **STILL-TRUE** (the code claim); the conclusion drawn at :18 is FALSIFIED |
| `src/Keeper/Recovery/RecoveryEndpointBinder.cs:28` | "A18 'no bus retry / no error transport on the execution path'" | **FALSIFIED** (the "no error transport" half) |
| `src/Keeper/Recovery/RecoveryEndpointBinder.cs:71` | "No bus retry / no `_error` here either." | **FALSIFIED** |
| `src/Keeper/RecoveryOptions.cs:6` | "symmetric with the exec path (no bus retry / no error transport, so no exhaustion-policy choice)" | **FALSIFIED** |
| `src/Orchestrator/Consumers/OrchestratorPostProcessConsumer.cs:11` | "(startup-bound, no bus retry / no error transport)" | **FALSIFIED** |
| `src/Orchestrator/Dispatch/RelocateTail.cs:70` | "DispatchAsync → broker redelivery (no `_error`)" | **FALSIFIED** |
| `src/Messaging.Contracts/Configuration/RetryOptions.cs:4` | "UseMessageRetry(Immediate(Limit)) — the dead-letter mechanism that fires on retry-budget exhaustion" | **NOT-A-CLAIM** (stale: describes a mechanism with zero remaining call sites; asserts nothing about the current disposition) |
| `src/Orchestrator/Dispatch/StepAdvancement.cs:44` | "the normal end-of-branch case that previously NRE'd and dead-lettered" | **NOT-A-CLAIM** — and in fact **corroborates** the measurement (past-tense first-hand report of the default firing) |
| `src/BaseProcessor.Core/DependencyInjection/BaseProcessorServiceCollectionExtensions.cs:127` | "Phase 32's final-attempt check cannot desync from UseMessageRetry). Absent section →" | **NOT-A-CLAIM** |
| `src/Orchestrator/Program.cs:70` | "StepCompletedConsumerDefinition owns the single endpoint-level UseMessageRetry, the other three definitions are intentional no-ops" | **NOT-A-CLAIM about disposition**, but **STALE/INACCURATE**: `StepCompletedConsumerDefinition` contains no `UseMessageRetry` call — its body is an explicit no-op. Worth including in the follow-up. |
| remaining widened hits in `RetryLoop.cs:7`, `ProcessorStartupOrchestrator.cs:274`, `OrchestratorPostProcessConsumerDefinition.cs:12`, `StepCancelledConsumerDefinition.cs:13`, `StepCompletedConsumerDefinition.cs:15`, `StepFailedConsumerDefinition.cs:13`, `StepProcessingConsumerDefinition.cs:13` | continuation lines of sentences already listed in §7a | **FALSIFIED** (same sentence) |

### 7c. Test-side anchor (outside `src/`, same claim)

| file:line | claim (quoted, trimmed) | classification |
|---|---|---|
| `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:20-31` | "the faulted consume must NOT be acked — it falls through to broker nack-requeue"; "a Guard-exhaust throw falls out of `Consume` to broker nack-requeue"; "Hermetic scope: in-memory proves the fault-and-no-ack SHAPE; **broker-literal nack-requeue defers to the RealStack close gate**" | **FALSIFIED**. Its own hedge named the missing proof — that proof now exists and contradicts it. This doc-comment should be rewritten to reference `ConsumerFaultDispositionE2ETests`. The `[Fact]` itself still passes: it asserts only that the *consume faults*, which remains true; it is the doc-comment's transport conclusion that is wrong. |
| `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:62`, `:88-89` | "falls through to broker nack-requeue; nothing routes to skp-dlq-1"; "no in-process retry, no error transport, and no dead-letter tier" | **FALSIFIED** (the "no error transport" / nack-requeue parts; "nothing routes to skp-dlq-1" is STILL-TRUE) |

### ⛔ DO NOT FIX HERE

**This task corrects nothing.** Zero `src/` files were modified (`git diff -- src/` is byte-identical
to its pre-task baseline, SHA `2b2c81ad…`; the only `src/` change in the tree is the pre-existing,
unrelated `ReinjectConsumer.cs` edit dated 2026-07-17). The table above is the **input to a follow-up
task**, which must decide, per site, whether to (a) correct the comment to describe the real
`_error`-park behavior, or (b) change the configuration so the comments become true (e.g. explicit
redelivery/retry policy, or `DiscardFaultedMessages` plus deliberate nack semantics) — that is an
architectural decision, not a comment edit, and it is out of scope here.

---

## 8. What this proof does NOT cover

Be precise; do not over-claim this result.

1. **It measures a `Consume` that THROWS.** It does not measure a consumer that hangs, that is
   cancelled, that returns without acking by some other means, or that faults during bus shutdown.
2. **It does not measure an endpoint with an explicit retry or error policy.** It measures the *bare*
   configuration. (Established separately by grep: no such policy exists anywhere in `src/` today —
   but if one is ever added, this result does not describe that endpoint.)
3. **It measures a normal durable endpoint.** The Orchestrator's Start/Stop/PauseAll/ResumeAll
   endpoints are `Temporary = true` (exclusive/auto-delete, per-pod names). The error transport is a
   receive-pipeline default and is not conditioned on queue durability, so the disposition is expected
   to be the same — but the `Temporary` variant was **not** separately measured, and the shape of its
   `_error` queue (durable vs auto-delete) is unknown from this run.
4. **It measures the console path** — `AddBaseConsoleMessaging` + `ConfigureEndpoints(ctx)`. It does
   **not** measure the WebApi's `AddBaseApiMessaging` explicit-`ReceiveEndpoint` path, which is where
   the pre-existing `processor-identity-query_error` queue lives. (That queue is consistent with the
   same default, but this run did not exercise that path.)
5. **It does not measure what happens to the parked message afterwards** — no assertion about `_error`
   queue TTL, retention, or whether anything ever drains it. (Nothing in `src/` consumes `_error`.)
6. **Single broker, single version, n=3.** RabbitMQ 4.1.8 management / MassTransit 8.5.5, on
   Docker-Desktop k8s. The committed test is the guard against drift on either.

---

## Artifacts

| File | Contents |
|---|---|
| `tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs` | the committed RealStack probe (pins `ErrorTransportPark`) |
| `run1-observe.out` | RUN 1, `Unpinned`, FAIL + evidence block |
| `run2-pinned.out` | RUN 2, pinned, PASS |
| `run3-pinned-detailed.out` | RUN 3, pinned, PASS, green evidence block |
| `queues-before.txt` / `queues-after.txt` | production queue name-sets, identical |
| `hermetic-task1.out` | hermetic regression run (766 total, 20 failed = pre-existing baseline) |
| `src-diff-baseline.sha256` | pre-task `git diff -- src/` hash, unchanged at task end |
