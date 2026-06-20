import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireDebugSession, buildAspireCommandArgs, getLoggableDebugConfiguration } from '../debugger/AspireDebugSession';
import { AspireResourceExtendedDebugConfiguration } from '../dcp/types';

suite('AspireDebugSession tests', () => {
    teardown(() => sinon.restore());

    test('suppresses the Aspire CLI first-run banner for extension-managed launches', () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        const spawnStub = sinon.stub(aspireDebugSession, 'spawnAspireCommand').resolves();

        aspireDebugSession.handleMessage({ command: 'launch', seq: 1, arguments: { noDebug: false } });

        assert.strictEqual(spawnStub.calledOnce, true);
        assert.deepStrictEqual(spawnStub.firstCall.args[0], [
            'run',
            '--start-debug-session',
            '--nologo',
            '--apphost',
            '/workspace/apphost.cs',
        ]);
    });

    test('redacts debug configuration environment fields from logs by default', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            env: {
                SECRET_TOKEN: 'env-secret',
            },
            environmentVariables: 'SECRET_TOKEN=maui-secret',
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, false);

        assert.strictEqual(loggableConfig.env, '<redacted>');
        assert.strictEqual(loggableConfig.environmentVariables, '<redacted>');
    });

    test('redacts MAUI environmentVariables even when environment logging is enabled', () => {
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            env: {
                SECRET_TOKEN: 'env-secret',
            },
            environmentVariables: 'SECRET_TOKEN=maui-secret',
        } as AspireResourceExtendedDebugConfiguration;

        const loggableConfig = getLoggableDebugConfiguration(debugConfig, true);

        assert.deepStrictEqual(loggableConfig.env, { SECRET_TOKEN: 'env-secret' });
        assert.strictEqual(loggableConfig.environmentVariables, '<redacted>');
    });

    test('responds to breakpoint requests with a DAP breakpoint body', () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.cs',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isCliDebugLoggingEnabled: () => false,
        };
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });
        const messages: any[] = [];
        const subscription = aspireDebugSession.onDidSendMessage(message => messages.push(message));

        try {
            aspireDebugSession.handleMessage({
                command: 'setBreakpoints',
                seq: 4,
                arguments: {
                    breakpoints: [
                        { line: 27, column: 5 },
                    ],
                },
            });

            assert.deepStrictEqual(messages, [
                {
                    type: 'response',
                    seq: 1,
                    request_seq: 4,
                    success: true,
                    command: 'setBreakpoints',
                    body: {
                        breakpoints: [
                            {
                                id: 1,
                                verified: false,
                                line: 27,
                                column: 5,
                            },
                        ],
                    },
                },
            ]);
        }
        finally {
            subscription.dispose();
        }
    });

    test('starts resource debug sessions from the workspace folder containing the project', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/MauiAppHost/MauiAppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const workspaceFolder = {
            uri: vscode.Uri.file('/workspace'),
            name: 'workspace',
            index: 0,
        } as vscode.WorkspaceFolder;
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'coreclr',
            name: 'API',
            request: 'launch',
            program: '/workspace/Api/Api.dll',
            cwd: '/workspace/Api',
        } as AspireResourceExtendedDebugConfiguration;
        const getWorkspaceFolderStub = sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(workspaceFolder);
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(false);

        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        await aspireDebugSession.startAndGetDebugSession(debugConfig);

        assert.strictEqual(getWorkspaceFolderStub.calledOnceWith(vscode.Uri.file('/workspace/Api')), true);
        assert.strictEqual(startDebuggingStub.calledOnce, true);
        assert.strictEqual(startDebuggingStub.firstCall.args[0], workspaceFolder);
        assert.strictEqual(startDebuggingStub.firstCall.args[2], parentDebugSession);
    });

    test('retries MAUI resource debug sessions when the first start attempt is canceled', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/MauiAppHost/MauiAppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            project: '/workspace/MauiApp/MauiApp.csproj',
            cwd: '/workspace/MauiApp',
        } as AspireResourceExtendedDebugConfiguration;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging');
        startDebuggingStub.onFirstCall().resolves(false);
        startDebuggingStub.onSecondCall().callsFake(async (_folder, configuration) => {
            startSessionCallback?.({
                id: 'maui-session',
                type: 'maui',
                name: 'MAUI',
                configuration: configuration as vscode.DebugConfiguration,
            } as vscode.DebugSession);
            return true;
        });
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const sessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        await clock.tickAsync(5000);
        const session = await sessionPromise;

        assert.strictEqual(session?.id, 'maui-session');
        assert.strictEqual(startDebuggingStub.callCount, 2);
        assert.strictEqual(startDebuggingStub.firstCall.args[2], undefined);
        assert.strictEqual(startDebuggingStub.secondCall.args[2], undefined);
    });

    test('does not retry MAUI resource debug sessions while the first start is still pending', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        let resolveStart: ((value: boolean) => void) | undefined;
        const startDebuggingPromise = new Promise<boolean>(resolve => {
            resolveStart = resolve;
        });
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/MauiAppHost/MauiAppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            project: '/workspace/MauiApp/MauiApp.csproj',
            cwd: '/workspace/MauiApp',
        } as AspireResourceExtendedDebugConfiguration;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        const startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').returns(startDebuggingPromise);
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const sessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        await clock.tickAsync(95_001);
        const startAttemptsWhilePending = startDebuggingStub.callCount;
        startSessionCallback?.({
            id: 'maui-session',
            type: 'maui',
            name: 'MAUI',
            configuration: debugConfig as vscode.DebugConfiguration,
        } as vscode.DebugSession);
        resolveStart!(true);
        const session = await sessionPromise;

        assert.strictEqual(session?.id, 'maui-session');
        assert.strictEqual(startAttemptsWhilePending, 1);
        assert.strictEqual(startDebuggingStub.firstCall.args[2], undefined);
    });

    test('stops MAUI resource debug sessions that start after Aspire session disposal', async () => {
        let startSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
        let resolveStart: ((value: boolean) => void) | undefined;
        const startDebuggingPromise = new Promise<boolean>(resolve => {
            resolveStart = resolve;
        });
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/MauiAppHost/MauiAppHost.csproj',
                command: 'run',
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub(),
        };
        const terminalProvider = {
            isDebugConfigEnvironmentLoggingEnabled: () => false,
        };
        const debugConfig = {
            runId: 'run-1',
            debugSessionId: 'debug-1',
            type: 'maui',
            name: 'MAUI',
            request: 'launch',
            project: '/workspace/MauiApp/MauiApp.csproj',
            cwd: '/workspace/MauiApp',
        } as AspireResourceExtendedDebugConfiguration;
        const lateMauiSession = {
            id: 'maui-session',
            type: 'maui',
            name: 'MAUI',
            configuration: debugConfig as vscode.DebugConfiguration,
        } as vscode.DebugSession;
        sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            startSessionCallback = callback;
            return { dispose: sinon.stub() };
        });
        sinon.stub(vscode.debug, 'startDebugging').returns(startDebuggingPromise);
        const stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const aspireDebugSession = new AspireDebugSession(parentDebugSession as unknown as vscode.DebugSession, {} as any, {} as any, terminalProvider as any, () => { });

        const sessionPromise = aspireDebugSession.startAndGetDebugSession(debugConfig);
        await Promise.resolve();
        aspireDebugSession.dispose();
        startSessionCallback?.(lateMauiSession);
        resolveStart!(true);
        const session = await sessionPromise;

        assert.strictEqual(session, undefined);
        assert.strictEqual(stopDebuggingStub.calledWith(lateMauiSession), true);
    });

    suite('buildAspireCommandArgs', () => {
        test('appends extension arguments when command has no app argument separator', () => {
            const args = buildAspireCommandArgs('run', ['--isolated'], ['--start-debug-session', '--apphost', '/workspace/AppHost.csproj']);

            assert.deepStrictEqual(args, ['run', '--isolated', '--start-debug-session', '--apphost', '/workspace/AppHost.csproj']);
        });

        test('inserts extension arguments before app argument separator', () => {
            const args = buildAspireCommandArgs('run', ['--isolated', '--', '--custom-arg', 'value'], ['--apphost', '/workspace/AppHost.csproj']);

            assert.deepStrictEqual(args, ['run', '--isolated', '--apphost', '/workspace/AppHost.csproj', '--', '--custom-arg', 'value']);
        });
    });
});
