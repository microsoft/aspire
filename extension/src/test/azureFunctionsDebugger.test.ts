import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { prepareDebugSession } from '../debugger/debuggerExtensions';
import { azureFunctionsDebuggerExtension } from '../debugger/languages/azureFunctions';
import { DotNetService } from '../debugger/languages/dotnet';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { AspireResourceExtendedDebugConfiguration, AzureFunctionsLaunchConfiguration, EnvVar, LaunchOptions } from '../dcp/types';
import { azureFunctionsCmdDelayedExpansion, azureFunctionsCmdPercentArgument, azureFunctionsUnsupportedTaskShell } from '../loc/strings';

suite('Azure Functions Debugger Extension Tests', () => {
    setup(() => {
        sinon.stub(process, 'kill').returns(true);
    });

    teardown(() => {
        cleanupRun('azure-functions-test-run');
        sinon.restore();
    });

    test('builds the project and starts func host with HTTPS arguments in run mode', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const certificatePath = path.join('/workspace with spaces', 'FunctionsApp', 'aspire-functions-https.pfx');
        const getDotNetTargetPath = sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        const buildDotNetProject = sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const debugConfiguration = createDebugConfiguration(projectPath, ['--cert', certificatePath, '--password', ')456Y7R.D*S3Fwdr7mAv-p']);

        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe' });
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            debugConfiguration.args as string[],
            createEnvironmentVariables(),
            createLaunchOptions(false),
            debugConfiguration);

        assert.ok(getDotNetTargetPath.calledOnceWith(projectPath));
        assert.ok(buildDotNetProject.calledOnceWith(projectPath));
        sinon.assert.callOrder(buildDotNetProject, getDotNetTargetPath, startFuncProcess);
        assert.ok(startFuncProcess.calledOnceWith(
            path.dirname(targetPath),
            ['--cert', `"${certificatePath}"`, '--password', '")456Y7R.D*S3Fwdr7mAv-p"'],
            {
                AzureWebJobsStorage: 'UseDevelopmentStorage=true',
                ASPIRE_HTTPS_PORTS: '7042',
            }));
        assert.ok(resourceDebugSession);
        assert.strictEqual(resourceDebugSession.id, 'azure-functions-test-run');
        assert.strictEqual(resourceDebugSession.processId, 4242);
        assert.strictEqual(debugConfiguration.processId, undefined);

        await resourceDebugSession.stopSession();
        assert.strictEqual(await resourceDebugSession.termination, -1);
    });

    test('quotes HTTPS arguments for a configured cmd task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const debugConfiguration = createDebugConfiguration(projectPath, ['--password', ')456Y7R.D*S3Fwdr7mAv-p']);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\cmd.exe', args: ['/d', '/v:on', '/c'] });
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            debugConfiguration.args as string[],
            [],
            createLaunchOptions(false),
            debugConfiguration);

        assert.ok(startFuncProcess.calledOnceWith(
            path.dirname(targetPath),
            ['--password', '")456Y7R.D*S3Fwdr7mAv-p"'],
            {}));
    });

    test('quotes backslashes and apostrophes for a configured POSIX task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const debugConfiguration = createDebugConfiguration(projectPath, ['--password', "a\\b'c"]);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('linux', { path: '/bin/bash' });
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            debugConfiguration.args as string[],
            [],
            createLaunchOptions(false),
            debugConfiguration);

        assert.ok(startFuncProcess.calledOnceWith(
            path.dirname(targetPath),
            ['--password', "'a\\b'\"'\"'c'"],
            {}));
    });

    test('quotes a backslash before an apostrophe for a configured fish task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const password = String.raw`prefix\'; touch /tmp/owned`;
        const debugConfiguration = createDebugConfiguration(projectPath, ['--password', password]);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('linux', { path: '/usr/bin/fish' });
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            debugConfiguration.args as string[],
            [],
            createLaunchOptions(false),
            debugConfiguration);

        assert.ok(startFuncProcess.calledOnceWith(
            path.dirname(targetPath),
            ['--password', String.raw`'prefix\\\'; touch /tmp/owned'`],
            {}));
    });

    test('rejects percent expansion for a configured cmd task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const debugConfiguration = createDebugConfiguration(projectPath, ['--password', '%TEMP%']);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\cmd.exe' });
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        await assert.rejects(
            azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                [],
                createLaunchOptions(false),
                debugConfiguration),
            (error: Error) => error.message === azureFunctionsCmdPercentArgument);
        assert.ok(startFuncProcess.notCalled);
    });

    test('rejects exclamation mark arguments for a configured cmd task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const debugConfiguration = createDebugConfiguration(projectPath, ['--password', '!ASPIRE_PASSWORD!']);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\cmd.exe', args: ['/d', '/v:off', '/c'] });
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        await assert.rejects(
            azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                [],
                createLaunchOptions(false),
                debugConfiguration),
            (error: Error) => error.message === azureFunctionsCmdDelayedExpansion);
        assert.ok(startFuncProcess.notCalled);
    });

    test('rejects unsafe arguments for an unsupported task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const debugConfiguration = createDebugConfiguration(projectPath, ['--password', ')unsafe']);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\tools\\nu.exe' });
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        await assert.rejects(
            azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                [],
                createLaunchOptions(false),
                debugConfiguration),
            (error: Error) => error.message === azureFunctionsUnsupportedTaskShell);
        assert.ok(startFuncProcess.notCalled);
    });

    test('passes empty arguments through for an unsupported task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const debugConfiguration = createDebugConfiguration(projectPath);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\tools\\nu.exe' });
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            debugConfiguration.args as string[],
            [],
            createLaunchOptions(false),
            debugConfiguration);

        assert.ok(startFuncProcess.calledOnceWith(path.dirname(targetPath), [], {}));
    });

    test('passes universally safe arguments through for an unsupported task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const args = ['--verbose', '/workspace/cert.pfx'];
        const debugConfiguration = createDebugConfiguration(projectPath, args);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\tools\\nu.exe' });
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            debugConfiguration.args as string[],
            [],
            createLaunchOptions(false),
            debugConfiguration);

        assert.ok(startFuncProcess.calledOnceWith(path.dirname(targetPath), args, {}));
    });

    test('returns the already-started func host without launching CoreCLR in run mode', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const certificatePath = path.join('/workspace', 'FunctionsApp', 'aspire-functions-https.pfx');
        const aspireDebugSession = createAspireDebugSession();
        const startDebugging = sinon.stub(vscode.debug, 'startDebugging').resolves(false);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(sinon.stub().resolves({ success: true, processId: '4242' })));

        const preparedSession = await prepareDebugSession(
            { type: 'aspire', request: 'launch', name: 'Aspire', program: '' },
            createLaunchConfiguration(projectPath),
            ['--cert', certificatePath, '--password', 'secret-password'],
            createEnvironmentVariables(),
            createLaunchOptions(false, aspireDebugSession),
            azureFunctionsDebuggerExtension);

        assert.ok(preparedSession.alreadyStartedSession);
        assert.strictEqual(preparedSession.alreadyStartedSession.processId, 4242);
        const resourceDebugSession = aspireDebugSession.trackAlreadyStartedResourceSession(
            preparedSession.debugConfiguration,
            preparedSession.alreadyStartedSession);

        assert.strictEqual(resourceDebugSession?.id, 'azure-functions-test-run');
        assert.strictEqual(startDebugging.called, false);
        assert.strictEqual(preparedSession.debugConfiguration.processId, undefined);

        aspireDebugSession.dispose();
    });

    test('reports func host task exit in run mode', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const buildOutputPath = path.dirname(targetPath);
        const previousExecution = createFuncTaskExecution(buildOutputPath);
        const unrelatedExecution = createFuncTaskExecution(buildOutputPath, 'workspace', 'echo func');
        const taskExecution = createFuncTaskExecution(buildOutputPath);
        const taskEvents = stubFuncTaskEvents();
        const startFuncProcess = sinon.stub().callsFake(async () => {
            taskEvents.end(previousExecution, 143);
            taskEvents.start(unrelatedExecution);
            taskEvents.start(taskExecution);
            return { success: true, processId: '4242' };
        });

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(false),
            createDebugConfiguration(projectPath));

        assert.ok(resourceDebugSession);
        taskEvents.end(unrelatedExecution, 99);
        taskEvents.end(taskExecution, 17);

        assert.strictEqual(await resourceDebugSession.termination, 17);
        sinon.assert.calledOnce(taskExecution.terminate as sinon.SinonStub);
        sinon.assert.notCalled(previousExecution.terminate as sinon.SinonStub);
        sinon.assert.notCalled(unrelatedExecution.terminate as sinon.SinonStub);
    });

    test('reports a func host exit that occurs before the startup API returns', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const taskExecution = createFuncTaskExecution(path.dirname(targetPath));
        const taskEvents = stubFuncTaskEvents();
        const startFuncProcess = sinon.stub().callsFake(async () => {
            taskEvents.start(taskExecution);
            taskEvents.end(taskExecution, 23);
            return { success: true, processId: '4242' };
        });

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(false),
            createDebugConfiguration(projectPath));

        assert.ok(resourceDebugSession);
        assert.strictEqual(await resourceDebugSession.termination, 23);
    });

    for (const { platform, expectedExitCode } of [
        { platform: 'darwin', expectedExitCode: 0 },
        { platform: 'linux', expectedExitCode: 0 },
        { platform: 'win32', expectedExitCode: 143 },
    ] as const) {
        test(`normalizes func host SIGTERM task exit on ${platform}`, async () => {
            const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
            const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
            const taskExecution = createFuncTaskExecution(path.dirname(targetPath));
            const taskEvents = stubFuncTaskEvents();
            const startFuncProcess = sinon.stub().callsFake(async () => {
                taskEvents.start(taskExecution);
                return { success: true, processId: '4242' };
            });

            sinon.stub(process, 'platform').value(platform);
            sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
            sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
            installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

            const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                [],
                [],
                createLaunchOptions(false),
                createDebugConfiguration(projectPath));

            assert.ok(resourceDebugSession);
            taskEvents.end(taskExecution, 143);

            assert.strictEqual(await resourceDebugSession.termination, expectedExitCode);
        });
    }

    test('configures coreclr attach to the Azure Functions worker in debug mode', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const getDotNetTargetPath = sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        const buildDotNetProject = sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        const startFuncProcess = sinon.stub().resolves({ success: true, processId: '4242' });
        const debugConfiguration = createDebugConfiguration(projectPath, ['--verbose']);

        installAzureFunctionsExtensionStub(createAzureFunctionsApi(startFuncProcess));

        const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            debugConfiguration.args as string[],
            createEnvironmentVariables(),
            createLaunchOptions(true),
            debugConfiguration);

        assert.ok(getDotNetTargetPath.calledOnceWith(projectPath));
        assert.ok(buildDotNetProject.calledOnceWith(projectPath));
        assert.ok(startFuncProcess.calledOnceWith(
            path.dirname(targetPath),
            ['--verbose'],
            {
                AzureWebJobsStorage: 'UseDevelopmentStorage=true',
                ASPIRE_HTTPS_PORTS: '7042',
            }));
        assert.strictEqual(debugConfiguration.type, 'coreclr');
        assert.strictEqual(debugConfiguration.request, 'attach');
        assert.strictEqual(debugConfiguration.processId, '4242');
        assert.strictEqual(debugConfiguration.program, undefined);
        assert.strictEqual(debugConfiguration.args, undefined);
        assert.strictEqual(debugConfiguration.cwd, undefined);
        assert.strictEqual(debugConfiguration.console, undefined);
        assert.strictEqual(debugConfiguration.env, undefined);
        assert.strictEqual(resourceDebugSession, undefined);
    });

    test('surfaces Azure Functions API startup failures', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const debugConfiguration = createDebugConfiguration(projectPath);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        installAzureFunctionsExtensionStub(createAzureFunctionsApi(sinon.stub().resolves({ success: false, processId: '', error: 'func failed' })));

        await assert.rejects(
            azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                [],
                [],
                createLaunchOptions(false),
                debugConfiguration),
            /Azure Functions extension failed to start func host: func failed/);
    });
});

