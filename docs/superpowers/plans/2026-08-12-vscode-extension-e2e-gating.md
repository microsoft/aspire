# VS Code Extension E2E Gating Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `subagent-driven-development` (recommended) or `executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore blocking VS Code extension E2E coverage for healthy matrix rows while preserving diagnostics for explicitly tracked failures and ensuring child-process or harness failures can never be hidden by Mocha output.

**Architecture:** Replace the blanket boolean failure switch with an issue-backed `advisoryIssue` matrix field. Keep process execution failures structured in a focused CommonJS helper so the runner can allow only ordinary non-zero exits that have matching completed Mocha failures; timeout, signal, spawn, setup, hook, crash, and cleanup failures remain blocking. Fix the known Azure Functions resource-stream failure, Windows notification race, and Linux AppHost-tree synchronization issue while retaining advisory entries until hosted runs prove each row green.

**Tech Stack:** GitHub Actions YAML, Node.js/CommonJS, TypeScript, Mocha, VS Code Extension Tester/Selenium, C# 13, xUnit v3, gRPC.

---

## File map

- `extension/scripts/e2e-process-failure.cjs`: owns structured child-process errors and the narrow advisory predicate.
- `extension/scripts/run-e2e.js`: creates structured failures, reads the advisory issue, and decides whether a test failure blocks.
- `extension/scripts/e2e-mocha-results.cjs`: continues to identify completed Mocha test failures.
- `extension/src/test/e2eMochaReporter.test.ts`: functionally verifies Mocha completion and process-failure classification.
- `.github/workflows/extension-e2e-tests.yml`: makes healthy rows blocking and declares the four issue-backed advisory rows.
- `extension/src/test/e2eShardMatrix.test.ts`: validates the exact advisory allowlist and rejects malformed or blanket failure settings.
- `extension/src/test/e2eLaunchProfile.test.ts`: pins runner/workflow wiring and the two E2E helper regressions.
- `tests/Infrastructure.Tests/Pipelines/ExtensionE2eWorkflowTests.cs`: validates the workflow contract from the .NET infrastructure suite.
- `src/Aspire.Hosting/Dashboard/proto/Partials.cs`: maps unknown command states to hidden instead of terminating `WatchResources`.
- `tests/Aspire.Hosting.Tests/Dashboard/DashboardServiceTests.cs`: verifies the resource stream survives an unknown command state.
- `extension/src/test-e2e/helpers/vscode.ts`: retries notification reads when Selenium replaces an element.
- `extension/src/test-e2e/appHostTree.e2e.test.ts`: waits for durable CLI request gates before checking the running AppHost.
- `extension/src/test-e2e/helpers/fixtures.ts`: exposes the durable `ps` and `ls` request markers.

### Task 1: Update the branch baseline

**Consumed by:** Tasks 2, 3, 4, 5, 6, 7, 8

**Files:**
- Merge: `upstream/main`
- Preserve: `extension/src/test-e2e/appHostTree.e2e.test.ts`
- Preserve: `extension/src/test-e2e/helpers/fixtures.ts`
- Preserve: `extension/src/test/e2eLaunchProfile.test.ts`

- [ ] **Step 1: Confirm unrelated work is untouched**

Run:

```bash
git status --short
```

Expected: only the pre-existing untracked `.e2e-tmp/`, `.pr19261-changelog.md`, and `.pr19261.diff` entries.

- [ ] **Step 2: Merge the current upstream baseline**

Run:

```bash
git fetch upstream main
git merge --no-edit upstream/main
```

Expected: the branch gains #19275 and later `main` changes while retaining commit `4ff8747499` and its durable AppHost-tree gates.

- [ ] **Step 3: Verify the branch fix survived**

Run:

```bash
git diff upstream/main...HEAD -- extension/src/test-e2e/appHostTree.e2e.test.ts extension/src/test-e2e/helpers/fixtures.ts extension/src/test/e2eLaunchProfile.test.ts
```

Expected: the diff still contains `waitForPsSnapshotRequest`, `waitForLsCandidateRequest`, and releases both gates in `finally`.

### Task 2: Add structured process-failure classification

**Consumed by:** Task 3

**Files:**
- Create: `extension/scripts/e2e-process-failure.cjs`
- Modify: `extension/src/test/e2eMochaReporter.test.ts`

- [ ] **Step 1: Write failing classification tests**

Add tests that load `e2e-process-failure.cjs` and verify only an `exit-code` failure with a completed matching Mocha failure is advisory:

