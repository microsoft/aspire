<!-- analyze-ci-failure -->
⚠️ **CI Failure Analysis: Possible Flaky Test(s)**

The CI build failed due to test failure(s) that appear unrelated to the PR changes. These may be flaky tests.

**Failed jobs:**
- ` Tests ` — ` Classified failure ` (flaky-test)

**Suggested actions:**
- Re-run the failed CI jobs to confirm if the failure is intermittent
- If the test continues to fail, consider [quarantining it](https://github.com/microsoft/aspire/blob/main/docs/quarantined-tests.md) using `/quarantine-test <test name> <issue URL>`
- Search [existing issues](https://github.com/microsoft/aspire/issues?q=is%3Aissue+label%3Atest-failure) to see if this test is already known to be flaky

You can re-run the failed jobs from the [workflow run page](https://github.com/microsoft/aspire/actions/runs/123).