function createLaunchConfiguration(projectPath: string): AzureFunctionsLaunchConfiguration {
    return {
        type: 'azure-functions',
        mode: 'NoDebug',
        project_path: projectPath,
    };
}

function createDebugConfiguration(projectPath: string, args: string[] = []): AspireResourceExtendedDebugConfiguration {
    return {
        type: 'coreclr',
        request: 'launch',
        name: 'Run Azure Functions',
        program: projectPath,
        args,
        cwd: path.dirname(projectPath),
        env: {},
        justMyCode: false,
        stopAtEntry: false,
        noDebug: true,
        runId: 'azure-functions-test-run',
        debugSessionId: 'azure-functions-test-debug-session',
        console: 'internalConsole',
        isApphost: false
    };
}

function createLaunchOptions(debug: boolean, debugSession: AspireDebugSession = {} as AspireDebugSession): LaunchOptions {
    return {
        debug,
        runId: 'azure-functions-test-run',
        debugSessionId: 'azure-functions-test-debug-session',
        isApphost: false,
        debugSession
    };
}

function createEnvironmentVariables(): EnvVar[] {
    return [
        { name: 'AzureWebJobsStorage', value: 'UseDevelopmentStorage=true' },
        { name: 'ASPIRE_HTTPS_PORTS', value: '7042' },
    ];
}