```ts
test('allows only ordinary exit-code failures with completed Mocha failures', () => {
    const {
        E2eProcessError,
        shouldAllowAdvisoryTestFailure,
    } = require(path.join(__dirname, '..', '..', 'scripts', 'e2e-process-failure.cjs'));
    const results = {
        tests: [{ fullTitle: 'Aspire E2E starts an AppHost' }],
        failures: [{ fullTitle: 'Aspire E2E starts an AppHost' }],
    };

    assert.strictEqual(
        shouldAllowAdvisoryTestFailure(
            new E2eProcessError('exit-code', 'node', ['run-tests'], { exitCode: 1 }),
            results,
            false),
        true);

    for (const reason of ['timeout', 'signal', 'spawn'] as const) {
        assert.strictEqual(
            shouldAllowAdvisoryTestFailure(
                new E2eProcessError(reason, 'node', ['run-tests'], reason === 'signal' ? { signal: 'SIGTERM' } : {}),
                results,
                false),
            false);
    }

    assert.strictEqual(
        shouldAllowAdvisoryTestFailure(
            new E2eProcessError('exit-code', 'node', ['run-tests'], { exitCode: 1 }),
            results,
            true),
        false);
});
```

Also assert `exitCode`, `signal`, and `cause` are retained on their corresponding error objects.

- [ ] **Step 2: Compile and run the test to verify it fails**

Run:

```bash
cd extension
corepack yarn compile-tests
corepack yarn compile
corepack yarn unit-test --run out/test/e2eMochaReporter.test.js
```

Expected: FAIL because `scripts/e2e-process-failure.cjs` does not exist.

- [ ] **Step 3: Implement the focused helper**

Create:

```js
'use strict';

const { hasCompletedMochaTestFailures } = require('./e2e-mocha-results.cjs');

class E2eProcessError extends Error {
  constructor(reason, command, args, options = {}) {
    const commandLine = `${command} ${args.join(' ')}`;
    const detail = reason === 'exit-code'
      ? `exited with code ${options.exitCode}`
      : reason === 'timeout'
        ? `timed out after ${options.timeout}ms${options.didNotExit ? ' and did not exit after process-tree termination' : ''}`
        : reason === 'signal'
          ? `exited after signal ${options.signal ?? 'unknown'}`
          : `failed to spawn: ${options.cause?.message ?? 'unknown error'}`;
    super(`${commandLine} ${detail}.${options.diagnosticsSuffix ?? ''}`, options.cause ? { cause: options.cause } : undefined);
    this.name = 'E2eProcessError';
    this.reason = reason;
    this.exitCode = options.exitCode;
    this.signal = options.signal;
  }
}

function shouldAllowAdvisoryTestFailure(error, mochaResults, cleanupFailed) {
  return error instanceof E2eProcessError
    && error.reason === 'exit-code'
    && hasCompletedMochaTestFailures(mochaResults)
    && !cleanupFailed;
}

module.exports = {
  E2eProcessError,
  shouldAllowAdvisoryTestFailure,
};
```

- [ ] **Step 4: Run the focused test**

Run:

```bash
cd extension
corepack yarn compile-tests
corepack yarn compile
corepack yarn unit-test --run out/test/e2eMochaReporter.test.js
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add extension/scripts/e2e-process-failure.cjs extension/src/test/e2eMochaReporter.test.ts
git commit -m "Classify extension E2E process failures" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>" -m "Copilot-Session: 3d108969-ce19-4473-9da0-9b5ff1e52f01"
```

### Task 3: Wire structured failures into the E2E runner

**Consumed by:** Task 4

**Files:**
- Modify: `extension/scripts/run-e2e.js`
- Modify: `extension/src/test/e2eLaunchProfile.test.ts`

- [ ] **Step 1: Write failing runner-wiring assertions**

Replace the blanket advisory source assertions with checks for:

