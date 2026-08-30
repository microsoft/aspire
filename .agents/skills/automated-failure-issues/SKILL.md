---
name: automated-failure-issues
description: Use when creating or reviewing automation that files recurring CI, scheduled workflow, infrastructure, or failing-test GitHub issues.
---

# Automated Failure Issues

## Overview

Give each recurring failure a stable, versioned identity and route every lifecycle
operation through the checked-in tracking-issue modules. Treat identity as persistent
data: changing normalization, field order, delimiters, or hashing re-keys every
historical issue.

**REQUIRED SUB-SKILL:** Use test-driven-development for behavior changes.

## Ownership Boundary

| Concern | Owner | Rule |
|---|---|---|
| Deterministic mechanics | `tracking-issue.js` and its transports | Find, create, relist, select, update, reopen, comment, close, and reconcile duplicates |
| Identity | Producer or resolver | Define stable fields, normalization, marker version, and deterministic aliases |
| Content and policy | Producer | Supply titles, bodies, comments, labels, force-new intent, and trusted close decisions |
| Integration guidance | This skill | Preserve the ownership boundary, migration rules, and verification requirements |

Use [`.github/workflows/tracking-issue.js`](../../../.github/workflows/tracking-issue.js)
directly from JavaScript producers. Non-JavaScript callers must use the existing
[`create-failing-test-issue-tracking.js`](../../../.github/workflows/create-failing-test-issue-tracking.js)
adapter and
[`tracking-issue-gh-api.js`](../../../.github/workflows/tracking-issue-gh-api.js)
transport so they delegate to the same planner and executor. Do not invent another
gh-backed transport or reproduce selection and mutation rules in workflow YAML,
shell, C#, or another helper.

Each producer has exactly one supported lookup path. Find, create, update, reopen,
close, duplicate reconciliation, live execution, and dry-run planning must consume
the same marker, lookup label, planner, and transport contract. Never add a Search
fallback, title fallback, direct-create exception, or a second planner.

## Identity Design

Write down the identity fields and normalization before implementation. Prefer a
readable exact marker when stable fields are already short. Use XxHash3 only when
those fields are too long, and freeze a golden result.

For failing tests, `tools/CreateFailingTestIssue` owns the pipeline
`canonical test + workflow path -> normalize -> XxHash3 -> versioned marker`.
Consumers reuse `result.issue.metadataMarker`; they do not recalculate or parse it.

Identity must not depend on titles, raw error text, stack traces, run IDs, timestamps,
or model similarity. Marker matching is exact. Treat changes to case folding,
whitespace, path separators, field ordering, delimiters, or hash encoding as
identity changes.

## Integration Workflow

1. Add the lookup label with `ensureLabel` before reconciliation. A missing label is
   an integration failure, not permission to use a broader lookup.
2. Supply the exact marker, complete all-state labeled inventory, content builders,
   and producer policy to the shared engine.
3. Use the shared executor for live calls and its dry-run transport/planner seam for
   previews. Dry run must differ only in transport side effects.
4. Keep workflow markdown thin: parse trusted inputs, call the producer module, and
   report its result.
5. Serialize producers when practical. Create/relist convergence heals creator races
   but does not replace workflow concurrency.

With duplicate reconciliation enabled, the shared engine lists all issue states by
label, chooses the lowest-numbered exact match even when closed, deduplicates run
comments across canonical and duplicate matches, and can link and close newer exact
duplicates as `not_planned`. Without duplicate reconciliation, `reconcileRun`
preserves its open-match preference. Producers must not pre-filter the inventory to
open issues.

`--force-new` is an audited producer intent, not a lifecycle bypass. Send it through
the planner and stamp the resulting issue as duplicate-exempt. Reconciliation must
neither select nor close exempt issues.

## Version Migration and Retirement

Changing identity semantics requires a new marker version. Never silently reinterpret
an existing version.

An alias is valid only when a deterministic resolver proves that both marker versions
map to the same canonical stable fields. Document that proof and cover positive and
negative fixtures. Prefix, title, substring, and fuzzy matching are not proof.

Keep the old exact marker as an alias until every open and closed labeled issue has
been migrated or the old identity can no longer recur. Before retiring an alias,
inventory all states, add a migration/retirement test, and confirm every producer
emits only the new version.

## Trusted Auto-Close

The engine does not decide whether a failure is fixed. The producer must verify a
later success from the same trusted workflow, subject, and ref, require an exact
`autoclose:true` stamp, and then ask the shared executor to close with
`state_reason: completed`. Missing or malformed stamps fail closed. Dry-run and live
close use the same plan. Grant only the `issues: write` permission needed to mutate.

## Required Tests

Start with failing engine and producer tests. Cover:

- a frozen golden marker plus case, whitespace, and path-separator normalization;
- changed titles and similar errors not matching;
- oldest closed issue beating a newer open duplicate;
- explicit legacy aliases;
- concurrent create convergence;
- repeated run IDs;
- a closed canonical staying closed when that run was already recorded there;
- duplicate-exempt issues remaining independent;
- exact duplicates closing as `not_planned`;
- missing `autoclose:true` preventing resolution.

Exercise planner/executor behavior through `TrackingIssueTests.cs` and
`tracking-issue.harness.js`, then add a real producer integration test through its
checked-in harness. Cross-language adapters need conformance coverage proving they
delegate to the same engine. Verify label creation, all-state pagination, creator
relisting, every transport mutation, dry-run parity, consumer syntax, and workflow
compilation; helper-only tests are insufficient.

## Common Mistakes

| Mistake | Correction |
|---|---|
| Search or an alternate CLI lookup | Use the shared all-state label-list transport |
| Lifecycle logic embedded in YAML or C# | Delegate through a checked-in adapter to the shared planner/executor |
| Hashing raw logs | Identify the durable subject, not one occurrence |
| Closing fuzzy matches | Require exact markers or resolver-proven aliases |
| Omitting `ensureLabel` | Create the lookup label before reconciliation |
| Supplying pre-fetched open issues | Supply the complete all-state labeled inventory |
| A separate dry-run decision tree | Execute the same plan against the dry-run transport |
| Direct create for force-new | Use the planner and duplicate-exempt stamp |
| Putting stale-day policy in the engine | Keep resolution policy in the producer |
| Rewriting or deleting duplicate history | Link, close, and preserve it |
