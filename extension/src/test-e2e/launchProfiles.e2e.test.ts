import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import type { ProjectLaunchConfiguration } from '../dcp/types';
import { waitForRepositoryIdle } from './helpers/assertions';
import { executeE2eControlCommand, runE2eTeardown, writeFileWithRetry } from './helpers/fixtures';
import { getPrimaryAppHostProjectPath } from './helpers/paths';
import { openAspireView } from './helpers/vscode';

suite('Aspire launch profiles E2E', function () {
    this.timeout(240000);

    const appHostProjectPath = getPrimaryAppHostProjectPath();
    const appHostDirectory = path.dirname(appHostProjectPath);
    const launchSettingsDirectory = path.join(appHostDirectory, 'Properties');
    const launchSettingsPath = path.join(launchSettingsDirectory, 'launchSettings.json');
    let originalLaunchSettings: FileSnapshot | undefined;
    let launchSettingsDirectoryExisted: boolean | undefined;

    teardown(async () => {
        await runE2eTeardown([
            () => restoreFile(launchSettingsPath, originalLaunchSettings),
            () => removeDirectoryIfCreated(launchSettingsDirectory, launchSettingsDirectoryExisted),
        ], 'Launch profiles E2E teardown failed.');
    });

    test('uses the AppHost launch profile selected by launch.json', async () => {
        originalLaunchSettings = captureFile(launchSettingsPath);
        launchSettingsDirectoryExisted = fs.existsSync(launchSettingsDirectory);

        await openAspireView();
        await waitForRepositoryIdle();

        fs.mkdirSync(launchSettingsDirectory, { recursive: true });
        writeFileWithRetry(launchSettingsPath, JSON.stringify({
            profiles: {
                h1: {
                    commandName: 'Project',
                    environmentVariables: {
                        mode: '1',
                    },
                },
                h2: {
                    commandName: 'Project',
                    applicationUrl: 'http://localhost:15002',
                    environmentVariables: {
                        mode: '2',
                    },
                },
            },
        }, undefined, 2));

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: appHostProjectPath,
        };
        const controlStatus = await executeE2eControlCommand({
            name: 'createResourceDebugConfiguration',
            launchConfig,
            env: [
                { name: 'mode', value: '1' },
                { name: 'DOTNET_LAUNCH_PROFILE', value: 'h1' },
                { name: 'ASPNETCORE_URLS', value: 'http://localhost:15001' },
                { name: 'EXPLICIT', value: 'from-cli' },
            ],
            debug: false,
            isApphost: true,
            debuggers: {
                apphost: {
                    launchProfile: 'h2',
                    env: {
                        EXPLICIT: 'from-launch-json',
                    },
                },
            },
            environmentKeys: ['mode', 'DOTNET_LAUNCH_PROFILE', 'ASPNETCORE_URLS', 'EXPLICIT'],
        }, { timeoutMs: 180000 });
        const debugConfiguration = controlStatus.result as PreparedDebugConfiguration;

        assert.deepStrictEqual(debugConfiguration.environment, {
            mode: '2',
            DOTNET_LAUNCH_PROFILE: 'h2',
            ASPNETCORE_URLS: 'http://localhost:15002',
            EXPLICIT: 'from-launch-json',
        });
    });
});

interface PreparedDebugConfiguration {
    environment: Record<string, string | undefined>;
}

type FileSnapshot =
    | { exists: false }
    | { exists: true; content: string };

function captureFile(filePath: string): FileSnapshot {
    return fs.existsSync(filePath)
        ? { exists: true, content: fs.readFileSync(filePath, 'utf8') }
        : { exists: false };
}

function restoreFile(filePath: string, snapshot: FileSnapshot | undefined): void {
    if (snapshot === undefined) {
        return;
    }

    if (!snapshot.exists) {
        fs.rmSync(filePath, { force: true });
        return;
    }

    writeFileWithRetry(filePath, snapshot.content);
}

function removeDirectoryIfCreated(directoryPath: string, existed: boolean | undefined): void {
    if (existed === false && fs.existsSync(directoryPath)) {
        fs.rmdirSync(directoryPath);
    }
}