```ts
assert.ok(runner.includes("const advisoryIssue = process.env.ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE || '';"));
assert.ok(runner.includes("const { E2eProcessError, shouldAllowAdvisoryTestFailure } = require('./e2e-process-failure.cjs');"));
assert.ok(runner.includes("new E2eProcessError('timeout'"));
assert.ok(runner.includes("new E2eProcessError('signal'"));
assert.ok(runner.includes("new E2eProcessError('exit-code'"));
assert.ok(runner.includes("new E2eProcessError('spawn'"));
assert.ok(runner.includes('shouldAllowAdvisoryTestFailure(testFailure, readMochaResults(), cleanupFailed)'));
assert.ok(!runner.includes('ASPIRE_EXTENSION_E2E_ALLOW_TEST_FAILURE'));
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```bash
cd extension
corepack yarn compile-tests
corepack yarn compile
corepack yarn unit-test --run out/test/e2eLaunchProfile.test.js
```

Expected: FAIL because `run-e2e.js` still uses `allowTestFailure` and generic `Error`.

- [ ] **Step 3: Construct structured errors in `runWithProcessTreeTimeout`**

Import the helper and use one reason per failure path:

```js
const {
  E2eProcessError,
  shouldAllowAdvisoryTestFailure,
} = require('./e2e-process-failure.cjs');

const advisoryIssue = process.env.ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE || '';
```

Use `spawn` errors as `spawn`, timeout callbacks as `timeout`, null exit codes with signals as `signal`, and non-zero numeric exit codes as `exit-code`. Pass the existing diagnostics paths through `diagnosticsSuffix`.

- [ ] **Step 4: Restrict the advisory branch**

Replace the existing predicate with:

```js
if (advisoryIssue && shouldAllowAdvisoryTestFailure(testFailure, readMochaResults(), cleanupFailed)) {
  console.warn(`::warning title=VS Code extension E2E test failure advisory::${shardName} has completed test failures tracked by ${advisoryIssue}. Diagnostics were uploaded for investigation.`);
  return;
}
```

Setup, hook, crash, timeout, signal, spawn, and cleanup failures continue to throw.

- [ ] **Step 5: Run the focused tests**

Run:

```bash
cd extension
corepack yarn compile-tests
corepack yarn compile
corepack yarn unit-test --run out/test/e2eMochaReporter.test.js --run out/test/e2eLaunchProfile.test.js
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add extension/scripts/run-e2e.js extension/src/test/e2eLaunchProfile.test.ts
git commit -m "Keep extension E2E harness failures blocking" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>" -m "Copilot-Session: 3d108969-ce19-4473-9da0-9b5ff1e52f01"
```

### Task 4: Replace blanket failure allowance with tracked advisory rows

**Consumed by:** Task 8

**Files:**
- Modify: `.github/workflows/extension-e2e-tests.yml`
- Modify: `extension/src/test/e2eShardMatrix.test.ts`
- Modify: `extension/src/test/e2eLaunchProfile.test.ts`
- Modify: `tests/Infrastructure.Tests/Pipelines/ExtensionE2eWorkflowTests.cs`

- [ ] **Step 1: Write failing matrix contract tests**

Parse `advisoryIssue` as an optional string and define this exact allowlist:

```ts
const expectedAdvisoryRows = new Map<string, string>([
    [
        'Linux|apphost-tree|out/test-e2e/test-e2e/appHostTree.e2e.test.js',
        'https://github.com/microsoft/aspire/issues/19282',
    ],
    [
        'Windows|discovery-configuration|out/test-e2e/test-e2e/discoveryConfiguration.e2e.test.js',
        'https://github.com/microsoft/aspire/issues/19282',
    ],
    [
        'Linux|azure-functions|out/test-e2e/test-e2e/azureFunctions.e2e.test.js',
        'https://github.com/microsoft/aspire/issues/19151',
    ],
    [
        'Windows|debug-dashboard|out/test-e2e/test-e2e/debugDashboard.e2e.test.js',
        'https://github.com/microsoft/aspire/issues/19282',
    ],
]);
```

Reject `allowFailure`, `disabledIssue`, empty advisory values, non-`microsoft/aspire` issue URLs, and any workflow advisory row not present in the map.

- [ ] **Step 2: Rewrite the .NET workflow contract test**

Require:

```csharp
Assert.All(rows, row => Assert.False(row.Children.ContainsKey(new YamlScalarNode("allowFailure"))));
Assert.All(rows, row => Assert.False(row.Children.ContainsKey(new YamlScalarNode("disabledIssue"))));
Assert.Contains(rows, row => row.Children.ContainsKey(new YamlScalarNode("advisoryIssue")));
Assert.Contains(rows, row => !row.Children.ContainsKey(new YamlScalarNode("advisoryIssue")));

