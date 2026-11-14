import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { findResource, getCommandInvocationCount, isSamePath, waitForCommandOutcome, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResourceState, waitForSelectedWorkspaceAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, getGeneratedProjectRoot, removeGeneratedProject, restoreWorkspaceAppHostConfig, restoreWorkspaceCliPath, runE2eTeardown, stopAppHostIfRunning, stopPrimaryAppHostIfRunning, writeFileWithRetry, writeWorkspaceAppHostConfigForPath } from './helpers/fixtures';
import { invokeLanguageModelTool, prepareLanguageModelToolInvocation } from './helpers/languageModelTools';
import { getCliPath, getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { assertSafeResourceDebugResult, type ResourceDebugToolResult } from './helpers/resourceDebugResult';
import { openAspireView } from './helpers/vscode';
import { runProcess } from './helpers/process';
import type { AspireExtensionE2ECodeLensProbeResult } from '../types/extensionApi';

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
    resumeInterruptedInspectionResponses: Array<{
        command?: string;
        classification: { reason: 'pendingAtProofContinue' | 'sentAfterProofContinue'; proofContinueSeq: number };
    }>;
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
        // The first discovery wait can reopen the E2E workspace folder, which reloads the window and
        // hides the Aspire view. Reopen it so the extension keeps refreshing AppHost and resource state.
        await openAspireView();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const relativeAppHostPath = toWorkspaceRelativePath(appHostPath);

        const unresolved = await prepareLanguageModelToolInvocation(resourceDebugToolName, {
            appHostPath: 'missing/AppHost.csproj',
            resourceName: 'e2e-worker',
        });
        assert.deepStrictEqual(unresolved, {
            registered: true,
            supportsPreparation: true,
            invocationMessage: 'Attaching debugger to the requested Aspire resource...',
            confirmationTitle: 'Attach debugger to Aspire resource',
            confirmationMessage: 'Attach the debugger to the requested Aspire resource?',
        });

        const cases: Array<{ input: Record<string, unknown>; outcome: string; expectedResourceName: string }> = [
            {
                input: {
                    appHostPath: relativeAppHostPath,
                    resourceName: ' ',
                },
                outcome: 'invalidInput',
                expectedResourceName: '',
            },
            {
                input: {
                    appHostPath: relativeAppHostPath,
                    resourceName: 'e2e-worker',
                    unexpected: 'value',
                },
                outcome: 'invalidInput',
                expectedResourceName: '',
            },
            {
                input: {
                    appHostPath: 'missing/AppHost.csproj',
                    resourceName: 'e2e-worker',
                },
                outcome: 'unknownAppHost',
                expectedResourceName: 'e2e-worker',
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
            assertSafeResourceDebugResult(invocation.results[0], testCase.expectedResourceName);
        }
    });

    negativePathTest('requires explicit confirmation and returns safe running-resource outcomes', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        await openAspireView();
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
            registered: true,
            supportsPreparation: true,
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
            assertSafeResourceDebugResult(invocation.results[0], worker.name);
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
        assertSafeResourceDebugResult(missingResource.results[0], 'missing-resource');

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
        assertSafeResourceDebugResult(unsupportedResource.results[0], 'e2e-no-commands');
    });

    negativePathTest('cancels through the VS Code invocation token and reports a stopped resource without invoking a debugger', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        await openAspireView();
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

        const stopInvocationBefore = getCommandInvocationCount('aspire-vscode.stopResource');
        await executeE2eControlCommand({ name: 'stopResource', appHostPath, resourceName: worker.name }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.stopResource', 'success', 60000, stopInvocationBefore);
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
        assertSafeResourceDebugResult(stopped.results[0], worker.name);
    });

    test('attaches packaged .NET and Go debuggers, hits breakpoints, detaches, and tears down', async function () {
        this.timeout(900000);
        if (!resourceDebugPrerequisitesInstalled) {
            this.skip();
        }

        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        await openAspireView();
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
        const customOutputWorkerRunning = await waitForResourceState('e2e-custom-output-worker', ['Running'], 180000);
        const customOutputWorker = findResource(customOutputWorkerRunning.state, 'e2e-custom-output-worker');
        assert.ok(customOutputWorker);
        const goRunning = await waitForResourceState('e2e-go', ['Running'], 180000);
        const go = findResource(goRunning.state, 'e2e-go');
        assert.ok(go);

        const scenarios = [
            {
                resourceName: worker.name,
                displayName: worker.displayName,
                debugType: 'coreclr' as const,
                sourcePath: path.join(getWorkspaceRoot(), 'AspireE2E.Worker', 'Program.cs'),
                marker: 'app.MapGet("/", () => "ok");',
                expectedResponse: 'ok',
            },
            {
                resourceName: customOutputWorker.name,
                displayName: customOutputWorker.displayName,
                debugType: 'coreclr' as const,
                sourcePath: path.join(getWorkspaceRoot(), 'AspireE2E.CustomOutputWorker', 'Program.cs'),
                marker: 'app.MapGet("/", () => "custom-ok");',
                expectedResponse: 'custom-ok',
            },
            {
                resourceName: go.name,
                displayName: go.displayName,
                debugType: 'go' as const,
                sourcePath: path.join(getWorkspaceRoot(), 'AspireE2E.Go', 'main.go'),
                marker: 'message := "go-ok"',
                expectedResponse: 'go-ok',
            },
        ];

        for (const scenario of scenarios) {
            assert.ok(scenario.displayName);
            assert.notStrictEqual(scenario.resourceName, scenario.displayName);
            const status = await invokeLanguageModelTool<{ success: boolean; outcome: string }>(
                'aspire_debug_session_status',
                { appHostPath: toWorkspaceRelativePath(appHostPath), resourceName: scenario.displayName },
                { expectedConfirmations: 0 });
            assert.strictEqual(status.results.length, 1);
            assert.strictEqual(status.results[0].success, true);

            const proof = (await executeE2eControlCommand({
                name: 'proveAttachedResourceDebugging',
                appHostPath,
                resourceName: scenario.displayName,
                sourcePath: scenario.sourcePath,
                breakpointLine: findBreakpointLine(scenario.sourcePath, scenario.marker),
                expectedDebugType: scenario.debugType,
                expectedResponse: scenario.expectedResponse,
                timeoutMs: 300000,
            }, { timeoutMs: 360000 })).result as AttachedResourceDebugProof;

            assert.strictEqual(proof.proof, 'aspire-resource-attach-breakpoint-detach');
            assert.strictEqual(proof.toolPayload.outcome, 'started');
            assert.strictEqual(proof.toolPayload.provider, scenario.debugType === 'coreclr' ? 'dotnet' : 'go');
            assertSafeResourceDebugResult(proof.toolPayload, scenario.displayName);
            assert.strictEqual(proof.resourceName, scenario.displayName);
            assert.strictEqual(proof.debugType, scenario.debugType);
            assert.strictEqual(proof.breakpoint.matchingStackFrame.line, proof.breakpoint.line);
            const breakpointSourcePath = proof.breakpoint.matchingStackFrame.source?.path;
            assert.ok(breakpointSourcePath);
            assert.ok(isSamePath(breakpointSourcePath, scenario.sourcePath));
            assert.ok(proof.attachRequests.length > 0);
            assert.ok(proof.breakpointResponses.some(response => response.success === true));
            assert.deepStrictEqual(proof.debugAdapterResponses, []);
            logResumeInterruptedInspectionResponses(proof);
            assert.strictEqual(proof.resourceResponseAfterDetach, scenario.expectedResponse);
            assert.strictEqual(proof.sessionTerminated, true);
        }

        await stopPrimaryAppHostIfRunning();
        await waitForNoDebugSessions(120000);
        await waitForNoRunningAppHost(120000, appHostPath);
    });

    test('attaches to a .NET resource launched with Run Without Debugging', async function () {
        if (!resourceDebugPrerequisitesInstalled) {
            this.skip();
        }

        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        await openAspireView();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const runBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 180000, runBefore);
        const running = await waitForResourceState('e2e-worker', ['Running'], 180000);
        const worker = findResource(running.state, 'e2e-worker');
        assert.ok(worker);

        const invocation = await invokeLanguageModelTool<ResourceDebugToolResult>(resourceDebugToolName, {
            appHostPath: toWorkspaceRelativePath(appHostPath),
            resourceName: worker.name,
            strategy: 'attach',
        }, { expectedConfirmations: 1 });
        assert.strictEqual(invocation.results.length, 1);
        const result = invocation.results[0];
        assert.strictEqual(result.outcome, 'started');
        assertSafeResourceDebugResult(result, worker.name);

        await stopPrimaryAppHostIfRunning();
        await waitForNoDebugSessions(120000);
        await waitForNoRunningAppHost(120000, appHostPath);
    });

    test('attaches to a Go resource launched with Run Without Debugging, hits a breakpoint, and detaches', async function () {
        this.timeout(600000);
        if (!resourceDebugPrerequisitesInstalled) {
            this.skip();
        }

        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        await openAspireView();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const runBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 180000, runBefore);
        const running = await waitForResourceState('e2e-go', ['Running'], 180000);
        const go = findResource(running.state, 'e2e-go');
        assert.ok(go);
        const sourcePath = path.join(getWorkspaceRoot(), 'AspireE2E.Go', 'main.go');

        const proof = (await executeE2eControlCommand({
            name: 'proveAttachedResourceDebugging',
            appHostPath,
            resourceName: go.name,
            sourcePath,
            breakpointLine: findBreakpointLine(sourcePath, 'message := "go-ok"'),
            expectedDebugType: 'go',
            expectedResponse: 'go-ok',
            timeoutMs: 300000,
        }, { timeoutMs: 360000 })).result as AttachedResourceDebugProof;

        assertSafeResourceDebugResult(proof.toolPayload, go.name);
        assert.deepStrictEqual(proof.debugAdapterResponses, []);
        logResumeInterruptedInspectionResponses(proof);

        await stopPrimaryAppHostIfRunning();
        await waitForNoDebugSessions(120000);
        await waitForNoRunningAppHost(120000, appHostPath);
    });

    test('hits source breakpoints in a Go AppHost and its Go resource using packaged Delve', async function () {
        this.timeout(900000);
        if (!resourceDebugPrerequisitesInstalled) {
            this.skip();
        }

        const sdkVersion = process.env.ASPIRE_EXTENSION_E2E_APPHOST_SDK_VERSION;
        assert.ok(sdkVersion, 'The runner must provide the built AppHost SDK version.');
        const projectName = 'AspireE2E.GoAppHost';
        const projectRoot = getGeneratedProjectRoot(projectName);
        const appHostPath = path.join(projectRoot, 'apphost.go');
        const resourceSourcePath = path.join(getWorkspaceRoot(), 'AspireE2E.Go', 'main.go');
        fs.mkdirSync(projectRoot, { recursive: true });
        writeFileWithRetry(path.join(projectRoot, 'go.mod'), `module apphost

go 1.26

require apphost/modules/aspire v0.0.0
replace apphost/modules/aspire => ./.aspire/modules
`);
        writeFileWithRetry(path.join(projectRoot, 'aspire.config.json'), JSON.stringify({
            appHost: { path: 'apphost.go', language: 'go' },
            sdk: { version: sdkVersion },
            packages: { 'Aspire.Hosting.Go': sdkVersion },
        }, undefined, 2));
        writeFileWithRetry(appHostPath, `package main

import (
    "log"
    "apphost/modules/aspire"
)

func main() {
    builder, err := aspire.CreateBuilder()
    if err != nil {
        log.Fatal(aspire.FormatError(err))
    }
    api := builder.AddGoApp("go-api", "../AspireE2E.Go").
        WithHttpEndpoint(&aspire.WithHttpEndpointOptions{Env: aspire.StringPtr("PORT")})
    if err := api.Err(); err != nil {
        log.Fatal(aspire.FormatError(err))
    }
    app, err := builder.Build()
    if err != nil {
        log.Fatal(aspire.FormatError(err))
    }
    if err := app.Run(); err != nil {
        log.Fatal(aspire.FormatError(err))
    }
}
`);
        try {
            // Reuse the resource-debug shard's pinned Go extension, Go toolchain and preinstalled
            // Delve. The offline extension host must never depend on golang.go downloading tools.
            await runProcess(getCliPath(), ['restore', '--nologo'], { cwd: projectRoot, timeoutMs: 300000 });
            writeWorkspaceAppHostConfigForPath(appHostPath);
            await openAspireView();
            await executeE2eControlCommand({ name: 'refreshAppHosts' });
            await waitForSelectedWorkspaceAppHost(appHostPath, 180000);
            await openAspireView();
            const capabilities = (await executeE2eControlCommand({ name: 'getSupportedCapabilities' })).result as string[];
            assert.ok(capabilities.includes('go-apphost.v1'), `Missing Go AppHost capability: ${JSON.stringify(capabilities)}`);

            const proof = (await executeE2eControlCommand({
                name: 'proveAppHostAndResourceDebugging',
                appHostPath,
                resourceName: 'go-api',
                appHostSourcePath: appHostPath,
                appHostBreakpointLine: findBreakpointLine(appHostPath, 'api := builder.AddGoApp'),
                resourceSourcePath,
                resourceBreakpointLine: findBreakpointLine(resourceSourcePath, 'message := "go-ok"'),
                timeoutMs: 480000,
            }, { timeoutMs: 540000 })).result as {
                proof: string;
                appHostBreakpoint: { matchingStackFrame: { source?: { path?: string } } };
                resourceBreakpoint: { matchingStackFrame: { source?: { path?: string } } };
                appHostCodeLenses: AspireExtensionE2ECodeLensProbeResult;
                debugSessions: Array<{ type: string }>;
            };
            assert.strictEqual(proof.proof, 'aspire-apphost-and-resource-debug-breakpoints-hit');
            assert.ok(isSamePath(proof.appHostBreakpoint.matchingStackFrame.source?.path ?? '', appHostPath));
            assert.ok(isSamePath(proof.resourceBreakpoint.matchingStackFrame.source?.path ?? '', resourceSourcePath));
            assert.strictEqual(proof.debugSessions.filter(session => session.type === 'go').length, 2);
            assert.strictEqual(proof.appHostCodeLenses.languageId, 'go');
            assert.ok(proof.appHostCodeLenses.commandTitles.some(title => title.includes('Dashboard')), 'The Go AppHost must expose its dashboard action.');
            assert.ok(proof.appHostCodeLenses.commandTitles.some(title => title.includes('Running')), 'The Go resource must expose its running state.');
            await waitForNoDebugSessions(120000);
            await waitForNoRunningAppHost(120000, appHostPath);
        }
        finally {
            await runE2eTeardown([
                () => stopAppHostIfRunning(appHostPath),
                () => restoreWorkspaceAppHostConfig(),
                () => removeGeneratedProject(projectName),
            ], 'Go AppHost debug E2E teardown failed.');
        }
    });
});

