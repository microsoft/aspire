import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, waitForCommandOutcome, waitForResourceState, waitForSelectedWorkspaceAppHost } from './helpers/assertions';
import { addIntegrationPackageToAppHost, createEmptyAppHostProject, executeE2eControlCommand, getGeneratedAppHostPath, getGeneratedProjectRoot, removeGeneratedProject, restoreWorkspaceAppHostConfig, runE2eTeardown, stopAppHostIfRunning, writeFileWithRetry, writeWorkspaceAppHostConfigForPath } from './helpers/fixtures';
import { getNotificationActionTitles, openAspireView, waitForEditorTitle, waitForNotificationMessage, waitForWorkbenchText } from './helpers/vscode';

suite('Debugger install hint E2E', function () {
    this.timeout(600000);

    const projectName = 'DebuggerInstallHintApp';
    const appHostPath = getGeneratedAppHostPath(projectName);

    teardown(async () => {
        await runE2eTeardown([
            () => stopAppHostIfRunning(appHostPath),
            () => restoreWorkspaceAppHostConfig(),
            () => removeGeneratedProject(projectName),
        ], 'Debugger install hint E2E teardown failed.');
    });

    test('shows a toast and warning CodeLens for a running Python resource on a clean host', async () => {
        await openAspireView();

        const debuggerExtensions = await executeE2eControlCommand({ name: 'getResourceDebuggerExtensions' });
        const installedDebuggerTypes = (debuggerExtensions.result as Array<{ resourceType: string }>).map(extension => extension.resourceType);
        assert.ok(!installedDebuggerTypes.includes('python'), 'The clean E2E host must not have the Python debugger extension installed.');

        await createEmptyAppHostProject(projectName);
        await addIntegrationPackageToAppHost('Aspire.Hosting.Python', appHostPath);

        const pythonAppDirectory = path.join(getGeneratedProjectRoot(projectName), 'pythonapp');
        fs.mkdirSync(pythonAppDirectory, { recursive: true });
        writeFileWithRetry(path.join(pythonAppDirectory, 'app.py'), 'import time\n\nprint("ready", flush=True)\ntime.sleep(600)\n');

        const appHostSource = fs.readFileSync(appHostPath, 'utf8');
        writeFileWithRetry(
            appHostPath,
            `// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

${appHostSource.replace(
                'builder.Build().Run();',
                `builder.AddPythonApp("pythonapp", "./pythonapp", "app.py")
    .WithVirtualEnvironment(".venv", createIfNotExists: false)
    .WithCommand(OperatingSystem.IsWindows() ? "python" : "python3");

builder.Build().Run();`)}`);
        writeWorkspaceAppHostConfigForPath(appHostPath);

        await waitForSelectedWorkspaceAppHost(appHostPath);
        await executeE2eControlCommand({ name: 'openFile', filePath: appHostPath });
        await waitForEditorTitle('apphost.cs');

        const runBefore = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 180000, runBefore);
        await waitForResourceState('pythonapp', ['Running'], 180000);

        const notification = await waitForNotificationMessage(
            'Install the Python debugger extension to debug resources in this app.',
            60000);
        assert.deepStrictEqual(
            await getNotificationActionTitles(notification),
            ['Install', "Don't Show Again"]);

        await notification.dismiss();
        await waitForWorkbenchText('Install Python debugger', 60000);
    });
});
