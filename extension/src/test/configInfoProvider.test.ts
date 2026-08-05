import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { ConfigInfoProvider, getConfigInfo, parseConfigInfoOutput } from '../utils/configInfoProvider';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliModule from '../debugger/languages/cli';
import { lsJsonStreamCapability } from '../types/configInfo';

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

    test('getConfigInfo deduplicates concurrent probes for the same CLI path', async () => {
        const terminalProvider = createTerminalProvider();
        let probeOptions: cliModule.SpawnProcessOptions | undefined;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            probeOptions = options;
            return { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const firstProbe = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });
        const secondProbe = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });

        assert.strictEqual(spawnStub.callCount, 1);
        completeProbe(probeOptions, [lsJsonStreamCapability]);
        assert.deepStrictEqual((await firstProbe)?.capabilities, [lsJsonStreamCapability]);
        assert.deepStrictEqual((await secondProbe)?.capabilities, [lsJsonStreamCapability]);
    });

    test('getConfigInfo isolates concurrent probes and caches by CLI path', async () => {
        const terminalProvider = createTerminalProvider();
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

        completeProbe(optionsByCliPath.get('/new/aspire'), [lsJsonStreamCapability]);
        completeProbe(optionsByCliPath.get('/old/aspire'), []);

        assert.deepStrictEqual((await oldProbe)?.capabilities, []);
        assert.deepStrictEqual((await newProbe)?.capabilities, [lsJsonStreamCapability]);
        assert.deepStrictEqual((await provider.getConfigInfo({ cliPath: '/new/aspire' }))?.capabilities, [lsJsonStreamCapability]);
        assert.strictEqual(spawnStub.callCount, 2);
    });

    test('force refresh replaces an in-flight probe without caching its stale completion', async () => {
        const terminalProvider = createTerminalProvider();
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
        completeProbe(probeOptions[1], [lsJsonStreamCapability]);
        assert.deepStrictEqual((await refreshedProbe)?.capabilities, [lsJsonStreamCapability]);

        completeProbe(probeOptions[0], []);
        assert.deepStrictEqual((await staleProbe)?.capabilities, []);
        assert.deepStrictEqual((await provider.getConfigInfo({ cliPath: '/usr/bin/aspire' }))?.capabilities, [lsJsonStreamCapability]);
        assert.strictEqual(spawnStub.callCount, 2);
    });

    test('getConfigInfo does not cache failed probes', async () => {
        const terminalProvider = createTerminalProvider();
        const probeOptions: cliModule.SpawnProcessOptions[] = [];
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            if (options) {
                probeOptions.push(options);
            }
            return { kill: () => true } as unknown as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        const failedProbe = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });
        probeOptions[0].exitCallback?.(1);
        assert.strictEqual(await failedProbe, null);

        const retryProbe = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });
        assert.strictEqual(spawnStub.callCount, 2);
        completeProbe(probeOptions[1], [lsJsonStreamCapability]);
        assert.deepStrictEqual((await retryProbe)?.capabilities, [lsJsonStreamCapability]);
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

            completeProbe(probeOptions, [lsJsonStreamCapability]);
            assert.deepStrictEqual((await newerCaller)?.capabilities, [lsJsonStreamCapability]);
            assert.strictEqual(kill.callCount, 0);
        }
        finally {
            clock.restore();
        }
    });

    test('timed-out probes are stopped and retried', async () => {
        const clock = sinon.useFakeTimers();
        const terminalProvider = createTerminalProvider();
        const probeOptions: cliModule.SpawnProcessOptions[] = [];
        const kill = sinon.stub().returns(true);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').callsFake((_terminalProvider, _command, _args, options) => {
            if (options) {
                probeOptions.push(options);
            }
            return { kill } as unknown as ChildProcessWithoutNullStreams;
        });
        const provider = new ConfigInfoProvider(terminalProvider);

        try {
            const timedOutProbe = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });
            await clock.tickAsync(30_000);

            assert.strictEqual(await timedOutProbe, null);
            assert.strictEqual(kill.callCount, 1);

            const retryProbe = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });
            assert.strictEqual(spawnStub.callCount, 2);
            completeProbe(probeOptions[1], [lsJsonStreamCapability]);
            assert.deepStrictEqual((await retryProbe)?.capabilities, [lsJsonStreamCapability]);
        }
        finally {
            clock.restore();
        }
    });

    test('dispose stops an in-flight probe and resolves its callers', async () => {
        const terminalProvider = createTerminalProvider();
        const kill = sinon.stub().returns(true);
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns({ kill } as unknown as ChildProcessWithoutNullStreams);
        const provider = new ConfigInfoProvider(terminalProvider);
        const configInfo = provider.getConfigInfo({ cliPath: '/usr/bin/aspire', suppressErrors: true });

        provider.dispose();

        assert.strictEqual(await configInfo, null);
        assert.strictEqual(kill.callCount, 1);
        assert.strictEqual(await provider.getConfigInfo({ cliPath: '/usr/bin/aspire' }), null);
        assert.strictEqual(spawnStub.callCount, 1);
    });

    test('dispose cancels pending CLI path resolution before a process starts', async () => {
        const terminalProvider = {
            getAspireCliExecutablePath: () => new Promise<string>(() => { }),
            createEnvironment: () => ({}),
        } as unknown as AspireTerminalProvider;
        const spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
        const provider = new ConfigInfoProvider(terminalProvider);
        const configInfo = provider.getConfigInfo({ suppressErrors: true });

        provider.dispose();

        assert.strictEqual(await configInfo, null);
        assert.strictEqual(spawnStub.callCount, 0);
    });
});

function createTerminalProvider(): AspireTerminalProvider {
    return {
        getAspireCliExecutablePath: async () => '/unused/aspire',
        createEnvironment: () => ({}),
    } as unknown as AspireTerminalProvider;
}

function completeProbe(options: cliModule.SpawnProcessOptions | undefined, capabilities: string[]): void {
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
