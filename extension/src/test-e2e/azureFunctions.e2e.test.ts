import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { findResource, getCommandInvocationCount, getTaskProcessEventCount, waitForCommandOutcome, waitForExtensionState, waitForHttpText, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResourceState, waitForTaskProcessEvent, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, reloadWorkspaceForE2E, runE2eTeardown, stopPrimaryAppHostIfRunning, waitForKnownProcessExit } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { BottomBarPanel } from './helpers/extester';
import { openAspireView } from './helpers/vscode';

suite('Aspire Azure Functions E2E', function () {
    this.timeout(600000);

    teardown(async () => {
        if (!shouldRunAzureFunctionsE2E()) {
            return;
        }

        await runE2eTeardown([
            () => executeE2eControlCommand({ name: 'clearBreakpoints' }),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
        ], 'Azure Functions E2E teardown failed.');
    });

    test('runs and stops an HTTPS Functions resource through the real VS Code task in NoDebug mode', async function () {
        if (!shouldRunAzureFunctionsE2E()) {
            this.skip();
        }

        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();
        await reloadWorkspaceForE2E();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();
        // Reloading the window restarts the extension host and returns VS Code to Explorer.
        // Reopen the Aspire view so its visibility-driven runtime state polling observes the AppHost.
        await openAspireView();

        const appHostPath = getPrimaryAppHostProjectPath();
        const buildsBeforeRun = readFunctionsBuilds().length;
        const taskSequenceBeforeRun = getTaskProcessEventCount();
        const runInvocationBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 60000, runInvocationBefore);

        // Running and endpoint activation are separate updates; the backchannel omits inactive URLs.
        // Wait for both in the same snapshot before probing HTTPS (https://github.com/microsoft/aspire/issues/19639).
        const runningState = await waitForFunctionsWithHttps(taskSequenceBeforeRun, 300000);
        const functionsResource = findResource(runningState.state, 'e2e-functions');
        assert.ok(functionsResource, 'Expected the HTTPS Azure Functions resource in extension state.');
        const httpsUrl = functionsResource.urls?.find(url => new URL(url.url).protocol === 'https:')?.url;
        assert.ok(httpsUrl, 'Expected the Azure Functions resource to expose an HTTPS URL.');
        await waitForHttpText(new URL('/api/https-proof', httpsUrl).toString(), 'Aspire HTTPS Functions E2E', 180000);
        if (usesExternalBuildContract()) {
            assert.strictEqual(functionsResource.launchConfigurationType, 'azure-functions-with-external-build.v1');
            await assertBuildContext(httpsUrl, buildsBeforeRun);
        }

        const taskStarted = await waitForTaskProcessEvent(
            event => event.state === 'started' && isAzureFunctionsHostTask(event),
            'Azure Functions host task to start',
            60000,
            taskSequenceBeforeRun);
        assert.ok(taskStarted.processId && taskStarted.processId > 0, 'Expected VS Code to report the Functions host task process ID.');

        const stopInvocationBefore = getCommandInvocationCount('aspire-vscode.stopResource');
        await executeE2eControlCommand({ name: 'stopResource', appHostPath, resourceName: functionsResource.name });
        await waitForCommandOutcome('aspire-vscode.stopResource', 'success', 60000, stopInvocationBefore);
        await waitForResourceState(functionsResource.name, ['Exited', 'Finished', 'Stopped'], 120000);

        const taskEnded = await waitForTaskProcessEvent(
            event => event.state === 'ended' && event.executionId === taskStarted.executionId,
            'Azure Functions host task to end after stopping the resource',
            120000,
            taskStarted.sequence);
        assert.strictEqual(taskEnded.taskName, taskStarted.taskName);
        await waitForKnownProcessExit(taskStarted.processId!, 'Azure Functions Core Tools task', 120000);

        if (usesExternalBuildContract()) {
            const buildsBeforeRestart = readFunctionsBuilds().length;
            const restartSequence = getTaskProcessEventCount();
            await executeE2eControlCommand({ name: 'startResource', appHostPath, resourceName: functionsResource.name });
            const restarted = await waitForFunctionsWithHttps(restartSequence, 180000);
            const restartedResource = findResource(restarted.state, 'e2e-functions')!;
            const restartedUrl = restartedResource.urls?.find(url => new URL(url.url).protocol === 'https:')?.url;
            assert.ok(restartedUrl);
            await waitForHttpText(new URL('/api/https-proof', restartedUrl).toString(), 'Aspire HTTPS Functions E2E');
            assert.strictEqual(readFunctionsBuilds().length, buildsBeforeRestart, 'Starting prebuilt Functions again must not invoke an extension build.');
            const restartedTask = await waitForTaskProcessEvent(
                event => event.state === 'started' && isAzureFunctionsHostTask(event),
                'a fresh Functions task after starting the stopped resource', 60000, restartSequence);
            assert.notStrictEqual(restartedTask.executionId, taskStarted.executionId);
            await executeE2eControlCommand({ name: 'stopResource', appHostPath, resourceName: functionsResource.name });
            await waitForTaskProcessEvent(
                event => event.state === 'ended' && event.executionId === restartedTask.executionId,
                'the restarted Functions task to end', 120000, restartedTask.sequence);
            await waitForKnownProcessExit(restartedTask.processId!, 'restarted Azure Functions Core Tools task', 120000);
        }
    });

    test('attaches coreclr to the isolated worker, hits an HTTPS handler breakpoint and cleans up', async function () {
        if (!shouldRunAzureFunctionsE2E()) {
            this.skip();
        }

        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();
        const capabilities = (await executeE2eControlCommand({ name: 'getSupportedCapabilities' })).result as string[];
        assert.ok(capabilities.includes('azure-functions'));
        assert.ok(capabilities.includes('azure-functions-with-external-build.v1'));
        const appHostPath = getPrimaryAppHostProjectPath();
        const appHostSourcePath = path.join(path.dirname(appHostPath), 'AppHost.cs');
        const resourceSourcePath = path.join(getWorkspaceRoot(), 'AspireE2E.Functions', 'HttpsFunction.cs');
        const appHostBreakpointLine = findLine(appHostSourcePath, usesExternalBuildContract() ? 'builder.AddAzureFunctions(' : 'builder.AddAzureFunctionsProject(');
        const resourceBreakpointLine = findLine(resourceSourcePath, 'return new OkObjectResult("Aspire HTTPS Functions E2E");');
        const buildsBeforeRun = readFunctionsBuilds().length;
        const taskSequence = getTaskProcessEventCount();
        const status = await executeE2eControlCommand({
            name: 'proveAppHostAndResourceDebugging',
            appHostPath,
            resourceName: 'e2e-functions',
            appHostSourcePath,
            appHostBreakpointLine,
            resourceSourcePath,
            resourceBreakpointLine,
            resourceRequestPath: '/api/https-proof',
            continueExceptionStops: true,
            timeoutMs: 300000,
        }, { timeoutMs: 540000 });
        const proof = status.result as FunctionsDebugProof;
        assert.strictEqual(proof.proof, 'aspire-apphost-and-resource-debug-breakpoints-hit');
        const hit = proof.resourceBreakpoint;
        assert.strictEqual(hit.stoppedEvent.reason, 'breakpoint');
        assert.strictEqual(hit.stoppedEvent.sessionType, 'coreclr');
        assert.strictEqual(fs.realpathSync.native(hit.matchingStackFrame.source!.path!), fs.realpathSync.native(resourceSourcePath));
        assert.strictEqual(hit.matchingStackFrame.line, resourceBreakpointLine + 1);
        const workerSession = proof.debugSessions.find(session => session.id === hit.stoppedEvent.sessionId);
        assert.ok(workerSession);
        assert.strictEqual(workerSession.configuration.request, 'attach');
        assert.strictEqual(workerSession.configuration.resourceType, usesExternalBuildContract() ? 'azure-functions-with-external-build.v1' : 'azure-functions');
        const workerProcessId = Number(workerSession.configuration.processId);
        assert.ok(Number.isInteger(workerProcessId) && workerProcessId > 0);
        if (usesExternalBuildContract()) {
            assertSingleFunctionsBuild(buildsBeforeRun);
        }

        const taskStarted = await waitForTaskProcessEvent(
            event => event.state === 'started' && isAzureFunctionsHostTask(event),
            'the debug Functions Core Tools task to start', 30000, taskSequence);
        assert.notStrictEqual(workerProcessId, taskStarted.processId, 'coreclr must attach to the worker, not the task shell or Core Tools host.');
        await waitForTaskProcessEvent(
            event => event.state === 'ended' && event.executionId === taskStarted.executionId,
            'the debug Functions task to end when debugging stops', 120000, taskStarted.sequence);
        await waitForKnownProcessExit(workerProcessId, 'debugged Functions worker', 120000);
        await waitForKnownProcessExit(taskStarted.processId!, 'debugged Functions Core Tools task', 120000);
        await waitForNoDebugSessions();
        await waitForNoRunningAppHost();
    });
});

