import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, waitForCommandOutcome, waitForNoDebugSessions, waitForNoRunningAppHost, waitForRepositoryIdle, waitForResourceState, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, runE2eTeardown, stopPrimaryAppHostIfRunning } from './helpers/fixtures';
import { readExtensionLogs } from './helpers/logs';
import { getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire WinUI debug E2E', function () {
    this.timeout(1_200_000);

    const readyMarkerPath = path.join(getWorkspaceRoot(), 'winui-e2e-ready.txt');

    teardown(async () => {
        if (!shouldRunWinUiProof()) {
            return;
        }

        await runE2eTeardown([
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
            () => waitForNoDebugSessions().catch(() => undefined),
            () => waitForNoRunningAppHost().catch(() => undefined),
            () => fs.rmSync(readyMarkerPath, { force: true }),
        ], 'WinUI debug E2E teardown failed.');
    });

    test('debugs an unpackaged WinUI project through its generated apphost executable', async function () {
        if (!shouldRunWinUiProof()) {
            this.skip();
        }

        fs.rmSync(readyMarkerPath, { force: true });
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();

        const before = getCommandInvocationCount('aspire-vscode.debugAppHost');
        await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
        await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, before);

        const marker = await waitForReadyMarker(readyMarkerPath, 240000);
        assert.match(marker, /^ready:\d+$/);
        await waitForResourceState('e2e-winui', ['Running'], 30000);
        assert.ok(
            readExtensionLogs().includes('Using generated apphost executable for unpackaged WinUI project:'),
            'Expected the extension log to confirm that the WinUI apphost executable was selected.');

        await executeE2eControlCommand({ name: 'stopDebugging' });
        await waitForNoDebugSessions();
        await waitForNoRunningAppHost(120000, appHostPath);
    });
});

function shouldRunWinUiProof(): boolean {
    return process.platform === 'win32' && process.env.ASPIRE_EXTENSION_E2E_ENABLE_WINUI === 'true';
}

async function waitForReadyMarker(markerPath: string, timeoutMs: number): Promise<string> {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
        try {
            // The generated WinUI App writes `ready:<pid>` from OnLaunched. The reported crash occurs
            // in Application.Start before OnLaunched, so observing this marker proves XAML startup passed.
            return fs.readFileSync(markerPath, 'utf8').trim();
        }
        catch (error) {
            if ((error as NodeJS.ErrnoException).code !== 'ENOENT') {
                throw error;
            }
        }

        await new Promise(resolve => setTimeout(resolve, 500));
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for the WinUI OnLaunched marker.`);
}
