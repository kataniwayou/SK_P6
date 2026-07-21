# Phase 71 — Deferred Items (out-of-scope discoveries)

## Pre-existing full-suite test baseline (NOT caused by Plan 01)

**Discovered during:** 71-01 Task 2 verification.

**Observation:** `dotnet test tests/BaseApi.Tests/BaseApi.Tests.csproj` (whole suite, no filter)
reports **287 Failed / 476 Passed / 763 Total** on a clean tree at commit `44da475`
(Plan-01 Task-1 only; before any Task-2 source edit). The identical count was measured **after
stashing the Task-2 changes** — proving these failures are a **pre-existing baseline**, not a
regression introduced by the A1 close.

**Scope decision:** Out of scope for Plan 01 per the executor SCOPE BOUNDARY (only auto-fix issues
directly caused by the current task's changes). The Plan-01 touched slice
(`OutputTailFacts`, `InjectConsumerFacts`, `OrchestratorContractTests`) is **9/9 green**, and the
A1 edits do not break any other test (verified: no test asserts `Guid.Empty` on a *Completed*
`StepCompleted.EntryId` outside the two updated A1 assertions; production sites that build
`StepCompleted` are only `OutputTail.BuildStep` + `InjectConsumer.HandleAsync`, both handled).

**Note on the test runner:** the suite uses Microsoft.Testing.Platform (xUnit v3 MTP). VSTest-style
`--filter "FullyQualifiedName~X"` is **ignored** (emits MTP0001 and runs the whole suite). The
working filter is the MTP-native form: `-- --filter-class "Namespace.ClassName"`. The Plan-01
`<verify>` blocks used the VSTest syntax; the executor substituted the MTP-native `--filter-class`
to scope the targeted runs (deviation tracked in SUMMARY).

**Recommendation:** a future Wave-2 plan (or a dedicated suite-health quick task) should
triage/quarantine the 287 pre-existing failures. They are unrelated to the Phase-71 contracts/A1
work and should not gate Plan-01 completion.