var environment = (YamlMappingNode)runSuiteStep.Children[new YamlScalarNode("env")];
Assert.Equal("${{ matrix.advisoryIssue }}", Scalar(environment, "ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE"));
Assert.Null(Scalar(runSuiteStep, "continue-on-error"));
Assert.Null(Scalar(runSuiteStep, "if"));
```

- [ ] **Step 3: Run tests to verify they fail**

Run:

```bash
cd extension
corepack yarn compile-tests
corepack yarn compile
corepack yarn unit-test --run out/test/e2eShardMatrix.test.js --run out/test/e2eLaunchProfile.test.js
cd ..
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- --filter-class "*.ExtensionE2eWorkflowTests" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Expected: FAIL while all rows still use `allowFailure: true`.

- [ ] **Step 4: Update the workflow**

Delete every `allowFailure: true`. Add `advisoryIssue` only to the four rows in the allowlist. Replace:

```yaml
ASPIRE_EXTENSION_E2E_ALLOW_TEST_FAILURE: ${{ matrix.allowFailure }}
```

with:

```yaml
ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE: ${{ matrix.advisoryIssue }}
```

Do not add `continue-on-error`; every row still runs and the Node runner owns the narrow advisory decision.

- [ ] **Step 5: Run the focused tests**

Run the commands from Step 3.

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add .github/workflows/extension-e2e-tests.yml extension/src/test/e2eShardMatrix.test.ts extension/src/test/e2eLaunchProfile.test.ts tests/Infrastructure.Tests/Pipelines/ExtensionE2eWorkflowTests.cs
git commit -m "Restore blocking extension E2E rows" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>" -m "Copilot-Session: 3d108969-ce19-4473-9da0-9b5ff1e52f01"
```

### Task 5: Keep the dashboard resource stream alive for unknown command states

**Consumed by:** Task 8

**Files:**
- Modify: `tests/Aspire.Hosting.Tests/Dashboard/DashboardServiceTests.cs`
- Modify: `src/Aspire.Hosting/Dashboard/proto/Partials.cs`

- [ ] **Step 1: Extend the existing command-stream test**

Add a UI command whose state callback returns an unknown enum:

```csharp
builder.WithCommand(
    name: "UnknownStateName",
    displayName: "Unknown state display name",
    executeCommand: c => Task.FromResult(CommandResults.Success()),
    commandOptions: new()
    {
        UpdateState = c => (Hosting.ApplicationModel.ResourceCommandState)999
    });
```

Wait for all three command snapshots, then assert the gRPC resource contains exactly the normal enabled command and the unknown command mapped to `ResourceCommandState.Hidden`. The API-only command remains absent by virtue of the exact collection assertion.

- [ ] **Step 2: Run the test to verify it fails**

Run:

```bash
./restore.sh
dotnet test --project tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj --no-launch-profile -- --filter-method "*.WatchResources_ResourceHasCommands_CommandsSentWithResponse" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Expected: FAIL with `InvalidOperationException: Unexpected state: 999`.

- [ ] **Step 3: Fail closed in the protocol mapper**

Change the fallback to:

```csharp
_ => ResourceCommandState.Hidden
```

This keeps the stream alive and prevents a command with an unknown state from becoming actionable.

- [ ] **Step 4: Run the focused test**

Run the command from Step 2.

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting/Dashboard/proto/Partials.cs tests/Aspire.Hosting.Tests/Dashboard/DashboardServiceTests.cs
git commit -m "Hide unknown dashboard command states" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>" -m "Copilot-Session: 3d108969-ce19-4473-9da0-9b5ff1e52f01"
```

### Task 6: Retry stale notification elements

**Consumed by:** Task 8

**Files:**
- Modify: `extension/src/test-e2e/helpers/vscode.ts`
- Modify: `extension/src/test/e2eLaunchProfile.test.ts`

- [ ] **Step 1: Add a failing source contract**

Extract the `waitForNotificationMessage` function body and assert its WebDriver callback catches transient element failures and returns `false`:

```ts
assert.ok(waitForNotification.includes('try {'));
assert.ok(waitForNotification.includes('const notifications = await new Workbench().getNotifications();'));
assert.ok(waitForNotification.includes('const message = await notification.getMessage();'));
assert.ok(waitForNotification.includes('catch {'));
assert.ok(waitForNotification.includes('return false;'));
```

- [ ] **Step 2: Run the test to verify it fails**

Run:

```bash
cd extension
corepack yarn compile-tests
corepack yarn compile
corepack yarn unit-test --run out/test/e2eLaunchProfile.test.js
```

Expected: FAIL because `notification.getMessage()` currently escapes the wait callback.

- [ ] **Step 3: Retry from a fresh notification list**

Wrap the whole wait callback:

```ts
return await VSBrowser.instance.driver.wait(async () => {
    try {
        const notifications = await new Workbench().getNotifications();
        for (const notification of notifications) {
            const message = await notification.getMessage();
            if (message.includes(expectedText)) {
                return notification;
            }
        }
    }
    catch {
        // VS Code can replace notification elements while Selenium reads them.
        // Return false so WebDriver reacquires the current list on the next poll.
    }

    return false;
}, timeoutMs, `Timed out waiting for notification containing '${expectedText}'.`);
```

- [ ] **Step 4: Run the focused test**

Run the command from Step 2.

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add extension/src/test-e2e/helpers/vscode.ts extension/src/test/e2eLaunchProfile.test.ts
git commit -m "Retry replaced VS Code notifications" -m "Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>" -m "Copilot-Session: 3d108969-ce19-4473-9da0-9b5ff1e52f01"
```