suite('Aspire resource debug source paths', function () {
    test('matches Windows drive-letter casing variants', function () {
        if (process.platform !== 'win32' || !/^[a-z]:\\/i.test(__filename)) {
            this.skip();
        }

        const uppercaseDrivePath = __filename.charAt(0).toUpperCase() + __filename.slice(1);
        const lowercaseDrivePath = __filename.charAt(0).toLowerCase() + __filename.slice(1);
        assert.strictEqual(isSamePath(uppercaseDrivePath, lowercaseDrivePath), true);
    });
});

// The attach proof exempts failed inspection requests interrupted by its own resume (#21777). Log them so a
// passing run still shows that the exemption fired and why each response was classified.
function logResumeInterruptedInspectionResponses(proof: AttachedResourceDebugProof): void {
    if (proof.resumeInterruptedInspectionResponses.length > 0) {
        console.log(`Resource debug E2E ${proof.resourceName}: ignored failed inspection responses interrupted by the proof's continue: ${JSON.stringify(proof.resumeInterruptedInspectionResponses)}`);
    }
}

function toWorkspaceRelativePath(filePath: string): string {
    const relativePath = path.relative(getWorkspaceRoot(), filePath);
    assert.ok(relativePath.length > 0 && !relativePath.startsWith('..') && !path.isAbsolute(relativePath));
    return relativePath.split(path.sep).join('/');
}

function findBreakpointLine(sourcePath: string, marker: string): number {
    const lines = fs.readFileSync(sourcePath, 'utf8').split(/\r?\n/);
    const index = lines.findIndex(line => line.includes(marker));
    if (index < 0) {
        throw new Error(`Could not find '${marker}' in ${sourcePath} to place a breakpoint on.`);
    }

    return index;
}
