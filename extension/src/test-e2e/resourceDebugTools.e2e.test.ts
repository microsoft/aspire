import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { findResource, waitForCommandOutcome, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResourceState, waitForWorkspaceAppHost } from './helpers/assertions';
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

interface AttachedResourceDebugProof {
    proof: 'aspire-resource-attach-breakpoint-detach';
    toolPayload: ResourceDebugToolResult;
    resourceName: string;
    debugType: 'coreclr' | 'go';
    breakpoint: {
        sourcePath: string;
        line: number;
        text: string;
        matchingStackFrame: {
            source?: { path?: string };
            line?: number;
        };
    };
    attachRequests: unknown[];
    breakpointResponses: Array<{ success?: boolean }>;
    debugAdapterResponses: unknown[];
    resourceResponseAfterDetach: string;
    sessionTerminated: boolean;
}

const resourceDebugToolName = 'aspire_resource_debug';
const resourceDebugPrerequisitesInstalled = process.env.ASPIRE_EXTENSION_E2E_ENABLE_RESOURCE_DEBUG === 'true';
const negativePathTest = resourceDebugPrerequisitesInstalled ? test.skip : test;

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

    negativePathTest('returns bounded results for invalid, additional, and unknown selectors after generic confirmation', async () => {
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

    negativePathTest('requires explicit confirmation and returns safe running-resource outcomes', async () => {
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

        if (!resourceDebugPrerequisitesInstalled) {
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
        }

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

    negativePathTest('cancels through the VS Code invocation token and reports a stopped resource without invoking a debugger', async () => {
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

    test('attaches packaged .NET and Go debuggers, hits breakpoints, detaches, and tears down', async function () {
        this.timeout(900000);
        if (!resourceDebugPrerequisitesInstalled) {
            this.skip();
        }

        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        const start = (await executeE2eControlCommand({
            name: 'runAspireCli',
            args: ['start', '--apphost', appHostPath, '--format', 'json', '--non-interactive', '--nologo'],
            workingDirectory: '.',
            timeoutMs: 180000,
            noExtensionVariables: true,
        }, { timeoutMs: 210000 })).result as { exitCode: number | null; stdout: string; stderr: string };
        assert.strictEqual(start.exitCode, 0, `aspire start failed.\nstdout:\n${start.stdout}\nstderr:\n${start.stderr}`);
        const workerRunning = await waitForResourceState('e2e-worker', ['Running'], 180000);
        const worker = findResource(workerRunning.state, 'e2e-worker');
        assert.ok(worker);
        const goRunning = await waitForResourceState('e2e-go', ['Running'], 180000);
        const go = findResource(goRunning.state, 'e2e-go');
        assert.ok(go);

        const scenarios = [
            {
                resourceName: worker.name,
                debugType: 'coreclr' as const,
                sourcePath: path.join(getWorkspaceRoot(), 'AspireE2E.Worker', 'Program.cs'),
                marker: 'app.MapGet("/", () => "ok");',
                expectedResponse: 'ok',
            },
            {
                resourceName: go.name,
                debugType: 'go' as const,
                sourcePath: path.join(getWorkspaceRoot(), 'AspireE2E.Go', 'main.go'),
                marker: 'message := "go-ok"',
                expectedResponse: 'go-ok',
            },
        ];

        for (const scenario of scenarios) {
            const proof = (await executeE2eControlCommand({
                name: 'proveAttachedResourceDebugging',
                appHostPath,
                resourceName: scenario.resourceName,
                sourcePath: scenario.sourcePath,
                breakpointLine: findBreakpointLine(scenario.sourcePath, scenario.marker),
                expectedDebugType: scenario.debugType,
                expectedResponse: scenario.expectedResponse,
                timeoutMs: 300000,
            }, { timeoutMs: 360000 })).result as AttachedResourceDebugProof;

            assert.strictEqual(proof.proof, 'aspire-resource-attach-breakpoint-detach');
            assert.strictEqual(proof.toolPayload.outcome, 'started');
            assert.strictEqual(proof.toolPayload.provider, scenario.debugType === 'coreclr' ? 'dotnet' : 'go');
            assertSafeResourceDebugResult(proof.toolPayload);
            assert.strictEqual(proof.resourceName, scenario.resourceName);
            assert.strictEqual(proof.debugType, scenario.debugType);
            assert.strictEqual(proof.breakpoint.matchingStackFrame.line, proof.breakpoint.line);
            assert.ok(isSamePath(proof.breakpoint.matchingStackFrame.source?.path, scenario.sourcePath));
            assert.ok(proof.attachRequests.length > 0);
            assert.ok(proof.breakpointResponses.some(response => response.success === true));
            assert.deepStrictEqual(proof.debugAdapterResponses, []);
            assert.strictEqual(proof.resourceResponseAfterDetach, scenario.expectedResponse);
            assert.strictEqual(proof.sessionTerminated, true);
        }

        await stopPrimaryAppHostIfRunning();
        await waitForNoDebugSessions(120000);
        await waitForNoRunningAppHost(120000, appHostPath);
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

function findBreakpointLine(sourcePath: string, marker: string): number {
    const lines = fs.readFileSync(sourcePath, 'utf8').split(/\r?\n/);
    const index = lines.findIndex(line => line.includes(marker));
    if (index < 0) {
        throw new Error(`Could not find '${marker}' in ${sourcePath} to place a breakpoint on.`);
    }

    return index;
}

function isSamePath(left: string | undefined, right: string): boolean {
    return left !== undefined && path.resolve(left) === path.resolve(right);
}
