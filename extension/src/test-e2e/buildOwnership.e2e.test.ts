import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';

import { getCommandInvocationCount, waitForCommandOutcome, waitForDebugConsoleOutput, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForRunningAppHost, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, runE2eTeardown, stopPrimaryAppHostIfRunning, writeFileWithRetry } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire AppHost build ownership E2E', function () {
    this.timeout(300000);

    let appHostSourcePath: string | undefined;
    let originalSource: string | undefined;

    teardown(async () => {
        await runE2eTeardown([
            () => {
                if (appHostSourcePath !== undefined && originalSource !== undefined) {
                    writeFileWithRetry(appHostSourcePath, originalSource);
                }
            },
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
        ], 'AppHost build ownership E2E teardown failed.');
    });

    test('Run with Aspire rebuilds a changed AppHost before the stale second launch', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        appHostSourcePath = path.join(path.dirname(appHostPath), 'AppHost.cs');
        originalSource = fs.readFileSync(appHostSourcePath, 'utf8');
        // Keep this fixture on APIs available in released AppHost SDKs so the same regression
        // proves the extension-side fallback with older CLI and SDK combinations.
        const compatibleSource = `// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

builder.Build().Run();
`;
        writeFileWithRetry(appHostSourcePath, compatibleSource);

        let before = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 120000, before);
        await waitForRunningAppHost(180000);

        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions(120000);
        await waitForNoRunningAppHost(120000, appHostPath);

        const brokenSource = compatibleSource.replace(
            'builder.Build().Run();',
            '__AspireE2EStaleRunMissingSymbol__();\n\nbuilder.Build().Run();');
        assert.notStrictEqual(brokenSource, compatibleSource, 'Expected AppHost fixture to contain builder.Build().Run().');
        writeFileWithRetry(appHostSourcePath, brokenSource);

        before = getCommandInvocationCount('aspire-vscode.runAppHost');
        await executeE2eControlCommand({ name: 'runAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.runAppHost', 'success', 120000, before);
        await waitForDebugConsoleOutput("__AspireE2EStaleRunMissingSymbol__' does not exist", appHostPath, 120000);
        await waitForDebugConsoleOutput('The project could not be built', appHostPath, 120000);
        await waitForNoRunningAppHost(120000, appHostPath);
    });
});
