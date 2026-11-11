import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import { EventEmitter } from 'events';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { mkdtemp, rename, rm, writeFile } from 'fs/promises';
import * as os from 'os';
import * as path from 'path';
import { ConfigInfoProvider, getConfigInfo, parseCliUpdateRecommendationOutput, parseConfigInfoOutput } from '../utils/configInfoProvider';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliModule from '../utils/process/cliProcess';
import { AppHostDiscoveryService } from '../utils/appHostDiscovery';
import { AppHostDataRepository } from '../data/AppHostDataRepository';
import { agentMcpCapability, describeIncludeDisabledCommandsCapability, isolatedLaunchCapability, lsJsonStreamCapability } from '../types/configInfo';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';

function emitConfigInfo(options: cliModule.SpawnProcessOptions | undefined, capabilities: readonly string[] = []): void {
    options?.stdoutCallback?.(JSON.stringify({
        localSettingsPath: '/workspace/aspire.config.json',
        globalSettingsPath: '/home/user/.aspire/aspire.config.json',
        availableFeatures: [],
        localSettingsSchema: { properties: [] },
        globalSettingsSchema: { properties: [] },
        capabilities,
    }));
    options?.exitCallback?.(0);
}

function createDoctorVersionOutput(
    currentVersion: string,
    latestVersion?: string,
    updateCheckError?: string,
    identityChannel: string | null = 'stable',
    latestVersionChannel: string | null = latestVersion
        ? (latestVersion.includes('-') ? 'prerelease' : 'stable')
        : null,
): string {
    return JSON.stringify({
        checks: [{
            name: 'cli-version',
            metadata: {
                currentVersion,
                latestVersion,
                updateCheckError,
                ...(identityChannel === null ? {} : { identityChannel }),
                ...(latestVersionChannel === null ? {} : { latestVersionChannel }),
            },
        }],
        summary: { passed: 0, warnings: 0, failed: 0 },
        installations: [],
    });
}

