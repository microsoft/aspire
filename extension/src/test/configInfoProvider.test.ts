import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import { EventEmitter } from 'events';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { ConfigInfoProvider, getConfigInfo, parseConfigInfoOutput } from '../utils/configInfoProvider';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliModule from '../utils/process/cliProcess';
import { AppHostDiscoveryService } from '../utils/appHostDiscovery';
import { AppHostDataRepository } from '../data/AppHostDataRepository';
import { describeIncludeDisabledCommandsCapability, lsJsonStreamCapability } from '../types/configInfo';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';

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