### Task 7: Verify the durable AppHost-tree gate

**Consumed by:** Task 8

**Files:**
- Verify: `extension/src/test-e2e/appHostTree.e2e.test.ts`
- Verify: `extension/src/test-e2e/helpers/fixtures.ts`
- Verify: `extension/src/test/e2eLaunchProfile.test.ts`

- [ ] **Step 1: Run the unit contract**

Run:

```bash
cd extension
corepack yarn compile-tests
corepack yarn compile
corepack yarn unit-test --run out/test/e2eLaunchProfile.test.js --grep "durable AppHost discovery gates"
```

Expected: PASS, proving the branch waits for both `ps` and `ls` request markers before asserting the running AppHost.

- [ ] **Step 2: Review the final gate ordering**

Run:

```bash
git diff upstream/main...HEAD -- extension/src/test-e2e/appHostTree.e2e.test.ts extension/src/test-e2e/helpers/fixtures.ts extension/src/test/e2eLaunchProfile.test.ts
```

Expected: the `ps` snapshot remains gated until the running AppHost is observed, the `ls` candidate is released afterward, and both releases remain in `finally`.

### Task 8: Validate the integrated change

**Consumed by:** nothing

**Files:**
- Verify all files listed above

- [ ] **Step 1: Run extension lint and focused unit tests**

Run:

```bash
cd extension
corepack yarn lint
corepack yarn compile-e2e
corepack yarn compile-tests
corepack yarn compile
corepack yarn unit-test \
  --run out/test/e2eMochaReporter.test.js \
  --run out/test/e2eShardMatrix.test.js \
  --run out/test/e2eLaunchProfile.test.js
```

Expected: PASS.

- [ ] **Step 2: Run the .NET workflow and dashboard tests**

Run:

```bash
dotnet test --project tests/Infrastructure.Tests/Infrastructure.Tests.csproj --no-launch-profile -- --filter-class "*.ExtensionE2eWorkflowTests" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
dotnet test --project tests/Aspire.Hosting.Tests/Aspire.Hosting.Tests.csproj --no-launch-profile -- --filter-method "*.WatchResources_ResourceHasCommands_CommandsSentWithResponse" --filter-not-trait "quarantined=true" --filter-not-trait "outerloop=true"
```

Expected: PASS.

- [ ] **Step 3: Verify the exact matrix state**

Run:

```bash
git grep -n -E 'allowFailure|disabledIssue|advisoryIssue|ASPIRE_EXTENSION_E2E_(ALLOW_TEST_FAILURE|ADVISORY_ISSUE)' -- .github/workflows/extension-e2e-tests.yml extension/scripts extension/src/test tests/Infrastructure.Tests/Pipelines
```

Expected: no legacy field or environment variable in workflow/runtime code; legacy names appear only in negative contract tests. The workflow has exactly four `advisoryIssue` rows and the tests contain the matching allowlist.

- [ ] **Step 4: Confirm no unrelated files changed**

Run:

```bash
git status --short
git diff --check
```

Expected: only planned files plus the user’s pre-existing untracked files; no whitespace errors.

- [ ] **Step 5: Record hosted follow-up**

The four advisory rows remain explicit until hosted GitHub Actions runs prove their platform-specific fixes. Remove each `advisoryIssue` entry and matching allowlist entry after repeated green runs. When the map becomes empty, delete `ASPIRE_EXTENSION_E2E_ADVISORY_ISSUE` and the advisory branch so all E2E test failures block directly.
