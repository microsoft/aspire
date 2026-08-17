import * as assert from 'assert';
import { findResource, getCommandInvocationCount, getTaskProcessEventCount, waitForCommandOutcome, waitForHttpText, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResourceState, waitForTaskProcessEvent, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, reloadWorkspaceForE2E, runE2eTeardown, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire Azure Functions E2E', function () {
    this.timeout(600000);

    teardown(async () => {
        if (!shouldRunAzureFunctionsE2E()) {
            return;
        }

        await runE2eTeardown([
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
        const taskSequenceBeforeRun = getTaskProcessEventCount();
        const runInvocationBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 60000, runInvocationBefore);

        const runningState = await waitForResourceState('e2e-functions', ['Running'], 300000);
        const functionsResource = findResource(runningState.state, 'e2e-functions');
        assert.ok(functionsResource, 'Expected the HTTPS Azure Functions resource in extension state.');
        const httpsUrl = functionsResource.urls?.find(url => new URL(url.url).protocol === 'https:')?.url;
        assert.ok(httpsUrl, 'Expected the Azure Functions resource to expose an HTTPS URL.');
        await waitForHttpText(new URL('/api/https-proof', httpsUrl).toString(), 'Aspire HTTPS Functions E2E', 180000);

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
    });
});

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