function createFuncTaskExecution(buildOutputPath: string, source = 'func', commandLine = 'func host start'): vscode.TaskExecution {
    const task = new vscode.Task(
        { type: `func  ${buildOutputPath}` },
        vscode.TaskScope.Workspace,
        'func: host start',
        source,
        new vscode.ShellExecution(commandLine, { cwd: buildOutputPath }));
    return {
        task,
        terminate: sinon.stub(),
    } as unknown as vscode.TaskExecution;
}

function stubFuncTaskEvents(): {
    start(execution: vscode.TaskExecution): void;
    end(execution: vscode.TaskExecution, exitCode: number | undefined): void;
} {
    let startTaskProcess: ((event: vscode.TaskProcessStartEvent) => unknown) | undefined;
    let endTaskProcess: ((event: vscode.TaskProcessEndEvent) => unknown) | undefined;
    sinon.stub(vscode.tasks, 'onDidStartTaskProcess').callsFake(listener => {
        startTaskProcess = listener;
        return new vscode.Disposable(() => { });
    });
    sinon.stub(vscode.tasks, 'onDidEndTaskProcess').callsFake(listener => {
        endTaskProcess = listener;
        return new vscode.Disposable(() => { });
    });

    return {
        start: execution => startTaskProcess!({ execution, processId: 4242 }),
        end: (execution, exitCode) => endTaskProcess!({ execution, exitCode }),
    };
}

