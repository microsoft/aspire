# Unquarantine Policy

## Overview

Tests that have been quarantined (marked with `[QuarantinedTest]`) must meet strict criteria before they can be unquarantined. A code fix alone is not sufficient — the test must demonstrate sustained reliability across all platforms.

## Policy

A quarantined test can be unquarantined when it has **zero failures across ALL operating systems (Windows, Linux, macOS) for at least 21 consecutive days** in the quarantine CI runs.

The quarantine CI workflow (`tests-quarantine.yml`) runs every 6 hours. The failure tracking data in each test's GitHub issue (linked from meta-issue https://github.com/microsoft/aspire/issues/8813) records per-OS pass/fail rates.

## What This Means for Fixing Flaky Tests

When you fix a quarantined test:

1. **DO** apply your fix and push it
2. **DO** verify the fix via the reproduce workflow (`reproduce-flaky-tests.yml`)
3. **DO NOT** unquarantine the test in the same PR — leave the `[QuarantinedTest]` attribute in place
4. **DO NOT** close the tracking issue — a separate process monitors the 21-day window and closes issues when the criteria are met

The test will continue running in the quarantine CI. If your fix is correct, the failure rate will drop to 0%. After 21 days of zero failures, a separate process (or a human) will unquarantine the test and close the issue.

## Why 21 Days?

Some flaky tests fail rarely (1-5% rate) or only under specific conditions (load, time-of-day, runner hardware). A 21-day window with runs every 6 hours provides ~84 data points per OS (~252 total), giving high confidence that the fix is durable.

## Exceptions

- If the test was quarantined due to a **test infrastructure bug** (not a timing/race issue) and the fix is clearly correct, a shorter observation period may be acceptable at maintainer discretion.
- If the failure rate was extremely high (>80%) and drops to 0% immediately after the fix, maintainers may choose to unquarantine sooner.

In all cases, the decision to unquarantine is made by reviewing the tracking data, not by the agent that applied the fix.
