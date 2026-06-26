import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import { getCommandInvocationCount, waitForCommandOutcome, waitForDebugSessionStartup, waitForRepositoryIdle, waitForWorkspaceAppHost } from './helpers/assertions';
import { executeE2eControlCommand, restoreE2eCliPathForE2E, restoreWorkspaceCliPath, runE2eTeardown, setE2eCliPathForE2E, stopPrimaryAppHostIfRunning, writeFileWithRetry, writeWorkspaceCliPath } from './helpers/fixtures';
import { getCliPath, getPrimaryAppHostProjectPath, getWorkspaceRoot } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire configured CLI path E2E', function () {
    this.timeout(360000);

    teardown(async () => {
        await runE2eTeardown([
            () => restoreE2eCliPathForE2E(),
            () => restoreWorkspaceCliPath(),
            () => executeE2eControlCommand({ name: 'stopDebugging' }),
            () => stopPrimaryAppHostIfRunning(),
        ], 'Configured CLI path E2E teardown failed.');
    });

    test('flows the configured CLI path into AppHost debug builds', async () => {
        await openAspireView();
        await waitForRepositoryIdle();
        const discovered = await waitForWorkspaceAppHost();
        const appHostPath = discovered.state.workspaceAppHostPath ?? getPrimaryAppHostProjectPath();
        const originalProject = fs.readFileSync(appHostPath, 'utf8');
        const writtenCliPathFile = path.join(path.dirname(appHostPath), 'obj', 'e2e-aspire-cli-path.txt');
        const configuredCliPath = copyCliBundleWithSpaces();

        try {
            fs.rmSync(writtenCliPathFile, { force: true });
            await setE2eCliPathForE2E(undefined);
            await writeWorkspaceCliPath(configuredCliPath);
            const resolvedCli = await executeE2eControlCommand({ name: 'getResolvedCliPath' });
            assertResolvedCliPath(resolvedCli.result, configuredCliPath);
            writeFileWithRetry(appHostPath, addAspireCliPathProbeTarget(originalProject));

            const before = getCommandInvocationCount('aspire-vscode.debugAppHost');
            await executeE2eControlCommand({ name: 'debugAppHost', appHostPath }, { waitFor: 'started' });
            await waitForCommandOutcome('aspire-vscode.debugAppHost', 'success', 60000, before);
            await waitForDebugSessionStartup(appHostPath, 300000);

            await waitForFileContent(writtenCliPathFile, configuredCliPath, 180000);
        }
        finally {
            writeFileWithRetry(appHostPath, originalProject);
            await runE2eTeardown([
                () => executeE2eControlCommand({ name: 'stopDebugging' }),
                () => stopPrimaryAppHostIfRunning(),
            ], 'Configured CLI path E2E cleanup failed.');
        }
    });
});

function copyCliBundleWithSpaces(): string {
    const cliPath = getCliPath();
    const sourceDirectory = path.dirname(cliPath);
    const configuredDirectory = path.join(getWorkspaceRoot(), 'configured cli bundle');
    fs.rmSync(configuredDirectory, { recursive: true, force: true });
    fs.cpSync(sourceDirectory, configuredDirectory, { recursive: true });

    const configuredCliPath = path.join(configuredDirectory, path.basename(cliPath));
    if (!fs.existsSync(configuredCliPath)) {
        throw new Error(`Configured CLI bundle copy did not contain '${path.basename(cliPath)}'.`);
    }

    if (process.platform !== 'win32') {
        fs.chmodSync(configuredCliPath, fs.statSync(configuredCliPath).mode | 0o700);
    }

    return configuredCliPath;
}

function addAspireCliPathProbeTarget(projectContents: string): string {
    const target = `
  <Target Name="WriteE2EAspireCliPath" BeforeTargets="ComputeRunArguments;CoreCompile">
    <WriteLinesToFile File="$(BaseIntermediateOutputPath)e2e-aspire-cli-path.txt"
                      Lines="$(AspireCliPath)"
                      Overwrite="true" />
  </Target>
`;
    const updated = projectContents.replace('</Project>', `${target}\n</Project>`);
    assert.notStrictEqual(updated, projectContents, 'Expected AppHost project to contain </Project>.');

    return updated;
}

async function waitForFileContent(filePath: string, expectedText: string, timeoutMs: number): Promise<void> {
    const started = Date.now();
    let lastContent = '<missing>';
    while (Date.now() - started < timeoutMs) {
        if (fs.existsSync(filePath)) {
            lastContent = fs.readFileSync(filePath, 'utf8');
            if (lastContent.includes(expectedText)) {
                return;
            }
        }

        await delay(500);
    }

    throw new Error(`Timed out after ${timeoutMs}ms waiting for ${filePath} to contain '${expectedText}'. Last content:\n${lastContent}`);
}

function delay(ms: number): Promise<void> {
    return new Promise(resolve => setTimeout(resolve, ms));
}

function assertResolvedCliPath(value: unknown, expectedCliPath: string): void {
    assert.ok(value && typeof value === 'object', `Expected resolved CLI path result, got ${JSON.stringify(value)}.`);
    const result = value as { cliPath?: unknown; configuredPath?: unknown; e2eCliPath?: unknown };
    assert.strictEqual(result.cliPath, expectedCliPath);
    assert.strictEqual(result.configuredPath, expectedCliPath);
    assert.strictEqual(result.e2eCliPath, undefined);
}
