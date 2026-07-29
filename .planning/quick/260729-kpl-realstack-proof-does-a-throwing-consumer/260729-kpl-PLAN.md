---
phase: quick-260729-kpl
plan: 01
type: execute
wave: 1
depends_on: []
autonomous: true
requirements: [QUICK-260729-kpl]
files_modified:
  # created (test — the measurement instrument)
  - tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs
  # created (deliverable 2/3 — the findings note)
  - .planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-FINDINGS.md
  # NO src/ file is modified by this plan — MEASUREMENT ONLY

must_haves:
  truths:
    - "ONE fact is settled empirically against the REAL broker: when a consumer wired through the REAL console bus path throws, the message is either (a) redelivered by RabbitMQ to the same queue, or (b) moved to <queue>_error by MassTransit's default error transport with the original acked — and which one it is, is recorded"
    - "The proof runs through src/BaseConsole.Core AddBaseConsoleMessaging UNMODIFIED — not a hand-rolled UsingRabbitMq — so it measures the configuration the consoles actually ship"
    - "The proof runs on a UNIQUELY-NAMED scratch endpoint; no production queue, exchange, binding, deployment or replica count is touched"
    - "The committed test PINS the observed reality and FAILS if MassTransit, the broker, or the bus configuration ever changes that behavior — and it was written so it would have been meaningful under EITHER outcome (the verdict constant was set AFTER observation, not before)"
    - "The broker is left byte-clean: after the run, no queue or exchange whose name starts with skp-probe-fault- or contains ProbeFaultMessage exists on vhost /"
    - "The pinned MassTransit version and its DOCUMENTED default on unhandled consumer exception are recorded alongside the empirical result; if they disagree, the empirical result is the verdict and the disagreement is itself recorded"
    - "A written VERDICT states yes-or-no whether the ~15 'throw -> nack-requeue, no _error' doc-comments in src/ match reality; if NO, every offending file:line is enumerated for a FOLLOW-UP task"
    - "ZERO production behavior changed: git diff of src/ is empty at the end of this plan"
    - "Release build of the whole solution succeeds with ZERO warnings (TreatWarningsAsErrors=true)"
    - "The new test carries Category=RealStack so it does NOT appear in the hermetic run; the hermetic failing-test NAME SET is unchanged"
  artifacts:
    - path: "tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs"
      provides: "The RealStack probe: real AddBaseConsoleMessaging bus + one always-throwing consumer on a GUID-named scratch endpoint, invocation-count + RabbitMQ-management-API observation over a bounded window, a computed FaultDisposition verdict pinned by assertion, and full scratch-topology cleanup"
      min_lines: 200
      contains: "AddBaseConsoleMessaging"
    - path: ".planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-FINDINGS.md"
      provides: "Observed behavior, raw evidence (invocation timeline + queue snapshots), MassTransit version + documented default, the VERDICT, and (if the verdict is NO) the enumerated wrong-comment file:line list"
      min_lines: 60
      contains: "VERDICT"
  key_links:
    - from: "tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs"
      to: "src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs"
      via: "the test builds its bus by calling the production AddBaseConsoleMessaging extension — the whole point of the proof"
      pattern: "AddBaseConsoleMessaging"
    - from: "tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs"
      to: "http://localhost:15673/api/queues/%2F"
      via: "RabbitMQ management API polling for <scratch> and <scratch>_error existence + message counts"
      pattern: "15673"
    - from: "tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs"
      to: "rabbitmq://localhost:5673/"
      via: "RabbitMq:Host config value in URI-host form — the host-mapped k8s port-forward"
      pattern: "5673"
---

<objective>
Settle ONE fact, decisively and repeatably, and record the answer.

**Question:** on this project's console messaging configuration
(`src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs:45-63` —
`AddMassTransit` -> `UsingRabbitMq` -> `Host` + four correlation filters + optional `configureBus`
seam + `ConfigureEndpoints(ctx)`, with NO `UseMessageRetry`, NO `ConfigureError`, NO redelivery),
when a consumer's `Consume` throws, does **RabbitMQ redeliver the message to the same queue**
(nack-requeue), or does **MassTransit's default error transport move it to `<queue>_error` and ACK
the original**?

**Why it matters:** 28 doc-comment hits across 22 files in `src/` assert the first answer
("throw -> RabbitMQ nack-requeue redelivery, no `_error`, no dead-letter"), and the user has stated
as a REQUIREMENT that Start/Stop/PauseAll/ResumeAll are redelivered by the broker when a consumer
never acks. Those comments reason from "no `ConfigureError` is called". That inference is suspect:
`ConfigureError` customizes WHERE faults go, so its ABSENCE means the DEFAULT error transport
applies. Counter-evidence already on the live broker: `processor-identity-query_error` EXISTS
(durable, 0 messages) for an endpoint configured with no error config at all, and error queues here
are created LAZILY (orchestrator fan-out endpoints have run for hours with none).

**This is a MEASUREMENT task.** Whatever the answer, this plan changes NO production behavior and
corrects NO comment. If the verdict is that the comments are wrong, the offending lines are
ENUMERATED for a follow-up task and left untouched.

