---
name: automated-failure-issues
description: Use when creating or reviewing automation that files recurring CI, scheduled workflow, infrastructure, or failing-test GitHub issues.
---

# Automated Failure Issues

## Overview

Give each recurring failure a stable, versioned identity and delegate lifecycle mechanics to `.github/workflows/tracking-issue.js`. Treat identity as persistent data: changing normalization, field order, delimiters, or hashing re-keys every historical issue.

**REQUIRED SUB-SKILL:** Use test-driven-development for behavior changes.

## Core Contract

| Concern | Owner | Rule |
|---|---|---|
| Identity | Producer/resolver | Build an exact hidden marker from stable fields, never a title or raw error message |
| Reconciliation | `tracking-issue.js` | List by label, choose the oldest exact match, deduplicate occurrences, and close newer exact duplicates |
| Resolution | Producer | Auto-close only from a trusted success signal and an `autoclose:true` stamp |

Each producer must have exactly one lookup path. Both find and create must use the same marker and lookup label. Do not add a Search fallback, title fallback, or a second implementation with different canonical selection.

For failing tests, reuse `result.issue.metadataMarker` from `tools/CreateFailingTestIssue`. The pipeline is `canonical test + workflow path -> normalize -> XxHash3 -> v1 marker -> issue body -> exact lookup`. Do not reproduce it in shell or JavaScript.

For other producers, prefer a readable versioned marker whose fields are already short and stable:

```javascript
const tracking = require('./tracking-issue.js');
const marker = `<!-- ci-failure:v1:${workflowFile}:${failureKind} -->`;

await tracking.ensureLabel(github, context.repo.owner, context.repo.repo, labelDefinition);
await tracking.recordRun(github, context, core, {
    label: 'automation-broken',
    marker,
    title,
    runId: context.runId,
    buildBody: () => tracking.buildBody({ marker, lead, note, autoClose }),
    comment,
    closeDuplicates: true,
});
```

Use XxHash3 only when stable identity fields are too long for a readable marker.

## Duplicate and Migration Rules

1. Query `issues.listForRepo` through `listIssuesByLabel`; GitHub Search is eventually consistent.
2. Match complete hidden markers only. Never use titles, stack traces, fuzzy text, or model similarity.
3. The lowest issue number is canonical, even when closed. A recurrence reopens it.
4. An alias is proven only when a deterministic resolver parses both marker versions to the same canonical stable fields. Document the mapping and cover positive and negative fixtures; never use a prefix or fuzzy alias.
5. Pass `closeDuplicates: true` only for machine-owned markers. The reconciler comments on and closes newer matches as `not_planned`; it never deletes or copies discussion.
6. Serialize producers when practical. Re-listing after create heals a race but does not replace workflow concurrency.
7. `--force-new` is an audited exception. Stamp its issue with `duplicateExemptStamp()` so reconciliation neither selects nor closes it.

When identity semantics must change, bump the marker version. Keep the old exact marker as an alias until every all-state historical issue has the new marker or can never recur. Before retiring an alias, inventory open and closed labeled issues and add a migration test. Never silently reinterpret an existing version.

## Auto-Close Policy

The engine does not decide when a failure is fixed. The producer must verify a later success from the same trusted workflow, subject, and ref, require `readAutoClose(issue.body) === true`, then close with `state_reason: completed`. Missing or malformed stamps fail closed. Grant only the `issues: write` permission needed for mutation.

## Required Tests

Cover:

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

Extend `TrackingIssueTests.cs` through `tracking-issue.harness.js`, then add a real producer integration test. Verify label creation, all-state pagination, consumer syntax, and workflow compilation; helper-only tests are insufficient.

## Common Mistakes

| Mistake | Correction |
|---|---|
| `search.issuesAndPullRequests` for dedup | Use the strongly consistent label list |
| Hashing raw logs | Identify the durable subject, not one occurrence |
| Closing fuzzy matches | Require exact markers or resolver-proven aliases |
| Omitting `ensureLabel` | Create the lookup label before reconciliation |
| Supplying pre-fetched open issues | Supply the complete all-state labeled inventory |
| Putting stale-day policy in the engine | Keep resolution policy in the producer |
| Rewriting or deleting duplicate history | Link, close, and preserve it |
