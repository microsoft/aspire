import * as assert from 'assert';
import * as path from 'path';
import { waitForRepositoryIdle, waitForWorkspaceAppHost } from './helpers/assertions';
import {
    getCliWrapperInvocationCount,
    restoreE2eCliPathForE2E,
    restoreWorkspaceCliPath,
    runE2eTeardown,
    setE2eCliPathForE2E,
    touchPrimaryAppHostProject,
    waitForCliWrapperInvocation,
    writeTrackedStreamingDiscoveryCliWrapper,
    writeWorkspaceCliPath,
} from './helpers/fixtures';
import { getWorkspaceRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Workspace-relative CLI path E2E', function () {
    this.timeout(240000);

    teardown(async () => {
        await runE2eTeardown([
            () => restoreE2eCliPathForE2E(),
            () => restoreWorkspaceCliPath(),
        ], 'Workspace-relative CLI path E2E teardown failed.');
    });

    test('expands workspaceFolder before invoking the CLI', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        await waitForWorkspaceAppHost();

        const wrapper = writeTrackedStreamingDiscoveryCliWrapper(0, 0);
        const relativePath = path.relative(getWorkspaceRoot(), wrapper.cliPath).split(path.sep).join('/');
        await setE2eCliPathForE2E(undefined);
        await writeWorkspaceCliPath(`\${workspaceFolder}/${relativePath}`);

        touchPrimaryAppHostProject();
        await waitForCliWrapperInvocation(wrapper.invocationLogPath, 30_000);

        assert.ok(getCliWrapperInvocationCount(wrapper.invocationLogPath) > 0);
    });
});