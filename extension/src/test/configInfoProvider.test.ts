import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import { EventEmitter } from 'events';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { ConfigInfoProvider, getConfigInfo, parseConfigInfoOutput } from '../utils/configInfoProvider';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliModule from '../debugger/languages/cli';

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