interface FunctionsDebugProof {
    proof: string;
    resourceBreakpoint: {
        stoppedEvent: { sessionId: string; sessionType: string; reason: string };
        matchingStackFrame: { source?: { path?: string }; line: number };
    };
    debugSessions: Array<{
        id: string;
        configuration: { request?: string; resourceType?: string; processId?: string };
    }>;
}

async function waitForFunctionsWithHttps(afterTaskSequence: number, timeoutMs: number) {
    const snapshot = await waitForExtensionState(file => {
        const resource = findResource(file.state, 'e2e-functions');
        return resource?.state === 'FailedToStart'
            || file.taskProcessEvents.some(event => event.sequence > afterTaskSequence && event.state === 'ended' && isAzureFunctionsHostTask(event))
            || (resource?.state === 'Running' && resource.urls?.some(url => new URL(url.url).protocol === 'https:') === true);
    }, 'Functions task startup or HTTPS readiness', timeoutMs);
    const ended = snapshot.taskProcessEvents.find(event =>
        event.sequence > afterTaskSequence && event.state === 'ended' && isAzureFunctionsHostTask(event));
    if (ended || findResource(snapshot.state, 'e2e-functions')?.state === 'FailedToStart') {
        // A terminal startup failure cannot recover during this wait. Preserve its console
        // before teardown closes the task instead of hiding the cause behind an endpoint timeout.
        const terminal = await new BottomBarPanel().openTerminalView();
        const text = (await terminal.getText()).replaceAll(String.raw`Aspire E2E p@ss'\word`, '<fixture-password>');
        throw new Error(`Functions task failed before HTTPS readiness (exit ${ended?.exitCode ?? '<unknown>'}).\n${text}`);
    }

    return snapshot;
}

