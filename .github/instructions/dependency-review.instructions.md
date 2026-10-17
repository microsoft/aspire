---
applyTo: "**/*Packages.props,eng/Versions.props,**/package.json,**/yarn.lock,**/package-lock.json,.github/workflows/**,**/*ContainerImageTags.cs"
---

# Dependency updates

Review dependency-only diffs as behavior changes, not mechanical version edits.
Read nearby documented holds and consumer constraints before approving. Run
`python3 eng/dependency-review/check.py` for the focused objective constraints;
a clean result is not a compatibility or upstream-safety certificate.

Use the dependency evidence procedure in `.agents/skills/pr-testing/dependency-review.md`.
Map changed dependencies to actual build, runtime, generated-code, deployment,
and platform consumers. Separate required coverage, CI selection, successful
execution, blocked execution, and unexercised coverage at the exact head SHA.
Skipped jobs, aggregate green CI, and review-request jobs do not prove coverage.

For npm, inspect resolved lockfile versions and advisories, not just selectors.
For extension updates, check the declared minimum VS Code host as well as the
latest host; newer type declarations alone do not prove a runtime regression.
For image majors, review migration/data-format and supported platform effects.
Report definite defects separately from unknowns and follow-up scenarios.
Existing approved-feed, action allow-list, generated-API, and test-selection
rules still apply; do not duplicate or weaken them here.
