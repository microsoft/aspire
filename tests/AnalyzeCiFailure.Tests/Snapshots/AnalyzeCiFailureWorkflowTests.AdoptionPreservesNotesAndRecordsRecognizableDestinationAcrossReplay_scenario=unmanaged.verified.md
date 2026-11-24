<!-- ci-failure-cause:debugger-timeout -->
<!-- ci-failure-cause-type:flaky-test -->

## Build Information

Build: https://github.com/microsoft/aspire/actions/runs/123
Build error leg: ` Tests `
Pull request: #42

## Error Message

    Connection timed out

## Description

` Debugger timeout `

**Type**: flaky-test

<!-- ci-failure-tests:start -->
## Observed tests

- ` Tests.Method(value: 1/2) ` — job 123 (` Tests `)
- ` Tests.Other ` — job 123 (` Tests `)
<!-- ci-failure-tests:end -->

<!-- ci-failure-occurrences:start -->
## Occurrences

Showing 1 most recent of 1 occurrences.

| Date | Build | Job | Context |
|------|-------|-----|----|
| 2026-10-21 | [123](https://github.com/microsoft/aspire/actions/runs/123) | ` Tests ` | #42 |
<!-- ci-failure-occurrences:end -->

## Original issue notes

<!-- ci-failure-adoption-notes:start -->
<!-- CIF:legacy-fingerprint -->
Human diagnosis.

Keep these notes exactly.

<!-- ci-failure-adoption-notes:end -->