function usesExternalBuildContract(): boolean {
    return process.env.ASPIRE_EXTENSION_E2E_SKIP_CURRENT_CLI_REGRESSIONS !== 'true';
}

function readFunctionsBuilds(): string[] {
    const file = path.join(getWorkspaceRoot(), 'AspireE2E.Functions', 'build-invocations.txt');
    return fs.existsSync(file) ? fs.readFileSync(file, 'utf8').trim().split(/\r?\n/).filter(Boolean) : [];
}

function assertSingleFunctionsBuild(previousCount: number): string {
    const appHostConfigurationFile = path.join(path.dirname(getPrimaryAppHostProjectPath()), 'apphost-build-configuration.txt');
    assert.strictEqual(
        fs.readFileSync(appHostConfigurationFile, 'utf8'),
        'Release',
        'The running AppHost must report AssemblyConfiguration=Release, including when CLI global properties override the project default.');
    const builds = readFunctionsBuilds().slice(previousCount);
    assert.strictEqual(builds.length, 1, 'Expected exactly one coordinated Functions build, with no second extension build.');
    // The MSBuild probe writes "Release|custom|<absolute TargetPath>". '|' cannot
    // occur in a Windows path and the fixture never uses it in Unix path names.
    const [configuration, flavor, targetPath, ...extra] = builds[0].split('|');
    assert.deepStrictEqual([configuration, flavor, ...extra], ['Release', 'custom']);
    assert.match(targetPath.replaceAll('\\', '/'), /\/bin\/custom\/Release\/net10\.0\/AspireE2E\.Functions\.dll$/);
    assert.strictEqual(fs.existsSync(path.join(getWorkspaceRoot(), 'AspireE2E.Functions', 'bin', 'wrong')), false);
    return targetPath;
}

async function assertBuildContext(httpsUrl: string, previousCount: number): Promise<void> {
    // MSBuild can retain a lowercase Windows drive while the runtime reports its canonical casing.
    const targetPath = fs.realpathSync.native(assertSingleFunctionsBuild(previousCount));
    await waitForHttpText(
        new URL('/api/build-proof', httpsUrl).toString(),
        `Release|custom|<none>|runtime-value|${targetPath}|${path.dirname(targetPath)}`);
}

function findLine(filePath: string, marker: string): number {
    const line = fs.readFileSync(filePath, 'utf8').split(/\r?\n/).findIndex(value => value.includes(marker));
    assert.ok(line >= 0, `Expected breakpoint marker ${marker} in ${filePath}.`);
    return line;
}

function shouldRunAzureFunctionsE2E(): boolean {
    return process.env.ASPIRE_EXTENSION_E2E_ENABLE_AZURE_FUNCTIONS === 'true';
}

function isAzureFunctionsHostTask(event: { taskName: string; taskSource: string; taskDefinitionType: string }): boolean {
    // The Azure Functions extension contributes the literal `func` task type.
    // Dynamic types such as `func  <buildPath>` are rejected by VS Code 1.130 and later.
    return event.taskName === 'func: host start'
        && event.taskSource === 'func'
        && event.taskDefinitionType === 'func';
}