Purpose: convert a 22-file architectural assertion from "reasoned from an absence" into "pinned by
a broker-literal test", and close the gap the project's own test admits at
`tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:29` ("broker-literal nack-requeue defers to
the live proof") — a live proof that does not currently exist.

Output:
1. `tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs` — the committed RealStack proof.
2. `260729-kpl-FINDINGS.md` — observed behavior, raw evidence, MassTransit version + documented
   default, VERDICT, and the wrong-comment enumeration if the verdict is NO.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/STATE.md

# The configuration under test — read it, do not re-derive it
@src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs

# The RealStack test pattern to model on (traits, host-mapped endpoints, teardown discipline)
@tests/BaseApi.Tests/Orchestrator/CorrelationPropagationE2ETests.cs

# The in-memory console-host composition pattern (AddInMemoryCollection config + AddBaseConsoleMessaging)
@tests/BaseApi.Tests/Console/ConsoleTestHostFixture.cs

# The hermetic test whose doc-comment admits this exact gap (line 29)
@tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs

<interfaces>
<!-- Verified during planning. Use these directly; do NOT go re-reading the codebase for them. -->

PRODUCTION BUS ENTRY POINT (src/BaseConsole.Core/DependencyInjection/MessagingServiceCollectionExtensions.cs):

    public static IServiceCollection AddBaseConsoleMessaging(
        this IServiceCollection services, IConfiguration cfg,
        Action<IBusRegistrationConfigurator> configureConsumers,
        Action<IBusRegistrationContext, IRabbitMqBusFactoryConfigurator>? configureBus = null)

  Body (verbatim behavior, do NOT modify):
    - reads cfg.Require("RabbitMq:Host") / ("RabbitMq:Username") / ("RabbitMq:Password")
      -- NOTE: there is NO "RabbitMq:Port" key on this path (unlike BaseApi.Core, which reads one).
    - AddSingleton<ICorrelationAccessor, AsyncLocalCorrelationAccessor>()
    - AddMassTransit(x => { configureConsumers(x); x.UsingRabbitMq((ctx, c) => {
          c.Host(rabbitHost, h => { h.Username(...); h.Password(...); });
          c.UseConsumeFilter(typeof(InboundCorrelationConsumeFilter<>), ctx);
          c.UseConsumeFilter(typeof(InboundExecutionScopeConsumeFilter<>), ctx);
          c.UseSendFilter(typeof(OutboundCorrelationSendFilter<>), ctx);
          c.UsePublishFilter(typeof(OutboundCorrelationPublishFilter<>), ctx);
          configureBus?.Invoke(ctx, c);
          c.ConfigureEndpoints(ctx);
      }); });

REACHING localhost:5673 THROUGH THAT UNMODIFIED PATH (verified from the MassTransit 8.5.5 package
XML docs, MassTransit.RabbitMqTransport.xml lines 527-534):

    M:MassTransit.RabbitMqHostConfigurationExtensions.Host(IRabbitMqBusFactoryConfigurator, System.String, Action{IRabbitMqHostConfigurator})
      <param name="host">The host name of the broker, OR A WELL-FORMED URI HOST ADDRESS</param>

  => setting config key "RabbitMq:Host" = "rabbitmq://localhost:5673/" reaches the host-mapped
     port THROUGH the production call site with ZERO source change. This is a config VALUE, exactly
     like the k8s manifests set RabbitMq__Host=rabbitmq. THIS IS THE PRIMARY ROUTE.

MASSTRANSIT VERSION (Directory.Packages.props:137-138, Central Package Management):
    <PackageVersion Include="MassTransit" Version="8.5.5" />
    <PackageVersion Include="MassTransit.RabbitMQ" Version="8.5.5" />
  (8.5.5 is the last Apache-2.0 line per the props comment.)

CONSUME-FILTER PASS-THROUGH (verified): InboundExecutionScopeConsumeFilter&lt;T&gt; short-circuits to
`next.Send(context)` unless the message implements IExecutionCorrelated. A plain probe record is
therefore untouched by the filters — no contract interface is required on the probe message.

TEST FRAMEWORK: xUnit v3 on Microsoft.Testing.Platform. Use `TestContext.Current.CancellationToken`
on every awaited call that accepts one (xUnit1051 is an ERROR under TreatWarningsAsErrors).
`IAsyncLifetime` members return `ValueTask`.

LIVE ENDPOINTS (k8s port-forwards, already up — do NOT bring anything up):
    RabbitMQ AMQP        localhost:5673
    RabbitMQ management  localhost:15673   (basic auth guest:guest)
  Management API URL encoding: vhost "/" => %2F ; a ':' inside an exchange name => %3A
  (Uri.EscapeDataString produces both correctly.)
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Build the measurement instrument — the RealStack fault-disposition probe (deliberately RED until Task 2 pins it)</name>
  <files>tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs</files>
  <action>
Create ONE new test file. Touch NOTHING in `src/`. Touch no other test file.

**File:** `tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs`,
namespace `BaseApi.Tests.Console`.

Contents, in this order:

**(a) The probe verdict enum** — public (or internal) enum `FaultDisposition` with members:
`Unpinned`, `NackRequeueRedelivery`, `ErrorTransportPark`, `BoundedRetryThenPark`, `Inconclusive`.
`Unpinned` is the DELIBERATE initial state: the test cannot pass until a human has observed reality
and pinned it (Task 2). Document that in an XML comment — it is what makes this proof honest rather
than a presupposition dressed as a test.

**(b) The probe message** — `public sealed record ProbeFaultMessage(Guid RunId);` in this namespace.
Its MassTransit message-type exchange will be named `BaseApi.Tests.Console:ProbeFaultMessage`
(namespace:TypeName). Note that in a comment — the cleanup in (f) targets it by substring.

**(c) A static recorder** — `ProbeFaultRecorder` with `Interlocked`-incremented `Invocations`, a
`ConcurrentQueue<TimeSpan>` of offsets-since-`Reset()` (use a `Stopwatch` started at `Reset()`), and
a `Reset()` that clears both. Static because MassTransit builds a fresh scoped consumer per delivery,
so an instance counter would always read 1 and silently fake the "invoked once" answer.

**(d) The always-throwing consumer** — `ProbeFaultConsumer : IConsumer<ProbeFaultMessage>`:
  - record the invocation (count + offset),
  - `await Task.Delay(250, CancellationToken.None);`
    **LOAD-BEARING:** pass `CancellationToken.None`, NEVER `context.CancellationToken`. A
    cancellation-shaped exception on bus stop is NOT the fault under measurement and MassTransit
    treats it differently — threading the consume token in would corrupt the result. The 250 ms is
    observation hygiene: if the answer turns out to be nack-requeue, it bounds an otherwise
    unbounded hot redelivery loop against the LIVE broker to ~4 deliveries/second.
  - then `throw new InvalidOperationException($"PROBE-FAULT {context.Message.RunId:N} — deliberate, measurement only");`
    Deterministic every time: no counter-conditional throw, no first-time-only. The question is what
    the transport does with a fault, so every delivery must fault identically.

**(e) The test** — one `[Fact]` on a sealed class carrying BOTH traits, matching the rest of the
suite: `[Trait("Category", "E2E")] [Trait("Category", "RealStack")]`. Do NOT put it in the
`Observability` or `RedisOutageSerial` collection: it touches neither Redis nor env vars, it builds
its own in-memory config, and its endpoint name is GUID-unique, so it cannot race anything. Accept
`ITestOutputHelper` by constructor injection and write the evidence table to it unconditionally
(pass or fail), so a green run still shows its evidence.

  Test body:

  1. `var runId = Guid.NewGuid();` and
     `var scratch = $"skp-probe-fault-{runId:N}";`
     The `skp-probe-fault-` prefix matches no production queue and makes stray residue from an
     aborted run greppable and self-healing (see cleanup).
  2. `ProbeFaultRecorder.Reset();`
  3. Build the host with `Host.CreateApplicationBuilder()` +
     `builder.Configuration.AddInMemoryCollection(...)` carrying exactly:
        `["RabbitMq:Host"]     = "rabbitmq://localhost:5673/"`   (URI-host form — see &lt;interfaces&gt;)
        `["RabbitMq:Username"] = "guest"`
        `["RabbitMq:Password"] = "guest"`
     then the ONE production call:
        `builder.Services.AddBaseConsoleMessaging(builder.Configuration, x =>
             x.AddConsumer<ProbeFaultConsumer>().Endpoint(e => { e.Name = scratch; }));`
     Do NOT pass the `configureBus` seam. Do NOT call `UsingRabbitMq` yourself. Do NOT call
     `AddBaseConsole` / `AddBaseConsoleObservability` (not needed; avoids the embedded health
     listener port and an OTLP exporter). This single call IS the thing under test.
     OPTIONAL hygiene, only if the members exist on the endpoint configurator in 8.5.5:
     `e.PrefetchCount = 1; e.ConcurrentMessageLimit = 1;`. If either does not compile, DROP it — the
     250 ms in-consumer delay already bounds the rate — and note the drop in a comment.
  4. `await host.StartAsync(ct);` then wait (poll the management API, up to 20 s) for queue
     `scratch` to APPEAR on vhost `/`. Its appearance is the proof the bus reached the intended
     broker on the intended port. If it never appears, `Assert.Fail` with an explicit message naming
     the URI-host form as the suspect, and STOP — do not silently measure nothing.
     **Also snapshot at this moment** whether `{scratch}_error` exists (it MUST NOT yet). This
     "before" snapshot is what makes its later appearance meaningful — MassTransit creates error
     queues lazily here (verified: the orchestrator fan-out endpoints have run for hours with none),
     but the test must not depend on that being true; it must SHOW it.
  5. Send exactly ONE message, straight to the scratch endpoint (not `Publish` — a Send to
     `queue:{scratch}` targets that endpoint's own exchange and cannot fan out to a second
     concurrent run):
        `var bus = host.Services.GetRequiredService<IBus>();`
        `var ep = await bus.GetSendEndpoint(new Uri($"queue:{scratch}"));`
        `await ep.Send(new ProbeFaultMessage(runId), ct);`
  6. **Observation loop**, ~500 ms cadence, hard cap ~25 s. Each tick records a snapshot row:
        elapsed | ProbeFaultRecorder.Invocations
              | scratch: messages, messages_unacknowledged, message_stats.redeliver (if present)
              | {scratch}_error: exists?, messages
     Break EARLY the moment the answer is unambiguous:
        - `Invocations >= 5`  (redelivery is happening — no need to keep hammering the live broker), or
        - `{scratch}_error` exists AND its `messages >= 1` (the message has been parked).
     After breaking, wait ~2 s and take ONE final settle snapshot (so a "N then park" sequence is
     captured whole rather than truncated at the break).
  7. `await host.StopAsync(ct);` then take the FINAL snapshot after the bus is down.
  8. **Compute the verdict from the final + timeline snapshots** — data-driven, not hardcoded:
        - `Invocations == 1` AND error queue appeared with `messages >= 1` AND scratch drained to
          `messages == 0 && messages_unacknowledged == 0`  => `ErrorTransportPark`
          (the original was ACKed and the body moved — the comments would be WRONG)
        - `Invocations >= 5` AND the error queue never appeared              => `NackRequeueRedelivery`
          (the comments would be RIGHT)
        - `Invocations > 1` AND the error queue eventually appeared with `messages >= 1`
                                                                          => `BoundedRetryThenPark`
          (a bounded retry somewhere, then a park — record the exact N)
        - anything else                                                     => `Inconclusive`
  9. Build a human-readable EVIDENCE string: the run id, the scratch name, the full snapshot table,
     the invocation offsets, and the computed verdict. `outputHelper.WriteLine(evidence)`.
 10. **Assert** against a pinned expectation held in a
     `private static readonly FaultDisposition PinnedDisposition = FaultDisposition.Unpinned;`
     (`static readonly`, NOT `const` — a `const` enum comparison can fold and trip an
     unreachable-code / always-false warning, which is a BUILD FAILURE under
     `TreatWarningsAsErrors=true`).
        `Assert.True(observed == PinnedDisposition,
             $"fault disposition changed: pinned={PinnedDisposition}, observed={observed}.\n{evidence}");`
     A doc-comment above `PinnedDisposition` must state: this value is set from OBSERVED reality
     (Task 2), never from expectation; if this assertion ever fails, the transport's fault handling
     changed and every "nack-requeue" doc-comment in `src/` must be re-audited.

**(f) Cleanup — in a `finally`, unconditional, best-effort per item.** Via the management API on
`http://localhost:15673` (basic auth `guest:guest`; build the URL with
`Uri.EscapeDataString`, vhost `/` => `%2F`, exchange-name `:` => `%3A`). Treat 204 AND 404 as
success. Delete:
   - queues:    `{scratch}`, `{scratch}_error`, `{scratch}_skipped`
   - exchanges: `{scratch}`, `{scratch}_error`, `{scratch}_skipped`
   - EVERY exchange whose name contains `ProbeFaultMessage` — this catches both the message-type
     exchange `BaseApi.Tests.Console:ProbeFaultMessage` and the fault exchange
     `MassTransit.Fault[[BaseApi.Tests.Console:ProbeFaultMessage, ...]]` that a faulted consume
     publishes into (fanout, no bindings, so its message is dropped — but the exchange persists).
   - EVERY queue whose name starts with `skp-probe-fault-` — the self-healing sweep that reclaims
     residue from any earlier aborted run.
   Do NOT delete the bus's own temporary endpoint queue (`bus-...`) — it is auto-delete and vanishes
   on stop. Do NOT delete anything else, ever: no name-set diffing, no "delete what appeared during
   the run" heuristic. The live stack creates and destroys queues on its own; only explicitly-named
   probe topology may be removed.

**(g) Residue assertion** — after cleanup, re-list queues and exchanges and assert that NOTHING
matching `skp-probe-fault-` or `ProbeFaultMessage` remains. This makes "the broker was left clean"
a checked fact rather than an intention.

Keep the management-API access to a small private helper inside this file (an `HttpClient` with the
basic-auth header + two `GET`s + one `DELETE`). Do NOT add a shared test helper, a new fixture, a
new package reference, or a new project reference.
  </action>
  <verify>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; dotnet build SK_P.sln -c Release 2>&amp;1 | tail -5   # must report 0 Warning(s), 0 Error(s)</automated>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; git diff --stat -- src/   # MUST be EMPTY — measurement only</automated>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; ./tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack 2>&amp;1 | tail -20   # ConsumerFaultDisposition must NOT appear; failing-test NAME SET unchanged vs the ~20 pre-existing baseline</automated>
  </verify>
  <done>
`ConsumerFaultDispositionE2ETests.cs` exists and compiles; Release build is 0-warning;
`git diff -- src/` is empty; the hermetic run does NOT execute the new test (RealStack trait) and its
failing-test NAME SET is identical to the pre-existing baseline (compare NAME SETS, never counts —
~20 known baseline failures). The test is deliberately RED-until-pinned: `PinnedDisposition` is
`Unpinned`, so a RealStack invocation right now would fail and print the evidence. That is the
intended state at the end of this task.
  </done>
</task>

<task type="auto">
  <name>Task 2: Observe against the live broker, pin the verdict, prove it repeatable</name>
  <files>tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs</files>
  <action>
**Precondition check first (do not assume):** confirm the live stack is reachable without changing
it — `curl -s -u guest:guest http://localhost:15673/api/overview` returns JSON, and
`curl -s -u guest:guest http://localhost:15673/api/queues/%2F` lists the production queues. If the
management port is not answering, STOP and report; do NOT `kubectl apply -k k8s/` (it reverts
running images to a stale `:local` tag), do NOT bring up docker-compose (this project is k8s-only),
do NOT scale or restart anything.

Also capture, for the FINDINGS note, a BEFORE listing of production queues so the after-listing can
be shown identical: save `curl -s -u guest:guest http://localhost:15673/api/queues/%2F` filtered to
names only.

**RUN 1 — observe.** Invoke the built test binary directly (NEVER `dotnet test` — it hangs on
Windows MTP), filtered to this one test:

    ./tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-class "*ConsumerFaultDispositionE2ETests*"

(If `--filter-class` is not accepted by this runner build, fall back to
`--filter-method "*FaultDisposition*"`, or run with `--filter-trait Category=RealStack` and read this
test's output out of the run.)

This run is EXPECTED TO FAIL — `PinnedDisposition` is still `Unpinned`. That failure message carries
the EVIDENCE block. **Capture the full evidence block verbatim** (invocation count, invocation
offsets, the whole snapshot table, the computed disposition). Redirect the run output to
`.planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/run1-observe.out` so the raw
evidence survives for the findings note.

**Failure-mode handling (record whichever applies, do not paper over it):**
  - *The scratch queue never appeared* => the URI-host form did not take. FALLBACK, and only then:
    pass the `configureBus` seam and re-invoke the host inside it —
    `AddBaseConsoleMessaging(cfg, consumers, (ctx, c) => c.Host("localhost", 5673, "/", h => { h.Username("guest"); h.Password("guest"); }))`.
    This overrides ONLY the broker address; every filter, `ConfigureEndpoints`, and the
    absence of retry/error configuration — i.e. everything that determines the answer — is
    untouched. If MassTransit rejects a second `Host` call, report and stop. Record in the findings
    note WHICH route was used, because the primary route is the stronger claim ("the production path
    byte-unchanged").
  - *The computed disposition is `Inconclusive`* => do NOT pin `Inconclusive`. Report the raw
    snapshots and stop for a human decision. An inconclusive measurement is a finding, not a pin.

**PIN.** Edit ONE line — set `PinnedDisposition` to the OBSERVED value. Update its doc-comment to
record: the value observed, the date, the run id, and the one-line evidence that decided it
(e.g. "invoked N times in Xs; {scratch}_error absent throughout" or "invoked once; {scratch}_error
appeared with messages=1 while the scratch queue drained to 0/0"). Change NOTHING else in the file —
not the discriminator logic, not the thresholds. Rewriting the discriminator to match the outcome
would destroy the property that makes this proof worth anything.

**RUN 2 — confirm green + repeatable + residue-free.** Rebuild Release (0 warnings) and re-run the
same filtered invocation; redirect to `run2-pinned.out`. It must PASS. Passing on a SECOND run also
proves Run 1's cleanup left no residue (a leftover `{scratch}_error` from run 1 cannot influence run
2 — the names are GUID-unique — but a leftover message-type or fault exchange would, and the
self-healing sweep plus the residue assertion cover it).

**Broker cleanliness check (independent of the test's own assertion).** After Run 2:
    curl -s -u guest:guest http://localhost:15673/api/queues/%2F    | grep -c "skp-probe-fault"   # expect 0
    curl -s -u guest:guest http://localhost:15673/api/exchanges/%2F | grep -c "ProbeFaultMessage" # expect 0
and diff the production queue-name listing against the BEFORE listing captured at the start — it
must be identical (modulo the stack's own transient/auto-delete queues; call out any difference
explicitly rather than waving it through).

**CROSS-CHECK (cheap, same task) — the documented default.**
  - Version: `grep -rn "MassTransit" Directory.Packages.props` — record the exact pinned version
    (planning already read 8.5.5 for both `MassTransit` and `MassTransit.RabbitMQ`; re-confirm it
    rather than trusting this plan).
  - Documented behavior: fetch the MassTransit exception-handling documentation
    (https://masstransit.io/documentation/concepts/exceptions) and record, in one or two quoted
    lines, what it says happens by DEFAULT when a consumer throws and no retry/error policy is
    configured. If the fetch fails, say so and fall back to the package's own XML docs
    (`~/.nuget/packages/masstransit/8.5.5/lib/net8.0/MassTransit.xml`) — searching for the error
    transport / `_error` queue members — and mark the source as "package XML, docs site unreachable".
  - If documented and empirical DISAGREE: the EMPIRICAL result is the verdict, and the disagreement
    is itself recorded as a finding.

No `src/` file may be touched in this task. No comment anywhere may be corrected in this task.
  </action>
  <verify>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; dotnet build SK_P.sln -c Release 2>&amp;1 | tail -5   # 0 Warning(s), 0 Error(s)</automated>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; ./tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-class "*ConsumerFaultDispositionE2ETests*" 2>&amp;1 | tail -30   # must PASS after the pin</automated>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; curl -s -u guest:guest http://localhost:15673/api/queues/%2F | grep -c "skp-probe-fault" ; curl -s -u guest:guest http://localhost:15673/api/exchanges/%2F | grep -c "ProbeFaultMessage"   # both 0 (grep -c prints 0 and exits 1 — read the number, not the exit code)</automated>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; git diff --stat -- src/   # still EMPTY</automated>
  </verify>
  <done>
`PinnedDisposition` holds the OBSERVED disposition with a dated evidence comment; the filtered
RealStack run PASSES twice in a row; the broker carries zero `skp-probe-fault-*` queues and zero
`*ProbeFaultMessage*` exchanges; the production queue listing matches the before-listing; Release is
0-warning; `git diff -- src/` is still empty; `run1-observe.out` and `run2-pinned.out` hold the raw
evidence; the pinned MassTransit version and the documented default are captured for the note.
  </done>
</task>

<task type="auto">
  <name>Task 3: Write the findings note with the VERDICT, enumerate the wrong comments if any, commit by explicit path</name>
  <files>.planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-FINDINGS.md</files>
  <action>
**Part A — write `260729-kpl-FINDINGS.md`** in the task directory. Sections, in this order:

  1. **Question** — the one sentence being settled.
  2. **Method** — the bus was built through the production `AddBaseConsoleMessaging` (state whether
     the primary URI-host route or the `configureBus` host-override fallback was used, and if the
     fallback, exactly what differed); one always-throwing consumer; one GUID-named scratch
     endpoint; one message sent; bounded observation window; management-API polling on 15673.
     State plainly what was NOT controlled for, if anything.
  3. **Raw evidence** — the invocation count and offsets, and the snapshot table verbatim from
     `run1-observe.out`; plus the `{scratch}_error` before/after existence and message counts, and
     the scratch queue's final `messages` / `messages_unacknowledged` (that pair is the ACK
     evidence). Include the run id and the scratch queue name so the run is identifiable.
  4. **Observed behavior** — one paragraph in plain language: what the broker and MassTransit
     actually did.
  5. **MassTransit version + documented default** — the exact pinned version from
     `Directory.Packages.props`, the quoted documented default, the source of that quote, and an
     explicit AGREES / DISAGREES line against the empirical result.
  6. **VERDICT** — a single unambiguous line, in this exact shape:
        `VERDICT: the ~15 "throw -> nack-requeue, no _error, no dead-letter" doc-comments in src/ are {CORRECT|WRONG}.`
     followed by 2-4 sentences of why, and an explicit statement of what it means for the user's
     stated requirement that Start/Stop/PauseAll/ResumeAll be redelivered by the broker when a
     consumer never acks: is that requirement MET or SILENTLY VIOLATED on the current configuration?
  7. **If the verdict is WRONG — the wrong-comment enumeration.** Produce it from a real grep, not
     from memory:
        `grep -rn "nack-requeue" src/ --include=*.cs`
     (28 hits across 22 files at planning time; re-run it — do not trust that count). Widen with a
     second pass for adjacent claim shapes that the first pattern misses:
        `grep -rni "no _error\|no dead-letter\|dead-letter\|error transport\|ConfigureError" src/ --include=*.cs`
     Render a table: `file:line | claim (quoted, trimmed) | FALSIFIED / STILL-TRUE / NOT-A-CLAIM`.
     Classify each hit honestly — some mentions are descriptive of `RetryLoop` semantics or of the
     deleted `skp-dlq-1` tier and may remain true even if the transport disposition claim is false.
     Only rows marked FALSIFIED belong to the follow-up. Explicitly name the anchors from the task
     brief and mark each: `EntryStepDispatchConsumer.cs:23`, `OutputTail.cs:158`,
     `ProcessorPipeline.cs:47`, `SpawnSendExhaustedException.cs:6`, `RecoveryEndpointBinder.cs:26`,
     `StartOrchestrationConsumerDefinition.cs`, `OrchestratorPostProcessConsumerDefinition.cs:12`,
     plus `tests/BaseApi.Tests/Keeper/RecoveryDeadLetterFacts.cs:20-31` (whose doc-comment makes the
     same claim AND names this very proof as the thing that would settle it — it should now
     reference this test).
     End the section with an explicit **DO NOT FIX HERE** line: this task corrects nothing; the
     table is the input to a follow-up task.
  8. **What this proof does NOT cover** — be precise about the boundary, so the next reader does not
     over-claim it. At minimum: it measures a `Consume` that THROWS; it does not measure a consumer
     that hangs, that is cancelled, that faults during bus shutdown, or an endpoint configured with
     an explicit retry/error policy; and it measures the console path
     (`AddBaseConsoleMessaging` + `ConfigureEndpoints`), not the WebApi's `AddBaseApiMessaging`
     explicit-`ReceiveEndpoint` path (which is where `processor-identity-query_error` lives).

**Part B — regression gate.** Re-run the hermetic suite and compare failing-test NAME SETS against
the pre-existing baseline (~20 known failures — see [[hermetic-preexisting-failures]]):
    ./tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack
Compare NAME SETS, never counts. The new test must NOT appear (it carries `Category=RealStack`). If
a NEW failing name appears, STOP and report — do not adjust a test to make it green.

**Part C — commit, by EXPLICIT PATH ONLY.** The working tree carries ~110 untracked `*.out` files
and unrelated modifications (`src/Keeper/Recovery/ReinjectConsumer.cs`, `SK_P.sln`,
`scripts/phase-67-harness.ps1`, `analyzer-reports/*.json`). `git add -A` and `git add .` are
FORBIDDEN. Stage exactly:
    tests/BaseApi.Tests/Console/ConsumerFaultDispositionE2ETests.cs
    .planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-PLAN.md
    .planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-FINDINGS.md
    .planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-SUMMARY.md
    .planning/STATE.md
Decide deliberately whether to commit `run1-observe.out` / `run2-pinned.out`: the repo root is
already littered with ~110 untracked `.out` files, so prefer NOT committing them and instead quoting
their content inline in the findings note (the note is then self-contained). If you do commit them,
they must be inside the task directory, never at the repo root.
Then `git status --short` and CONFIRM the unrelated dirty paths are still unstaged before committing.
Commit message: `test(quick-260729-kpl): pin broker-literal consumer-fault disposition (RealStack proof)`.
  </action>
  <verify>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; grep -c "VERDICT" .planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-FINDINGS.md   # >= 1</automated>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; ./tests/BaseApi.Tests/bin/Release/net8.0/BaseApi.Tests.exe --filter-not-trait Category=RealStack 2>&amp;1 | tail -20   # failing NAME SET == pre-existing baseline; ConsumerFaultDisposition absent</automated>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; git diff HEAD~1 --stat -- src/   # EMPTY — the commit touches no production source</automated>
    <automated>cd C:/Users/UserL/source/repos/SK_P6 &amp;&amp; git status --short | head -20   # unrelated dirty paths still unstaged and uncommitted</automated>
  </verify>
  <done>
`260729-kpl-FINDINGS.md` exists with all eight sections, a single unambiguous `VERDICT:` line, an
explicit MET / SILENTLY-VIOLATED statement about the user's redelivery requirement, and — if the
verdict is WRONG — a grep-derived `file:line | claim | FALSIFIED/STILL-TRUE/NOT-A-CLAIM` table
carrying a DO-NOT-FIX-HERE line. Hermetic failing-test name set unchanged. The commit contains only
the enumerated paths, touches zero `src/` files, and leaves every unrelated working-tree change
unstaged.
  </done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| test process -> LIVE k8s broker (localhost:5673) | A test is being pointed at a production-shaped running stack; anything it declares, publishes or deletes is real |
| test process -> RabbitMQ management API (localhost:15673) | A `DELETE`-capable admin surface authenticated as `guest`; a wrong URL here destroys production topology |
| measurement -> recorded conclusion | The point of failure specific to a measurement task: a result shaped by the expectation rather than by the broker |
| working tree -> git index | ~110 untracked `.out` files and 5 unrelated modified paths must not enter this commit |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-kpl-01 | Tampering | Management-API `DELETE` hitting a PRODUCTION queue or exchange | mitigate | Deletion is restricted to an explicit allow-list: the three `{scratch}*` queue/exchange names (GUID-unique, `skp-probe-fault-` prefixed), any queue with the `skp-probe-fault-` prefix, and any exchange containing `ProbeFaultMessage`. No name-set diffing, no "delete what appeared" heuristic — the live stack creates and destroys queues on its own. Task 2 diffs the production queue listing before/after and must find it identical |
| T-kpl-02 | Denial of Service | An unbounded hot redelivery loop hammering the LIVE broker if the answer is nack-requeue | mitigate | 250 ms delay inside the consumer (~4 deliveries/s), optional `PrefetchCount=1`/`ConcurrentMessageLimit=1`, early break at `Invocations >= 5`, hard 25 s cap, and `host.StopAsync` immediately after the break |
| T-kpl-03 | Tampering | Production behavior changed while "measuring" | mitigate | `git diff -- src/` asserted EMPTY in all three tasks and `git diff HEAD~1 --stat -- src/` asserted empty on the commit; no `src/` path appears in `files_modified`; comment corrections are explicitly deferred to a follow-up with a DO-NOT-FIX-HERE line in the note |
| T-kpl-04 | Repudiation | A result that merely confirms the pre-existing belief (the classic measurement failure) | mitigate | `PinnedDisposition` starts at `Unpinned` so the test CANNOT pass until a human observes; the discriminator enumerates all four outcomes (including `BoundedRetryThenPark` and `Inconclusive`) and is written BEFORE the run; Task 2 forbids editing the discriminator after observing; `Inconclusive` may not be pinned |
| T-kpl-05 | Spoofing | Measuring the wrong thing — a hand-rolled bus, or the right bus against the wrong broker | mitigate | The bus is built by the production `AddBaseConsoleMessaging` with the endpoint name as the ONLY seam input; the port is reached via a config VALUE through the unmodified call site (URI-host form, verified in the 8.5.5 package XML docs); the test hard-fails if the scratch queue never appears on `localhost:15673`, which is the proof it reached the intended broker |
| T-kpl-06 | Spoofing | A per-message scoped consumer instance making "invoked once" a measurement artifact | mitigate | Invocations are counted in a STATIC `Interlocked` recorder reset per run, not on the consumer instance; offsets are recorded so a redelivery cadence is visible, not just a total |
| T-kpl-07 | Information Disclosure | Broker credentials in a committed test | accept | `guest:guest` on a local Docker-Desktop dev broker, already hardcoded across the existing RealStack suite (e.g. `CorrelationPropagationE2ETests`); no new secret is introduced |
| T-kpl-08 | Denial of Service | Disturbing the running stack (restart / scale / stale-image revert) | mitigate | No `kubectl apply -k k8s/` (reverts running images to a stale `:local` tag), no `kubectl scale`, no restarts, no docker-compose (k8s-only project); the test only declares its own scratch endpoint. Task 2 opens with a read-only reachability check that STOPS rather than repairs if the stack is down |
| T-kpl-09 | Elevation of Privilege | Unrelated dirty working-tree changes riding into the commit | mitigate | `git add -A` / `git add .` forbidden; Task 3 Part C enumerates the exact staged paths and requires a `git status --short` confirmation that the unrelated paths remain unstaged |
| T-kpl-10 | Tampering | A silently-weakened hermetic suite masking a regression | mitigate | Failing-test NAME SETS compared against the ~20-failure pre-existing baseline, never counts; an explicit instruction to STOP and report rather than adjust a newly-failing test |
| T-kpl-SC | Tampering | npm/pip/cargo installs | accept | No package installs of any kind — no new `PackageReference`, no new `ProjectReference`, no new fixture. The package-legitimacy gate does not apply |
</threat_model>

<verification>
1. `dotnet build SK_P.sln -c Release` — success, ZERO warnings (`TreatWarningsAsErrors=true`).
2. `git diff -- src/` is EMPTY throughout, and `git diff HEAD~1 --stat -- src/` is empty on the
   final commit. Measurement only.
3. Filtered RealStack invocation of `ConsumerFaultDispositionE2ETests` PASSES twice consecutively
   after the pin (repeatability + no cross-run residue).
4. The verdict was pinned from OBSERVED output: `run1-observe.out` shows the `Unpinned` failure with
   its evidence block; `run2-pinned.out` shows the pass. Both are quoted in the findings note.
5. Broker left clean: zero `skp-probe-fault-*` queues, zero `*ProbeFaultMessage*` exchanges on vhost
   `/`, and the production queue-name listing matches the pre-run listing.
6. Hermetic run (`--filter-not-trait Category=RealStack`) does NOT execute the new test and its
   failing-test NAME SET equals the pre-existing baseline.
7. `260729-kpl-FINDINGS.md` carries a single unambiguous `VERDICT:` line, the pinned MassTransit
   version with its documented default and an AGREES/DISAGREES line, and an explicit MET or
   SILENTLY-VIOLATED statement on the Start/Stop/PauseAll/ResumeAll redelivery requirement.
8. If the verdict is WRONG: a grep-derived `file:line` table classifying every hit as FALSIFIED /
   STILL-TRUE / NOT-A-CLAIM, with a DO-NOT-FIX-HERE line. Zero comments corrected in this task.
9. `git status --short` still shows the unrelated dirty paths unstaged and uncommitted.
</verification>

<success_criteria>
The question is settled by broker-literal evidence, not by inference from an absent `ConfigureError`
call. A committed RealStack test pins the observed disposition and will fail the day it changes. A
findings note records the observation, the raw evidence, the pinned MassTransit version, the
documented default, and a yes/no verdict on the 22-file doc-comment claim plus the user's redelivery
requirement. If the claim is falsified, every offending line is enumerated for a follow-up — and
none of them is edited here. `src/` is byte-unchanged and the live stack is undisturbed.
</success_criteria>

<output>
Create `.planning/quick/260729-kpl-realstack-proof-does-a-throwing-consumer/260729-kpl-SUMMARY.md`
when done. It MUST state the VERDICT in its first three lines — this task exists to produce an
answer, and a summary that buries it has failed at its one job.
</output>
</content>
</invoke>
