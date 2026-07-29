---
phase: 88
slug: pipeline-dashboard-maintenance-handoff-proof-fault-injection
date: 2026-07-29
status: accepted
requirements: [HAND-01]
evidence: analyzer-reports/phase-88-discrimination.json
operator-copy: docs/runbooks/business-dashboard-runbook.md
---

# Phase 88 — the runbook lives in `docs/`, not here

**The HAND-01 symptom → panel → action runbook is at
[`docs/runbooks/business-dashboard-runbook.md`](../../../docs/runbooks/business-dashboard-runbook.md).**

It is authored there rather than in this phase directory deliberately, and the reason is the rubric's
own question 6: a maintenance department has to be able to **find** the thing it is meant to rely on.
`.planning/` is a record of how this project was built, not a place anyone on maintenance duty would
look; `docs/` is the repo's only existing documentation tree. Authoring the runbook under
`.planning/` would have been convenient and would have made the runbook itself part of the
reachability problem the handoff verdict records as blocking gap **B1**. Placing it in `docs/` does
not close that gap — nothing in this phase's scope can — but it stops the runbook from adding to it.

This file is a pointer and nothing else. **The runbook table is not duplicated here**, because two
copies of an operator document drift and the stale one is the one that gets read.

The companion record is [`88-HANDOFF-VERDICT.md`](88-HANDOFF-VERDICT.md), which states whether this
dashboard is fit to hand over and under what conditions, and
[`88-FINDINGS.md`](88-FINDINGS.md), which carries the accepted-unproven register and the
fourteen-by-six practicality rubric the verdict is derived from.
