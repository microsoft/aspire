import * as assert from 'assert';
import * as path from 'path';
import { findResource, waitForCommandOutcome, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResourceState, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreWorkspaceCliPath, runE2eTeardown, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { invokeLanguageModelTool, prepareLanguageModelToolInvocation } from './helpers/languageModelTools';
import { getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

interface ResourceDebugToolResult {
    tool: 'aspire_resource_debug';
    success: boolean;
    outcome: string;
    appHost: string;
    resourceName: string;
    requestedStrategy: 'auto' | 'attach';
    effectiveStrategy: 'attach' | 'none';
    controller: 'editor' | 'none';
    provider?: 'dotnet' | 'go';
    debuggerExtensions?: Array<{ id: string; label: string }>;
}

const resourceDebugToolName = 'aspire_resource_debug';

// VS Code does not expose its telemetry transport to an Extension Host test, and the E2E bridge
// intentionally persists only bounded tool results. resourceDebugService.test.ts asserts the exact
// languageModelTool telemetry payload; this suite proves that source invokes the real registered tool.
suite('Aspire resource debug language model tool E2E', function () {
    this.timeout(360000);

    teardown(async () => {
        await runE2eTeardown([
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoRunningAppHost(),
            () => restoreWorkspaceCliPath(),
        ], 'Resource debug language model tool E2E teardown failed.');
    });

    test('returns bounded results for invalid, additional, and unknown selectors after generic confirmation', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const relativeAppHostPath = toWorkspaceRelativePath(appHostPath);

        const unresolved = await prepareLanguageModelToolInvocation(resourceDebugToolName, {
            appHostPath: 'missing/AppHost.csproj',
            resourceName: 'e2e-worker',
        });
        assert.deepStrictEqual(unresolved, {
            invocationMessage: 'Attaching debugger to the requested Aspire resource...',
            confirmationTitle: 'Attach debugger to Aspire resource',
            confirmationMessage: 'Attach the debugger to the requested Aspire resource?',
        });

        const cases: Array<{ input: Record<string, unknown>; outcome: string }> = [
            {
                input: {
                    appHostPath: relativeAppHostPath,
                    resourceName: ' ',
                },
                outcome: 'invalidInput',
            },
            {
                input: {
                    appHostPath: relativeAppHostPath,
                    resourceName: 'e2e-worker',
                    unexpected: 'value',
                },
                outcome: 'invalidInput',
            },
            {
                input: {
                    appHostPath: 'missing/AppHost.csproj',
                    resourceName: 'e2e-worker',
                },
                outcome: 'unknownAppHost',
            },
        ];

        for (const testCase of cases) {
            const invocation = await invokeLanguageModelTool<ResourceDebugToolResult>(
                resourceDebugToolName,
                testCase.input,
                { expectedConfirmations: 1 });

            assert.deepStrictEqual(invocation.dialogs[0], {
                message: 'Attach debugger to Aspire resource',
                details: 'Attach the debugger to the requested Aspire resource?',
            });
            assert.strictEqual(invocation.results.length, 1);
            assert.strictEqual(invocation.results[0].outcome, testCase.outcome);
            assertSafeResourceDebugResult(invocation.results[0]);
        }
    });

    test('requires explicit confirmation and returns safe running-resource outcomes', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const relativeAppHostPath = toWorkspaceRelativePath(appHostPath);

        const runBefore = await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        assert.ok(runBefore.startedObserved);
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 120000);
        const running = await waitForResourceState('e2e-worker', ['Running'], 180000);
        const worker = findResource(running.state, 'e2e-worker');
        assert.ok(worker);

        const prepared = await prepareLanguageModelToolInvocation(resourceDebugToolName, {
            appHostPath: relativeAppHostPath,
            resourceName: worker.name,
        });
        assert.deepStrictEqual(prepared, {
            invocationMessage: `Attaching debugger to Aspire resource ${worker.name}...`,
            confirmationTitle: 'Attach debugger to Aspire resource',
            confirmationMessage: `Attach the debugger to resource ${worker.name} from Aspire AppHost ${relativeAppHostPath}?`,
        });

        const invocation = await invokeLanguageModelTool<ResourceDebugToolResult>(
            resourceDebugToolName,
            {
                appHostPath: relativeAppHostPath,
                resourceName: worker.name,
            },
            { expectedConfirmations: 1, screenshotName: 'resource-debug-confirmation' });

        assert.deepStrictEqual(invocation.dialogs[0], {
            message: 'Attach debugger to Aspire resource',
            details: `Attach the debugger to resource ${worker.name} from Aspire AppHost ${relativeAppHostPath}?`,
        });
        assert.deepStrictEqual(invocation.results, [{
            tool: resourceDebugToolName,
            success: false,
            outcome: 'debuggerExtensionMissing',
            appHost: relativeAppHostPath,
            resourceName: worker.name,
            requestedStrategy: 'auto',
            effectiveStrategy: 'none',
            controller: 'none',
            debuggerExtensions: [{ id: 'ms-dotnettools.csharp', label: 'C#' }],
        }]);
        assertSafeResourceDebugResult(invocation.results[0]);

        const missingResource = await invokeLanguageModelTool<ResourceDebugToolResult>(
            resourceDebugToolName,
            {
                appHostPath: relativeAppHostPath,
                resourceName: 'missing-resource',
            },
            { expectedConfirmations: 1 });
        assert.deepStrictEqual(missingResource.results, [{
            tool: resourceDebugToolName,
            success: false,
            outcome: 'resourceNotFound',
            appHost: relativeAppHostPath,
            resourceName: 'missing-resource',
            requestedStrategy: 'auto',
            effectiveStrategy: 'none',
            controller: 'none',
        }]);
        assertSafeResourceDebugResult(missingResource.results[0]);

        const unsupportedResource = await invokeLanguageModelTool<ResourceDebugToolResult>(
            resourceDebugToolName,
            {
                appHostPath: relativeAppHostPath,
                resourceName: 'e2e-no-commands',
            },
            { expectedConfirmations: 1 });
        assert.deepStrictEqual(unsupportedResource.results, [{
            tool: resourceDebugToolName,
            success: false,
            outcome: 'unsupportedResource',
            appHost: relativeAppHostPath,
            resourceName: 'e2e-no-commands',
            requestedStrategy: 'auto',
            effectiveStrategy: 'none',
            controller: 'none',
        }]);
        assertSafeResourceDebugResult(unsupportedResource.results[0]);
    });

    test('cancels through the VS Code invocation token and reports a stopped resource without invoking a debugger', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const relativeAppHostPath = toWorkspaceRelativePath(appHostPath);

        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 120000);
        const running = await waitForResourceState('e2e-worker', ['Running'], 180000);
        const worker = findResource(running.state, 'e2e-worker');
        assert.ok(worker);

        const cancelled = await invokeLanguageModelTool<ResourceDebugToolResult>(
            resourceDebugToolName,
            {
                appHostPath: relativeAppHostPath,
                resourceName: worker.name,
            },
            { cancelAfterMs: 0, expectedConfirmations: 1 });
        assert.strictEqual(cancelled.cancelled, true);
        assert.deepStrictEqual(cancelled.results, []);
        assert.ok(cancelled.dialogs.length <= 1);
        if (cancelled.dialogs.length === 1) {
            assert.deepStrictEqual(cancelled.dialogs[0], {
                message: 'Attach debugger to Aspire resource',
                details: `Attach the debugger to resource ${worker.name} from Aspire AppHost ${relativeAppHostPath}?`,
            });
        }

        await executeE2eControlCommand({ name: 'stopResource', appHostPath, resourceName: worker.name });
        await waitForResourceState(worker.name, ['Exited', 'Finished', 'Stopped'], 90000);

        const stopped = await invokeLanguageModelTool<ResourceDebugToolResult>(
            resourceDebugToolName,
            {
                appHostPath: relativeAppHostPath,
                resourceName: worker.name,
                strategy: 'attach',
            },
            { expectedConfirmations: 1 });
        assert.strictEqual(stopped.results.length, 1);
        assert.strictEqual(stopped.results[0].outcome, 'resourceNotRunning');
        assertSafeResourceDebugResult(stopped.results[0]);
    });
});

function toWorkspaceRelativePath(filePath: string): string {
    const relativePath = path.relative(getWorkspaceRoot(), filePath);
    assert.ok(relativePath.length > 0 && !relativePath.startsWith('..') && !path.isAbsolute(relativePath));
    return relativePath.split(path.sep).join('/');
}

function assertSafeResourceDebugResult(result: ResourceDebugToolResult): void {
    const serialized = JSON.stringify(result);
    assert.deepStrictEqual(JSON.parse(serialized), result);
    assert.ok(!path.isAbsolute(result.appHost));
    assert.doesNotMatch(serialized, /(?:pid|process|configuration|arguments?|args|environment|env|secret|token|executable)|https?:\/\/|\/(?:Users|private|var|tmp)\b/i);
}