function stubTaskShell(platform: NodeJS.Platform, profile: { path: string; args?: string[] }): void {
    sinon.stub(process, 'platform').value(platform);
    const settingsPlatform = platform === 'win32' ? 'windows' : platform === 'darwin' ? 'osx' : 'linux';
    sinon.stub(vscode.workspace, 'getConfiguration').withArgs('terminal.integrated').returns({
        get: <T>(section: string): T | undefined =>
            section === `automationProfile.${settingsPlatform}` ? profile as T : undefined,
    } as unknown as vscode.WorkspaceConfiguration);
}

function createAzureFunctionsApi(startFuncProcess: sinon.SinonStub) {
    return {
        apiVersion: '1.10.0',
        startFuncProcess,
    };
}

function installAzureFunctionsExtensionStub(api: ReturnType<typeof createAzureFunctionsApi>): void {
    sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
        return extensionId === 'ms-azuretools.vscode-azurefunctions'
            ? {
                id: extensionId,
                isActive: true,
                exports: {
                    getApi: sinon.stub().withArgs('~1.10.0').returns(api)
                },
                activate: async () => undefined
            } as unknown as vscode.Extension<unknown>
            : undefined;
    });
}

function createAspireDebugSession(): AspireDebugSession {
    const parentDebugSession = {
        id: 'azure-functions-test-debug-session',
        type: 'aspire',
        name: 'Aspire',
        workspaceFolder: undefined,
        configuration: {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/AppHost/AppHost.csproj',
        },
        customRequest: sinon.stub(),
        getDebugProtocolBreakpoint: sinon.stub(),
    } as vscode.DebugSession;
    const terminalProvider = {
        isDebugConfigEnvironmentLoggingEnabled: () => false,
    };

    return new AspireDebugSession(parentDebugSession, {} as any, { sendNotification: sinon.stub() } as any, terminalProvider as any, () => { });
}