suite('configInfoProvider tests', () => {
    teardown(() => sinon.restore());

    test('parseConfigInfoOutput accepts current camel-case CLI JSON', () => {
        const configInfo = parseConfigInfoOutput(JSON.stringify({
            localSettingsPath: '/workspace/aspire.config.json',
            globalSettingsPath: '/home/user/.aspire/aspire.config.json',
            availableFeatures: [
                {
                    name: 'pipelines',
                    description: 'Pipeline support',
                    defaultValue: true,
                },
            ],
            localSettingsSchema: {
                properties: [
                    {
                        name: 'appHost',
                        type: 'object',
                        description: 'AppHost settings',
                        required: false,
                        subProperties: [
                            {
                                name: 'path',
                                type: 'string',
                                description: 'AppHost path',
                                required: true,
                            },
                        ],
                    },
                ],
            },
            globalSettingsSchema: {
                properties: [],
            },
            configFileSchema: {
                properties: [],
            },
            capabilities: ['pipelines'],
        }));

        assert.strictEqual(configInfo.localSettingsPath, '/workspace/aspire.config.json');
        assert.strictEqual(configInfo.globalSettingsPath, '/home/user/.aspire/aspire.config.json');
        assert.strictEqual(configInfo.availableFeatures[0].name, 'pipelines');
        assert.strictEqual(configInfo.availableFeatures[0].defaultValue, true);
        assert.strictEqual(configInfo.localSettingsSchema.properties[0].name, 'appHost');
        assert.strictEqual(configInfo.localSettingsSchema.properties[0].subProperties?.[0].name, 'path');
        assert.deepStrictEqual(configInfo.capabilities, ['pipelines']);
    });

    test('parseConfigInfoOutput accepts legacy Pascal-case CLI JSON', () => {
        const configInfo = parseConfigInfoOutput(JSON.stringify({
            LocalSettingsPath: '/workspace/aspire.config.json',
            GlobalSettingsPath: '/home/user/.aspire/aspire.config.json',
            AvailableFeatures: [
                {
                    Name: 'pipelines',
                    Description: 'Pipeline support',
                    DefaultValue: true,
                },
            ],
            LocalSettingsSchema: {
                Properties: [
                    {
                        Name: 'packageSources',
                        Type: 'object',
                        Description: 'Package sources',
                        Required: false,
                        AdditionalPropertiesType: 'string',
                    },
                ],
            },
            GlobalSettingsSchema: {
                Properties: [],
            },
            Capabilities: ['pipelines'],
        }));

        assert.strictEqual(configInfo.localSettingsPath, '/workspace/aspire.config.json');
        assert.strictEqual(configInfo.globalSettingsPath, '/home/user/.aspire/aspire.config.json');
        assert.strictEqual(configInfo.availableFeatures[0].description, 'Pipeline support');
        assert.strictEqual(configInfo.localSettingsSchema.properties[0].additionalPropertiesType, 'string');
        assert.deepStrictEqual(configInfo.capabilities, ['pipelines']);
    });

    test('getConfigInfo runs in the workspace folder when one is open', async () => {
        const workspaceFolder: vscode.WorkspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        };
        sinon.stub(vscode.workspace, 'workspaceFolders').value([workspaceFolder]);
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/usr/bin/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let workingDirectory: string | undefined;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            workingDirectory = options?.workingDirectory;
            options?.stdoutCallback?.(JSON.stringify({
                localSettingsPath: '/workspace/aspire.config.json',
                globalSettingsPath: '/home/user/.aspire/aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
            }));
            options?.exitCallback?.(0);
            return {} as ChildProcessWithoutNullStreams;
        });

        const configInfo = await getConfigInfo(terminalProvider);

        assert.ok(configInfo);
        assert.strictEqual(workingDirectory, workspaceFolder.uri.fsPath);
        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['config', 'info', '--json', '--nologo']);
        assert.strictEqual(spawnStub.firstCall.args[3]?.noExtensionVariables, true);
    });

    test('getConfigInfo runs in the targeted folder rather than the first one', async () => {
        const folderA: vscode.WorkspaceFolder = { uri: vscode.Uri.file('/repo/a'), name: 'a', index: 0 };
        const folderB: vscode.WorkspaceFolder = { uri: vscode.Uri.file('/repo/b'), name: 'b', index: 1 };
        sinon.stub(vscode.workspace, 'workspaceFolders').value([folderA, folderB]);
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/usr/bin/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let workingDirectory: string | undefined;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            workingDirectory = options?.workingDirectory;
            options?.stdoutCallback?.(JSON.stringify({
                localSettingsPath: '/repo/b/aspire.config.json',
                globalSettingsPath: '/home/user/.aspire/aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
            }));
            options?.exitCallback?.(0);
            return {} as ChildProcessWithoutNullStreams;
        });

        // `aspire config info` reports the local settings file it discovers from its working
        // directory, so running in folder A answers questions about folder A no matter which folder
        // the caller named. "Open Local Settings" would then open, or create, the wrong file.
        const configInfo = await new ConfigInfoProvider(terminalProvider).getConfigInfo({
            target: workspaceFolderCliPathTarget(folderB),
        });

        assert.ok(configInfo);
        assert.strictEqual(workingDirectory, folderB.uri.fsPath);
        assert.strictEqual(configInfo.localSettingsPath, '/repo/b/aspire.config.json');
    });

    test('getConfigInfo does not serve one folder result to another folder from cache', async () => {
        const folderA: vscode.WorkspaceFolder = { uri: vscode.Uri.file('/repo/a'), name: 'a', index: 0 };
        const folderB: vscode.WorkspaceFolder = { uri: vscode.Uri.file('/repo/b'), name: 'b', index: 1 };
        sinon.stub(vscode.workspace, 'workspaceFolders').value([folderA, folderB]);
        const terminalProvider = {
            // One CLI serves both folders, which is the common case and the reason a cache keyed only
            // by CLI path conflates them.
            getAspireCliExecutablePath: async () => '/usr/bin/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            options?.stdoutCallback?.(JSON.stringify({
                localSettingsPath: `${options?.workingDirectory}/aspire.config.json`,
                globalSettingsPath: '/home/user/.aspire/aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
            }));
            options?.exitCallback?.(0);
            return {} as ChildProcessWithoutNullStreams;
        });

        const provider = new ConfigInfoProvider(terminalProvider);
        const a = await provider.getConfigInfo({ target: workspaceFolderCliPathTarget(folderA) });
        const b = await provider.getConfigInfo({ target: workspaceFolderCliPathTarget(folderB) });

        assert.strictEqual(a?.localSettingsPath, `${folderA.uri.fsPath}/aspire.config.json`);
        assert.strictEqual(b?.localSettingsPath, `${folderB.uri.fsPath}/aspire.config.json`);
    });

    test('getConfigInfo forwards options.target to terminalProvider.getAspireCliExecutablePath', async () => {
        const workspaceFolder: vscode.WorkspaceFolder = {
            uri: vscode.Uri.file('/repo/a'),
            name: 'a',
            index: 0,
        };
        const target = workspaceFolderCliPathTarget(workspaceFolder);
        const getAspireCliExecutablePathStub = sinon.stub().resolves('/repo/a/bin/aspire');
        const terminalProvider = {
            getAspireCliExecutablePath: getAspireCliExecutablePathStub,
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            options?.stdoutCallback?.(JSON.stringify({
                localSettingsPath: '/repo/a/aspire.config.json',
                globalSettingsPath: '/home/user/.aspire/aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
            }));
            options?.exitCallback?.(0);
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const configInfo = await provider.getConfigInfo({ target });

        assert.ok(configInfo);
        assert.ok(getAspireCliExecutablePathStub.calledOnceWith(target));
    });

    test('getConfigInfo retries without nologo when an older CLI rejects it', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/usr/bin/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args = [], options) => {
            if (args.includes('--nologo')) {
                options?.stderrCallback?.("Unrecognized command or argument '--nologo'.");
                options?.exitCallback?.(1);
            } else {
                options?.stdoutCallback?.(JSON.stringify({
                    localSettingsPath: '/workspace/aspire.config.json',
                    globalSettingsPath: '/home/user/.aspire/aspire.config.json',
                    availableFeatures: [],
                    localSettingsSchema: { properties: [] },
                    globalSettingsSchema: { properties: [] },
                }));
                options?.exitCallback?.(0);
            }

            return {} as ChildProcessWithoutNullStreams;
        });

        const configInfo = await getConfigInfo(terminalProvider);

        assert.ok(configInfo);
        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['config', 'info', '--json', '--nologo']);
        assert.deepStrictEqual(spawnStub.secondCall.args[2], ['config', 'info', '--json']);
    });

    test('parseCliUpdateRecommendationOutput accepts stable and prerelease recommendations', () => {
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.4.0', '13.5.2')),
            { status: 'available', currentVersion: '13.4.0', version: '13.5.2' });
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.5.0', '13.6.0')),
            { status: 'available', currentVersion: '13.5.0', version: '13.6.0' });
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.6.0-preview.2', '13.7.0-preview.1', undefined, 'daily')),
            { status: 'available', currentVersion: '13.6.0-preview.2', version: '13.7.0-preview.1' });
        // Doctor reports only one stable-first recommendation. Mark that cross-lane result
        // ineligible because an unchanged prerelease identity cannot make it actionable.
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.6.0-preview.2', '13.6.0', undefined, 'daily')),
            { status: 'ineligible', currentVersion: '13.6.0-preview.2' });
        // The CLI's stable update rule cannot produce this payload, but reject it defensively so a
        // stable installation is never nudged onto a prerelease channel.
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.6.0', '13.7.0-preview.1')),
            { status: 'ineligible', currentVersion: '13.6.0' });
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.6.0')),
            { status: 'none', currentVersion: '13.6.0' });
        for (const identityChannel of ['local', 'pr-19670', 'run-42', 'default', 'future']) {
            assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
                createDoctorVersionOutput('13.6.0-dev', '13.7.0-preview.1', undefined, identityChannel)),
                { status: 'ineligible', currentVersion: '13.6.0-dev' });
        }
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.6.0-preview.1', '13.7.0-preview.1', undefined, null)),
            { status: 'ineligible', currentVersion: '13.6.0-preview.1' });
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.6.0-preview.1', '13.7.0-preview.1', undefined, 'daily', null)),
            { status: 'available', currentVersion: '13.6.0-preview.1', version: '13.7.0-preview.1' });
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.6.0-dev', undefined, 'offline', 'local')),
            { status: 'ineligible', currentVersion: '13.6.0-dev' });
        assert.deepStrictEqual(parseCliUpdateRecommendationOutput(
            createDoctorVersionOutput('13.5.0', undefined, 'offline')),
            { status: 'unavailable' });
    });

    test('getCliUpdateRecommendation accepts structured doctor output on a nonzero exit', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, command, args, options) => {
            assert.strictEqual(command, '/exact/aspire');
            assert.deepStrictEqual(args, ['doctor', '--format', 'json', '--nologo']);
            assert.deepStrictEqual(options?.env, [{ name: 'ASPIRE_NON_INTERACTIVE', value: 'true' }]);
            assert.strictEqual(options?.workingDirectory, '/captured/workspace');
            const output = JSON.parse(createDoctorVersionOutput('13.5.0', '13.6.0'));
            output.checks.push({
                name: 'unrelated-check',
                details: 'A nested command used --nologo.',
            });
            options?.stdoutCallback?.(JSON.stringify(output));
            // `aspire doctor` exits nonzero when an unrelated prerequisite check fails, but its
            // structured CLI update metadata is still valid. Text from another check must not be
            // mistaken for the root command rejecting --nologo.
            options?.exitCallback?.(1);
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        assert.deepStrictEqual(
            await provider.getCliUpdateRecommendation({
                cliPath: '/exact/aspire',
                workingDirectory: '/captured/workspace',
            }),
            { status: 'available', currentVersion: '13.5.0', version: '13.6.0' });
        assert.strictEqual(spawnStub.callCount, 1);
    });

    test('getCliUpdateRecommendation retries without nologo and keeps unavailable checks silent', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let attempt = 0;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args, options) => {
            if (attempt++ === 0) {
                options?.stderrCallback?.("Unrecognized command or argument '--nologo'.");
                options?.exitCallback?.(1);
            } else {
                options?.stdoutCallback?.('not json');
                options?.exitCallback?.(0);
            }
            return {} as ChildProcessWithoutNullStreams;
        });
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const provider = new ConfigInfoProvider(terminalProvider);

        assert.deepStrictEqual(
            await provider.getCliUpdateRecommendation({ cliPath: '/exact/aspire' }),
            { status: 'unavailable' });
        assert.deepStrictEqual(spawnStub.getCalls().map(call => call.args[2]), [
            ['doctor', '--format', 'json', '--nologo'],
            ['doctor', '--format', 'json'],
        ]);
        assert.strictEqual(showErrorMessage.callCount, 0);
    });

    test('getCliVersion identifies an executable replaced with the same version', async () => {
        const directory = await mkdtemp(path.join(os.tmpdir(), 'aspire-cli-version-'));
        const cliPath = path.join(directory, 'aspire');
        const replacementPath = path.join(directory, 'replacement');
        await writeFile(cliPath, 'first executable');
        const terminalProvider = {
            getAspireCliExecutablePath: async () => cliPath,
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, command, args, options) => {
            assert.strictEqual(command, cliPath);
            assert.deepStrictEqual(args, ['--version']);
            options?.stdoutCallback?.('13.5.0');
            options?.exitCallback?.(0);
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        try {
            const first = await provider.getCliVersion({ cliPath });
            await writeFile(replacementPath, 'replacement executable');
            await rename(replacementPath, cliPath);
            const second = await provider.getCliVersion({ cliPath });

            assert.strictEqual(first?.version, '13.5.0');
            assert.strictEqual(second?.version, '13.5.0');
            assert.notStrictEqual(first?.executableIdentity, second?.executableIdentity);
            assert.strictEqual(spawnStub.callCount, 2);
        }
        finally {
            await rm(directory, { recursive: true, force: true });
        }
    });

    test('version and update probes do not settle before cancellation termination completes', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const childProcess = { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        sinon.stub(cliModule, 'spawnCliProcess').returns(childProcess);
        const terminations: Array<() => void> = [];
        sinon.stub(cliModule, 'terminateCliProcess').callsFake(() =>
            new Promise<void>(resolve => terminations.push(resolve)));
        const provider = new ConfigInfoProvider(terminalProvider);

        for (const startProbe of [
            (cancellation: vscode.CancellationTokenSource) => provider.getCliVersion({
                cliPath: '/exact/aspire',
                cancellationToken: cancellation.token,
            }),
            (cancellation: vscode.CancellationTokenSource) => provider.getCliUpdateRecommendation({
                cliPath: '/exact/aspire',
                cancellationToken: cancellation.token,
            }),
        ]) {
            const cancellation = new vscode.CancellationTokenSource();
            const probe = startProbe(cancellation);
            let settled = false;
            void probe.then(() => settled = true);

            cancellation.cancel();
            await Promise.resolve();
            assert.strictEqual(settled, false);

            terminations.shift()?.();
            await probe;
            assert.strictEqual(settled, true);
            cancellation.dispose();
        }
    });

    test('getCapabilityStatus uses advertised capabilities before the minimum-version fallback', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args, options) => {
            assert.deepStrictEqual(args, ['config', 'info', '--json', '--nologo']);
            emitConfigInfo(options, [isolatedLaunchCapability]);
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const status = await provider.getCapabilityStatus(isolatedLaunchCapability, {
            cliPath: '/exact/aspire',
            forceRefresh: true,
            minimumVersion: '13.2.0',
        });

        assert.strictEqual(status, 'supported');
        assert.strictEqual(spawnStub.callCount, 1);
    });

    test('getCapabilityStatus reports agent MCP support as unsupported when the CLI does not advertise it', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            emitConfigInfo(options, []);
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const status = await provider.getCapabilityStatus(agentMcpCapability, {
            cliPath: '/exact/aspire',
            forceRefresh: true,
        });

        assert.strictEqual(status, 'unsupported');
    });

    test('getCapabilityStatus reports agent MCP support as unsupported when the CLI omits the capabilities property entirely', async () => {
        // An older CLI does not emit `capabilities` at all, which is a successful probe of a CLI
        // that cannot run `aspire agent mcp`, not a probe that failed. It has to fail closed as
        // `unsupported` rather than `unavailable`, and without falling back to a version check.
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            options?.stdoutCallback?.(JSON.stringify({
                localSettingsPath: '/workspace/aspire.config.json',
                globalSettingsPath: '/home/user/.aspire/aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
            }));
            options?.exitCallback?.(0);
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const configInfo = await provider.getConfigInfo({ cliPath: '/exact/aspire', forceRefresh: true });
        const status = await provider.getCapabilityStatus(agentMcpCapability, { cliPath: '/exact/aspire' });

        assert.ok(configInfo, 'The probe must succeed so the status reflects an older CLI, not a failed read.');
        assert.strictEqual(configInfo.capabilities, undefined);
        assert.strictEqual(status, 'unsupported');
    });

    test('getCapabilityStatus reports agent MCP support as supported when the CLI advertises it', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            emitConfigInfo(options, [agentMcpCapability]);
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const status = await provider.getCapabilityStatus(agentMcpCapability, {
            cliPath: '/exact/aspire',
            forceRefresh: true,
        });

        assert.strictEqual(status, 'supported');
    });

    test('getCapabilityStatus accepts the stable minimum and higher numeric cores but rejects minimum-core prereleases', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const versions = [
            ['13.2.0', 'supported'],
            ['13.2.4+abcdef', 'supported'],
            ['13.3.0-preview.1.12345.6', 'supported'],
            ['13.3.0-dev.123+abcdef', 'supported'],
            ['13.2.0-preview.1.12345.6', 'unsupported'],
            ['13.2.0-dev.123+abcdef', 'unsupported'],
            ['13.1.99', 'unsupported'],
        ] as const;
        let versionIndex = 0;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, command, args, options) => {
            assert.strictEqual(command, '/exact/aspire');
            if (args?.[0] === 'config') {
                emitConfigInfo(options);
            } else {
                assert.deepStrictEqual(args, ['--version']);
                options?.stdoutCallback?.(`${versions[versionIndex++][0]}\n`);
                options?.exitCallback?.(0);
            }
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        for (const [, expectedStatus] of versions) {
            assert.strictEqual(
                await provider.getCapabilityStatus(isolatedLaunchCapability, {
                    cliPath: '/exact/aspire',
                    forceRefresh: true,
                    minimumVersion: '13.2.0',
                }),
                expectedStatus);
        }

        assert.strictEqual(spawnStub.callCount, versions.length * 2);
        for (const call of spawnStub.getCalls().filter(call => call.args[2]?.[0] === '--version')) {
            assert.strictEqual(call.args[3]?.createProcessGroup, true);
            assert.strictEqual(call.args[3]?.noExtensionVariables, true);
        }
    });

    test('getCapabilityStatus reports malformed or unbounded version output as unavailable', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const invalidVersions = [
            '',
            'Aspire CLI 13.2.0',
            '13.2',
            '13.2.0\n13.2.1',
            '100000.2.0',
            '13.2.0-preview!',
        ];
        let versionIndex = 0;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args, options) => {
            if (args?.[0] === 'config') {
                emitConfigInfo(options);
            } else {
                options?.stdoutCallback?.(invalidVersions[versionIndex++]);
                options?.exitCallback?.(0);
            }
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        for (const _invalidVersion of invalidVersions) {
            assert.strictEqual(
                await provider.getCapabilityStatus(isolatedLaunchCapability, {
                    cliPath: '/exact/aspire',
                    forceRefresh: true,
                    minimumVersion: '13.2.0',
                }),
                'unavailable');
        }
    });

    test('getCapabilityStatus falls back to exact CLI version when config info is unavailable', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, command, args, options) => {
            assert.strictEqual(command, '/exact/aspire');
            if (args?.[0] === 'config') {
                options?.exitCallback?.(1);
            } else {
                assert.deepStrictEqual(args, ['--version']);
                options?.stdoutCallback?.('13.2.4');
                options?.exitCallback?.(0);
            }
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const status = await provider.getCapabilityStatus(isolatedLaunchCapability, {
            cliPath: '/exact/aspire',
            forceRefresh: true,
            suppressErrors: true,
            minimumVersion: '13.2.0',
        });

        assert.strictEqual(status, 'supported');
        assert.deepStrictEqual(spawnStub.getCalls().map(call => call.args[2]), [
            ['config', 'info', '--json', '--nologo'],
            ['--version'],
        ]);
    });

    test('getCapabilityStatus reports version spawn and exit failures as unavailable', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let versionAttempt = 0;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args, options) => {
            if (args?.[0] === 'config') {
                emitConfigInfo(options);
            } else if (versionAttempt++ === 0) {
                options?.errorCallback?.(new Error('spawn failed'));
            } else {
                options?.stdoutCallback?.('13.2.0');
                options?.exitCallback?.(2);
            }
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        for (let attempt = 0; attempt < 2; attempt++) {
            assert.strictEqual(
                await provider.getCapabilityStatus(isolatedLaunchCapability, {
                    cliPath: '/exact/aspire',
                    forceRefresh: true,
                    minimumVersion: '13.2.0',
                }),
                'unavailable');
        }
    });

    test('getCapabilityStatus reports version timeouts as unavailable and terminates the process', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const childProcess = { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args, options) => {
            if (args?.[0] === 'config') {
                emitConfigInfo(options);
                return {} as ChildProcessWithoutNullStreams;
            }

            return childProcess;
        });
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        const provider = new ConfigInfoProvider(terminalProvider);

        try {
            const probe = provider.getCapabilityStatus(isolatedLaunchCapability, {
                cliPath: '/exact/aspire',
                forceRefresh: true,
                minimumVersion: '13.2.0',
            });
            await clock.tickAsync(30_000);

            assert.strictEqual(await probe, 'unavailable');
            sinon.assert.calledOnceWithExactly(
                terminateStub,
                childProcess,
                'timed-out Aspire CLI version probe');
        }
        finally {
            clock.restore();
        }
    });

    test('getCapabilityStatus shares one timeout budget across config and version probes', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const versionProcess = { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args, options) => {
            if (args?.[0] === 'config') {
                setTimeout(() => emitConfigInfo(options), 5_000);
                return {} as ChildProcessWithoutNullStreams;
            }

            return versionProcess;
        });
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        const provider = new ConfigInfoProvider(terminalProvider);

        try {
            const probe = provider.getCapabilityStatus(isolatedLaunchCapability, {
                cliPath: '/exact/aspire',
                forceRefresh: true,
                minimumVersion: '13.2.0',
                timeoutMs: 10_000,
            });
            await clock.tickAsync(10_000);

            sinon.assert.calledOnceWithExactly(
                terminateStub,
                versionProcess,
                'timed-out Aspire CLI version probe');
            assert.strictEqual(await probe, 'unavailable');
        }
        finally {
            clock.restore();
        }
    });

    test('getCapabilityStatus reports cancellation as unavailable and terminates the version process', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const childProcess = { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        let signalVersionStarted!: () => void;
        const versionStarted = new Promise<void>(resolve => signalVersionStarted = resolve);
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args, options) => {
            if (args?.[0] === 'config') {
                emitConfigInfo(options);
                return {} as ChildProcessWithoutNullStreams;
            }

            signalVersionStarted();
            return childProcess;
        });
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        const provider = new ConfigInfoProvider(terminalProvider);
        const cancellation = new vscode.CancellationTokenSource();

        const probe = provider.getCapabilityStatus(isolatedLaunchCapability, {
            cliPath: '/exact/aspire',
            forceRefresh: true,
            minimumVersion: '13.2.0',
            cancellationToken: cancellation.token,
        });
        await versionStarted;
        cancellation.cancel();

        assert.strictEqual(await probe, 'unavailable');
        sinon.assert.calledOnceWithExactly(
            terminateStub,
            childProcess,
            'cancelled Aspire CLI version probe');
    });

    test('getCapabilityStatus refreshes version fallback when a CLI changes in place', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let version = '13.1.3';
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, args, options) => {
            if (args?.[0] === 'config') {
                emitConfigInfo(options);
            } else {
                options?.stdoutCallback?.(version);
                options?.exitCallback?.(0);
            }
            return {} as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);
        const options = {
            cliPath: '/exact/aspire',
            forceRefresh: true,
            minimumVersion: '13.2.0',
        };

        assert.strictEqual(await provider.getCapabilityStatus(isolatedLaunchCapability, options), 'unsupported');
        version = '13.2.0';
        assert.strictEqual(await provider.getCapabilityStatus(isolatedLaunchCapability, options), 'supported');
        assert.deepStrictEqual(spawnStub.getCalls().map(call => call.args[2]), [
            ['config', 'info', '--json', '--nologo'],
            ['--version'],
            ['config', 'info', '--json', '--nologo'],
            ['--version'],
        ]);
    });

    test('shared provider deduplicates eager capability probes', async () => {
        sinon.stub(vscode.workspace, 'workspaceFolders').value(undefined);
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/usr/bin/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let configInfoOptions: cliModule.SpawnProcessOptions | undefined;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            configInfoOptions = options;
            return { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        });
        const configInfoProvider = new ConfigInfoProvider(terminalProvider);
        const discoveryService = new AppHostDiscoveryService(terminalProvider, configInfoProvider);
        const repository = new AppHostDataRepository(terminalProvider, discoveryService, configInfoProvider);
        const concurrentConfigInfo = configInfoProvider.getConfigInfo({ suppressErrors: true });

        try {
            await new Promise(resolve => setImmediate(resolve));

            assert.strictEqual(spawnStub.callCount, 1);
            assert.deepStrictEqual(spawnStub.firstCall.args[2], ['config', 'info', '--json', '--nologo']);

            configInfoOptions?.stdoutCallback?.(JSON.stringify({
                localSettingsPath: '/workspace/aspire.config.json',
                globalSettingsPath: '/home/user/.aspire/aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [describeIncludeDisabledCommandsCapability, lsJsonStreamCapability],
            }));
            configInfoOptions?.exitCallback?.(0);
            await concurrentConfigInfo;
            await new Promise(resolve => setImmediate(resolve));
        } finally {
            repository.dispose();
            discoveryService.dispose();
        }
    });

    test('getConfigInfo isolates concurrent probes by CLI path', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const optionsByCliPath = new Map<string, cliModule.SpawnProcessOptions>();
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, command, _args, options) => {
            if (options) {
                optionsByCliPath.set(command, options);
            }
            return { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const oldProbe = provider.getConfigInfo({ cliPath: '/old/aspire', suppressErrors: true });
        const newProbe = provider.getConfigInfo({ cliPath: '/new/aspire', suppressErrors: true });

        assert.strictEqual(spawnStub.callCount, 2);
        assert.deepStrictEqual(spawnStub.getCalls().map(call => call.args[1]), ['/old/aspire', '/new/aspire']);

        optionsByCliPath.get('/new/aspire')?.stdoutCallback?.(JSON.stringify({
            localSettingsPath: '/workspace/aspire.config.json',
            globalSettingsPath: '/home/user/.aspire/aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [lsJsonStreamCapability],
        }));
        optionsByCliPath.get('/new/aspire')?.exitCallback?.(0);
        optionsByCliPath.get('/old/aspire')?.stdoutCallback?.(JSON.stringify({
            localSettingsPath: '/workspace/aspire.config.json',
            globalSettingsPath: '/home/user/.aspire/aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [],
        }));
        optionsByCliPath.get('/old/aspire')?.exitCallback?.(0);

        assert.deepStrictEqual((await oldProbe)?.capabilities, []);
        assert.deepStrictEqual((await newProbe)?.capabilities, [lsJsonStreamCapability]);
        assert.deepStrictEqual((await provider.getConfigInfo({ cliPath: '/new/aspire' }))?.capabilities, [lsJsonStreamCapability]);
        assert.strictEqual(spawnStub.callCount, 2);
    });

    test('force refresh replaces an in-flight probe without caching its stale completion', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const probeOptions: cliModule.SpawnProcessOptions[] = [];
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            if (options) {
                probeOptions.push(options);
            }
            return { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const staleProbe = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });
        const refreshedProbe = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true, forceRefresh: true });

        assert.strictEqual(spawnStub.callCount, 2);
        probeOptions[1].stdoutCallback?.(JSON.stringify({
            localSettingsPath: '/workspace/aspire.config.json',
            globalSettingsPath: '/home/user/.aspire/aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [lsJsonStreamCapability],
        }));
        probeOptions[1].exitCallback?.(0);
        assert.deepStrictEqual((await refreshedProbe)?.capabilities, [lsJsonStreamCapability]);

        probeOptions[0].stdoutCallback?.(JSON.stringify({
            localSettingsPath: '/workspace/aspire.config.json',
            globalSettingsPath: '/home/user/.aspire/aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [],
        }));
        probeOptions[0].exitCallback?.(0);
        assert.deepStrictEqual((await staleProbe)?.capabilities, []);

        assert.deepStrictEqual((await provider.getConfigInfo({ cliPath: '/usr/bin/aspire' }))?.capabilities, [lsJsonStreamCapability]);
        assert.strictEqual(spawnStub.callCount, 2);
    });

    test('force refresh cancels and terminates its capability probe', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const childProcess = { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        sinon.stub(cliModule, 'spawnCliProcess').returns(childProcess);
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        const provider = new ConfigInfoProvider(terminalProvider);
        const cancellation = new vscode.CancellationTokenSource();

        const probe = provider.getConfigInfo({
            cliPath: '/usr/bin/aspire',
            suppressErrors: true,
            forceRefresh: true,
            cancellationToken: cancellation.token,
        });
        cancellation.cancel();

        assert.strictEqual(await probe, null);
        sinon.assert.calledOnceWithExactly(terminateStub, childProcess, 'cancelled aspire config info command');
    });

    test('a token-bound force refresh is not shared with unrelated callers', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const childProcesses: ChildProcessWithoutNullStreams[] = [];
        const probeOptions: cliModule.SpawnProcessOptions[] = [];
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            const childProcess = { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
            childProcesses.push(childProcess);
            if (options) {
                probeOptions.push(options);
            }
            return childProcess;
        });
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        const provider = new ConfigInfoProvider(terminalProvider);
        const cancellation = new vscode.CancellationTokenSource();

        const refresh = provider.getConfigInfo({
            cliPath: '/usr/bin/aspire',
            suppressErrors: true,
            forceRefresh: true,
            cancellationToken: cancellation.token,
        });
        const shared = provider.getConfigInfo({
            cliPath: '/usr/bin/aspire',
            suppressErrors: true,
        });

        assert.strictEqual(probeOptions.length, 2);
        cancellation.cancel();
        assert.strictEqual(await refresh, null);
        sinon.assert.calledOnceWithExactly(terminateStub, childProcesses[0], 'cancelled aspire config info command');

        probeOptions[1].stdoutCallback?.(JSON.stringify({
            localSettingsPath: '/workspace/aspire.config.json',
            globalSettingsPath: '/home/user/.aspire/aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [lsJsonStreamCapability],
        }));
        probeOptions[1].exitCallback?.(0);

        assert.deepStrictEqual((await shared)?.capabilities, [lsJsonStreamCapability]);
    });

    test('cancelling one shared caller does not terminate the shared probe', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let probeOptions: cliModule.SpawnProcessOptions | undefined;
        const childProcess = { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            probeOptions = options;
            return childProcess;
        });
        const terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        const provider = new ConfigInfoProvider(terminalProvider);
        const cancellation = new vscode.CancellationTokenSource();

        const cancelledCaller = provider.getConfigInfo({
            cliPath: '/usr/bin/aspire',
            suppressErrors: true,
            cancellationToken: cancellation.token,
        });
        const unrelatedCaller = provider.getConfigInfo({
            cliPath: '/usr/bin/aspire',
            suppressErrors: true,
        });

        cancellation.cancel();
        assert.strictEqual(await cancelledCaller, null);
        assert.strictEqual(terminateStub.called, false);

        probeOptions?.stdoutCallback?.(JSON.stringify({
            localSettingsPath: '/workspace/aspire.config.json',
            globalSettingsPath: '/home/user/.aspire/aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [lsJsonStreamCapability],
        }));
        probeOptions?.exitCallback?.(0);

        assert.deepStrictEqual((await unrelatedCaller)?.capabilities, [lsJsonStreamCapability]);
    });

    test('a cancelled force refresh preserves the last successful cached result', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/unused/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const probeOptions: cliModule.SpawnProcessOptions[] = [];
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            if (options) {
                probeOptions.push(options);
            }
            return { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        });
        sinon.stub(cliModule, 'terminateCliProcess').resolves();
        const provider = new ConfigInfoProvider(terminalProvider);

        const initial = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });
        probeOptions[0].stdoutCallback?.(JSON.stringify({
            localSettingsPath: '/workspace/aspire.config.json',
            globalSettingsPath: '/home/user/.aspire/aspire.config.json',
            availableFeatures: [],
            localSettingsSchema: { properties: [] },
            globalSettingsSchema: { properties: [] },
            capabilities: [lsJsonStreamCapability],
        }));
        probeOptions[0].exitCallback?.(0);
        await initial;

        const cancellation = new vscode.CancellationTokenSource();
        const refresh = provider.getConfigInfo({
            cliPath: '/usr/bin/aspire',
            suppressErrors: true,
            forceRefresh: true,
            cancellationToken: cancellation.token,
        });
        cancellation.cancel();
        assert.strictEqual(await refresh, null);

        const cached = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });
        if (probeOptions[2]) {
            probeOptions[2].stdoutCallback?.(JSON.stringify({
                localSettingsPath: '/workspace/aspire.config.json',
                globalSettingsPath: '/home/user/.aspire/aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [],
            }));
            probeOptions[2].exitCallback?.(0);
        }

        assert.deepStrictEqual((await cached)?.capabilities, [lsJsonStreamCapability]);
        assert.strictEqual(probeOptions.length, 2);
    });

    test('caller timeout does not cancel a newer shared probe after delayed path resolution', async () => {
        const clock = sinon.useFakeTimers();
        let resolveCliPath: ((cliPath: string) => void) | undefined;
        const terminalProvider = {
            getAspireCliExecutablePath: () => new Promise<string>(resolve => {
                resolveCliPath = resolve;
            }),
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let probeOptions: cliModule.SpawnProcessOptions | undefined;
        const kill = sinon.stub().returns(true);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            probeOptions = options;
            return { kill } as unknown as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        try {
            const delayedCaller = provider.getConfigInfo({ suppressErrors: true });
            await clock.tickAsync(25_000);

            const newerCaller = provider.getConfigInfo({
                cliPath: '/usr/bin/aspire',
                suppressErrors: true,
            });
            resolveCliPath?.('/usr/bin/aspire');
            await clock.tickAsync(0);

            assert.strictEqual(spawnStub.callCount, 1);
            await clock.tickAsync(5_000);
            assert.strictEqual(await delayedCaller, null);
            assert.strictEqual(kill.callCount, 0);

            probeOptions?.stdoutCallback?.(JSON.stringify({
                localSettingsPath: '/workspace/aspire.config.json',
                globalSettingsPath: '/home/user/.aspire/aspire.config.json',
                availableFeatures: [],
                localSettingsSchema: { properties: [] },
                globalSettingsSchema: { properties: [] },
                capabilities: [lsJsonStreamCapability],
            }));
            probeOptions?.exitCallback?.(0);

            assert.deepStrictEqual((await newerCaller)?.capabilities, [lsJsonStreamCapability]);
            assert.strictEqual(kill.callCount, 0);
        }
        finally {
            clock.restore();
        }
    });

    test('getConfigInfo stops a hung CLI after timeout', async () => {
        const clock = sinon.useFakeTimers();
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/usr/bin/aspire',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        let errorCallback: ((error: Error) => void) | undefined;
        let childProcess: EventEmitter;
        const kill = sinon.stub().callsFake(() => {
            errorCallback?.(new Error('Process terminated.'));
            childProcess.emit('exit', null);
            return true;
        });
        sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            errorCallback = options?.errorCallback;
            childProcess = Object.assign(new EventEmitter(), {
                killed: false,
                exitCode: null,
                signalCode: null,
                kill,
            });
            return childProcess as unknown as ChildProcessWithoutNullStreams;
        });
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage');
        const provider = new ConfigInfoProvider(terminalProvider);

        try {
            const configInfoPromise = provider.getConfigInfo();
            await clock.tickAsync(30_000);

            assert.strictEqual(await configInfoPromise, null);
            assert.strictEqual(kill.callCount, 1);
            assert.strictEqual(showErrorMessage.callCount, 1);
            assert.strictEqual(showErrorMessage.firstCall.args[0], 'Aspire config info timed out after 30 seconds.');
        }
        finally {
            clock.restore();
        }
    });

    test('getConfigInfo terminates the Windows CLI process tree after timeout', async () => {
        const clock = sinon.useFakeTimers();
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const terminalProvider = {
            getAspireCliExecutablePath: async () => 'C:\\tools\\aspire.cmd',
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const kill = sinon.stub().returns(true);
        const childProcess = Object.assign(new EventEmitter(), {
            pid: 4242,
            killed: false,
            exitCode: null,
            signalCode: null,
            kill,
        });
        sinon.stub(cliModule, 'spawnCliProcess').returns(childProcess as unknown as ChildProcessWithoutNullStreams);
        const taskkillCalls: Array<{ command: string; args: string[]; stdio: unknown; windowsHide: boolean | undefined }> = [];
        const spawnProcessStub = sinon.stub(nodeChildProcess, 'spawn').callsFake((command: string, args?: readonly string[], options?: nodeChildProcess.SpawnOptions) => {
            taskkillCalls.push({
                command,
                args: [...(args ?? [])],
                stdio: options?.stdio,
                windowsHide: options?.windowsHide,
            });

            return Object.assign(new EventEmitter(), {
                unref: () => { },
            }) as nodeChildProcess.ChildProcess;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        try {
            const configInfoPromise = provider.getConfigInfo({ suppressErrors: true });
            await clock.tickAsync(30_000);

            assert.strictEqual(await configInfoPromise, null);
            assert.deepStrictEqual(taskkillCalls, [{
                command: 'taskkill.exe',
                args: ['/pid', '4242', '/t'],
                stdio: 'ignore',
                windowsHide: true,
            }]);
            assert.strictEqual(kill.callCount, 0);
            childProcess.emit('exit', null);
        }
        finally {
            spawnProcessStub.restore();
            platformStub.restore();
            clock.restore();
        }
    });
});
