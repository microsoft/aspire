import * as assert from 'assert';
import { EventEmitter } from 'events';
import fs = require('fs');
import http = require('http');
import https = require('https');
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { prepareDebugSession } from '../debugger/debuggerExtensions';
import { azureFunctionsDebuggerExtension } from '../debugger/languages/azureFunctions';
import { DotNetService } from '../debugger/languages/dotnet';
import { cleanupRun, registerRunCleanup } from '../debugger/runCleanupRegistry';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { AspireResourceExtendedDebugConfiguration, AzureFunctionsLaunchConfiguration, EnvVar, LaunchOptions } from '../dcp/types';
import { azureFunctionsCmdDelayedExpansion, azureFunctionsCmdPercentArgument, azureFunctionsInvalidProcessId, azureFunctionsUnsupportedTaskShell } from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';

suite('Azure Functions Debugger Extension Tests', () => {
    const defaultBuildOutputPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0');
    const defaultTempPath = path.join(defaultBuildOutputPath, 'aspire-functions-worker-test');
    const defaultWorkerPidArgument = path.join('aspire-functions-worker-test', 'worker-startup.json').split(path.sep).join('/');
    const defaultWorkerPid = 4343;
    let activateAzureFunctionsExtension: sinon.SinonStub;
    let fsMkdtempSync: sinon.SinonStub;
    let fsReadFileSync: sinon.SinonStub;
    let fsRmSync: sinon.SinonStub;
    let funcHostStatus: string;
    let getAzureFunctionsApi: sinon.SinonStub;
    let taskHarness: ReturnType<typeof stubRegisteredFuncTaskExecution>;
    setup(() => {
        sinon.stub(process, 'kill').returns(true);
        fsMkdtempSync = sinon.stub(fs, 'mkdtempSync').returns(defaultTempPath);
        fsReadFileSync = sinon.stub(fs, 'readFileSync').returns(
            `${JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: defaultWorkerPid })}\n`);
        fsRmSync = sinon.stub(fs, 'rmSync');
        funcHostStatus = 'Running';
        taskHarness = stubRegisteredFuncTaskExecution();
        ({ activate: activateAzureFunctionsExtension, getApi: getAzureFunctionsApi } = installAzureFunctionsExtensionStub());
    });

    teardown(() => {
        cleanupRun('azure-functions-test-run');
        sinon.restore();
    });

    test('builds the project and returns the worker process in run mode', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const certificatePath = path.join('/workspace with spaces', 'FunctionsApp', 'aspire-functions-https.pfx');
        const getDotNetTargetPath = sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        const buildDotNetProject = sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const debugConfiguration = createDebugConfiguration(projectPath, ['--port', String(statusServer.port), '--cert', certificatePath, '--password', ')456Y7R.D*S3Fwdr7mAv-p']);

        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe' });

        try {
            const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                createEnvironmentVariables(),
                createLaunchOptions(false),
                debugConfiguration);

            assert.ok(getDotNetTargetPath.calledOnceWith(projectPath));
            assert.ok(buildDotNetProject.calledOnceWith(projectPath));
            sinon.assert.callOrder(buildDotNetProject, getDotNetTargetPath, activateAzureFunctionsExtension, vscode.tasks.executeTask as sinon.SinonStub);
            assert.ok(resourceDebugSession);
            assert.strictEqual(resourceDebugSession.id, 'azure-functions-test-run');
            assert.strictEqual(resourceDebugSession.processId, defaultWorkerPid);
            assert.strictEqual(debugConfiguration.processId, undefined);

            const stop = resourceDebugSession.stopSession();
            taskHarness.end(0);
            await stop;
            assert.strictEqual(await resourceDebugSession.termination, -1);
            sinon.assert.notCalled(process.kill as sinon.SinonStub);
        } finally {
            await close(statusServer.server);
        }
    });

    test('executes a registered func host task with a VS Code-accepted definition in run mode', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const certificatePath = path.join('/workspace with spaces', 'FunctionsApp', 'aspire-functions-https.pfx');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const debugConfiguration = createDebugConfiguration(projectPath, ['--port', String(statusServer.port), '--cert', certificatePath, '--password', ')456Y7R.D*S3Fwdr7mAv-p']);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe' });

        try {
            const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                createEnvironmentVariables(),
                createLaunchOptions(false),
                debugConfiguration);

            const executedTask = taskHarness.getExecutedTask();
            assert.ok(executedTask, 'Expected createDebugSessionConfigurationCallback to execute a registered func task');

            const shellExecution = executedTask.execution as vscode.ShellExecution;
            assert.deepStrictEqual(executedTask.definition, {
                type: 'func',
                command: 'host start',
                args: [
                    '--port', String(statusServer.port),
                    '--cert', certificatePath,
                    '--password', ')456Y7R.D*S3Fwdr7mAv-p',
                    '--enable-json-output',
                    '--json-output-file', defaultWorkerPidArgument
                ]
            });
            assert.strictEqual(executedTask.name, 'func: host start');
            assert.strictEqual(executedTask.source, 'func');
            assert.ok(shellExecution instanceof vscode.ShellExecution);
            assert.strictEqual(shellExecution.options?.cwd, path.dirname(targetPath));
            assert.deepStrictEqual(shellExecution.options?.env, {
                AzureWebJobsStorage: 'UseDevelopmentStorage=true',
                ASPIRE_HTTPS_PORTS: '7042',
            });
            assert.strictEqual(
                shellExecution.commandLine,
                `func host start --port ${statusServer.port} --cert "${certificatePath}" --password ")456Y7R.D*S3Fwdr7mAv-p" --enable-json-output --json-output-file ${defaultWorkerPidArgument}`);
            sinon.assert.callOrder(
                activateAzureFunctionsExtension,
                vscode.tasks.onDidStartTaskProcess as sinon.SinonStub,
                vscode.tasks.onDidEndTaskProcess as sinon.SinonStub,
                vscode.tasks.executeTask as sinon.SinonStub);
            sinon.assert.notCalled(getAzureFunctionsApi);

            const stop = resourceDebugSession?.stopSession();
            taskHarness.end(0);
            await stop;
            sinon.assert.calledOnce(taskHarness.getExecution()!.terminate as sinon.SinonStub);
            sinon.assert.notCalled(process.kill as sinon.SinonStub);
        } finally {
            await close(statusServer.server);
        }
    });

    test('waits for the exact func task exit when stopping a run session and remains idempotent', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const buildOutputPath = path.dirname(targetPath);
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const unrelatedExecution = createFuncTaskExecution(buildOutputPath, 'workspace', 'echo func');

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        try {
            const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                ['--port', String(statusServer.port)],
                [],
                createLaunchOptions(false),
                createDebugConfiguration(projectPath));

            assert.ok(resourceDebugSession);
            let firstStopResolved = false;
            let secondStopResolved = false;
            const firstStop = resourceDebugSession.stopSession().then(() => firstStopResolved = true);
            const secondStop = resourceDebugSession.stopSession().then(() => secondStopResolved = true);

            await Promise.resolve();
            assert.strictEqual(firstStopResolved, false);
            assert.strictEqual(secondStopResolved, false);
            sinon.assert.calledOnce(taskHarness.getExecution()!.terminate as sinon.SinonStub);

            taskHarness.endExecution(unrelatedExecution, 0);
            await Promise.resolve();
            assert.strictEqual(firstStopResolved, false);
            assert.strictEqual(secondStopResolved, false);

            taskHarness.end(0);
            await Promise.all([firstStop, secondStop]);

            assert.strictEqual(firstStopResolved, true);
            assert.strictEqual(secondStopResolved, true);
            assert.strictEqual(await resourceDebugSession.termination, -1);
            sinon.assert.calledOnce(taskHarness.getExecution()!.terminate as sinon.SinonStub);
            sinon.assert.notCalled(process.kill as sinon.SinonStub);
        } finally {
            await close(statusServer.server);
        }
    });

    test('run cleanup terminates the exact func task once without killing the worker', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        try {
            const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                ['--port', String(statusServer.port)],
                [],
                createLaunchOptions(false),
                createDebugConfiguration(projectPath));

            assert.ok(resourceDebugSession);
            cleanupRun('azure-functions-test-run');
            cleanupRun('azure-functions-test-run');
            taskHarness.end(0);
            await resourceDebugSession.termination;

            sinon.assert.calledOnce(taskHarness.getExecution()!.terminate as sinon.SinonStub);
            sinon.assert.notCalled(process.kill as sinon.SinonStub);
        } finally {
            await close(statusServer.server);
        }
    });

    test('finishes run cleanup after the bounded shutdown period when the exact task exit is missing', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        const warn = sinon.stub(extensionLogOutputChannel, 'warn');
        let clock: sinon.SinonFakeTimers | undefined;

        try {
            const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                ['--port', String(statusServer.port)],
                [],
                createLaunchOptions(false),
                createDebugConfiguration(projectPath));

            assert.ok(resourceDebugSession);
            clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
            const stop = resourceDebugSession.stopSession();
            await clock.tickAsync(30_000);
            await stop;

            assert.strictEqual(await resourceDebugSession.termination, -1);
            sinon.assert.calledOnce(taskHarness.getExecution()!.terminate as sinon.SinonStub);
            sinon.assert.calledWithMatch(warn, sinon.match(/did not report an exit within 30 seconds/));
            sinon.assert.notCalled(process.kill as sinon.SinonStub);
        } finally {
            clock?.restore();
            await close(statusServer.server);
        }
    });

    test('retains the exact func task end listener after a successful debug launch', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const debugConfiguration = createDebugConfiguration(projectPath);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(true),
            debugConfiguration);

        sinon.assert.notCalled(taskHarness.getEndListenerDispose());

        cleanupRun('azure-functions-test-run');
        sinon.assert.notCalled(taskHarness.getEndListenerDispose());
        taskHarness.end(0);
        await new Promise<void>(resolve => setImmediate(resolve));

        sinon.assert.calledOnce(taskHarness.getExecution()!.terminate as sinon.SinonStub);
        sinon.assert.calledOnce(taskHarness.getEndListenerDispose());
        sinon.assert.notCalled(process.kill as sinon.SinonStub);
    });

    test('quotes HTTPS arguments for a configured cmd task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const debugConfiguration = createDebugConfiguration(projectPath, ['--port', String(statusServer.port), '--password', ')456Y7R.D*S3Fwdr7mAv-p']);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\cmd.exe', args: ['/d', '/v:on', '/c'] });

        try {
            await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                [],
                createLaunchOptions(false),
                debugConfiguration);

            assert.strictEqual(
                (taskHarness.getExecutedTask()?.execution as vscode.ShellExecution).commandLine,
                `func host start --port ${statusServer.port} --password ")456Y7R.D*S3Fwdr7mAv-p" --enable-json-output --json-output-file ${defaultWorkerPidArgument}`);
        } finally {
            await close(statusServer.server);
        }
    });

    test('uses a relative forward-slash worker metadata path for an Aspire-owned cmd task argument', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'Functions%!App', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const buildOutputPath = path.dirname(targetPath);
        const ownedMetadataDirectory = path.join(buildOutputPath, 'aspire-functions-worker-test');
        const ownedMetadataFile = path.join(ownedMetadataDirectory, 'worker-startup.json');
        const relativeMetadataFile = 'aspire-functions-worker-test/worker-startup.json';
        const debugConfiguration = createDebugConfiguration(projectPath);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        fsMkdtempSync.returns(ownedMetadataDirectory);
        fsReadFileSync.withArgs(ownedMetadataFile, 'utf8').returns(
            `${JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: defaultWorkerPid })}\n`);
        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\cmd.exe' });

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(true),
            debugConfiguration);

        const executedTask = taskHarness.getExecutedTask();
        assert.ok(executedTask);
        assert.deepStrictEqual(executedTask.definition.args, [
            '--dotnet-isolated-debug',
            '--enable-json-output',
            '--json-output-file',
            relativeMetadataFile
        ]);
        assert.strictEqual(
            (executedTask.execution as vscode.ShellExecution).commandLine,
            `func host start --dotnet-isolated-debug --enable-json-output --json-output-file ${relativeMetadataFile}`);
        assert.strictEqual(path.resolve(buildOutputPath, relativeMetadataFile), path.resolve(ownedMetadataFile));
        sinon.assert.calledOnceWithExactly(fsMkdtempSync, path.join(buildOutputPath, 'aspire-functions-worker-'));

        cleanupRun('azure-functions-test-run');
        taskHarness.end(0);
        await new Promise<void>(resolve => setImmediate(resolve));
        sinon.assert.calledOnceWithExactly(fsRmSync, ownedMetadataDirectory, { recursive: true, force: true });
    });

    for (const shell of ['bash', 'dash', 'ash']) {
        test(`quotes backslashes and apostrophes for a configured ${shell} task shell`, async () => {
            const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
            const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
            const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
            const debugConfiguration = createDebugConfiguration(projectPath, ['--port', String(statusServer.port), '--password', "a\\b'c"]);

            sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
            sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
            stubTaskShell('linux', { path: `/bin/${shell}` });

            try {
                await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                    createLaunchConfiguration(projectPath),
                    debugConfiguration.args as string[],
                    [],
                    createLaunchOptions(false),
                    debugConfiguration);

                assert.strictEqual(
                    (taskHarness.getExecutedTask()?.execution as vscode.ShellExecution).commandLine,
                    `func host start --port ${statusServer.port} --password 'a\\b'"'"'c' --enable-json-output --json-output-file ${defaultWorkerPidArgument}`);
            } finally {
                await close(statusServer.server);
            }
        });
    }

    test('quotes a backslash before an apostrophe for a configured fish task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const password = String.raw`prefix\'; touch /tmp/owned`;
        const debugConfiguration = createDebugConfiguration(projectPath, ['--port', String(statusServer.port), '--password', password]);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('linux', { path: '/usr/bin/fish' });

        try {
            await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                [],
                createLaunchOptions(false),
                debugConfiguration);

            assert.strictEqual(
                (taskHarness.getExecutedTask()?.execution as vscode.ShellExecution).commandLine,
                `func host start --port ${statusServer.port} --password ${String.raw`'prefix\\\'; touch /tmp/owned'`} --enable-json-output --json-output-file ${defaultWorkerPidArgument}`);
        } finally {
            await close(statusServer.server);
        }
    });

    test('rejects percent expansion for a configured cmd task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const debugConfiguration = createDebugConfiguration(projectPath, ['--password', '%TEMP%']);

        const getDotNetTargetPath = sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        const buildDotNetProject = sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\cmd.exe' });

        await assert.rejects(
            azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                [],
                createLaunchOptions(false),
                debugConfiguration),
            (error: Error) => error.message === azureFunctionsCmdPercentArgument);
        sinon.assert.notCalled(buildDotNetProject);
        sinon.assert.notCalled(getDotNetTargetPath);
        sinon.assert.notCalled(vscode.tasks.executeTask as sinon.SinonStub);
    });

    test('rejects exclamation mark arguments for a configured cmd task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const debugConfiguration = createDebugConfiguration(projectPath, ['--password', '!ASPIRE_PASSWORD!']);

        const getDotNetTargetPath = sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        const buildDotNetProject = sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\Windows\\System32\\cmd.exe', args: ['/d', '/v:off', '/c'] });

        await assert.rejects(
            azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                [],
                createLaunchOptions(false),
                debugConfiguration),
            (error: Error) => error.message === azureFunctionsCmdDelayedExpansion);
        sinon.assert.notCalled(buildDotNetProject);
        sinon.assert.notCalled(getDotNetTargetPath);
        sinon.assert.notCalled(vscode.tasks.executeTask as sinon.SinonStub);
    });

    test('rejects unsafe arguments for an unsupported task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const debugConfiguration = createDebugConfiguration(projectPath, ['--password', ')unsafe']);

        const getDotNetTargetPath = sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        const buildDotNetProject = sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\tools\\nu.exe' });

        await assert.rejects(
            azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                [],
                createLaunchOptions(false),
                debugConfiguration),
            (error: Error) => error.message === azureFunctionsUnsupportedTaskShell);
        sinon.assert.notCalled(buildDotNetProject);
        sinon.assert.notCalled(getDotNetTargetPath);
        sinon.assert.notCalled(vscode.tasks.executeTask as sinon.SinonStub);
    });

    test('passes empty arguments through for an unsupported task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const debugConfiguration = createDebugConfiguration(projectPath);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\tools\\nu.exe' });

        try {
            await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                ['--port', String(statusServer.port)],
                [],
                createLaunchOptions(false),
                debugConfiguration);

            assert.strictEqual(
                (taskHarness.getExecutedTask()?.execution as vscode.ShellExecution).commandLine,
                `func host start --port ${statusServer.port} --enable-json-output --json-output-file ${defaultWorkerPidArgument}`);
        } finally {
            await close(statusServer.server);
        }
    });

    test('passes universally safe arguments through for an unsupported task shell', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const args = ['--port', String(statusServer.port), '--verbose', '/workspace/cert.pfx'];
        const debugConfiguration = createDebugConfiguration(projectPath, args);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        stubTaskShell('win32', { path: 'C:\\tools\\nu.exe' });

        try {
            await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                debugConfiguration.args as string[],
                [],
                createLaunchOptions(false),
                debugConfiguration);

            assert.strictEqual(
                (taskHarness.getExecutedTask()?.execution as vscode.ShellExecution).commandLine,
                `func host start ${args.join(' ')} --enable-json-output --json-output-file ${defaultWorkerPidArgument}`);
        } finally {
            await close(statusServer.server);
        }
    });

    test('returns the already-started func host without launching CoreCLR in run mode', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const certificatePath = path.join('/workspace', 'FunctionsApp', 'aspire-functions-https.pfx');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const aspireDebugSession = createAspireDebugSession();
        const startDebugging = sinon.stub(vscode.debug, 'startDebugging').resolves(false);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        try {
            const preparedSession = await prepareDebugSession(
                { type: 'aspire', request: 'launch', name: 'Aspire', program: '' },
                createLaunchConfiguration(projectPath),
                ['--port', String(statusServer.port), '--cert', certificatePath, '--password', 'secret-password'],
                createEnvironmentVariables(),
                createLaunchOptions(false, aspireDebugSession),
                azureFunctionsDebuggerExtension);

            assert.ok(preparedSession.alreadyStartedSession);
            assert.strictEqual(preparedSession.alreadyStartedSession.processId, defaultWorkerPid);
            const resourceDebugSession = aspireDebugSession.trackAlreadyStartedResourceSession(
                preparedSession.debugConfiguration,
                preparedSession.alreadyStartedSession);

            assert.strictEqual(resourceDebugSession?.id, 'azure-functions-test-run');
            assert.strictEqual(startDebugging.called, false);
            assert.strictEqual(preparedSession.debugConfiguration.processId, undefined);

            aspireDebugSession.dispose();
            taskHarness.end(0);
            await new Promise<void>(resolve => setImmediate(resolve));
        } finally {
            await close(statusServer.server);
        }
    });

    test('reports func host task exit in run mode', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const buildOutputPath = path.dirname(targetPath);
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const unrelatedExecution = createFuncTaskExecution(buildOutputPath, 'workspace', 'echo func');

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        try {
            const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                ['--port', String(statusServer.port)],
                [],
                createLaunchOptions(false),
                createDebugConfiguration(projectPath));

            assert.ok(resourceDebugSession);
            taskHarness.endExecution(unrelatedExecution, 99);
            taskHarness.end(17);

            assert.strictEqual(await resourceDebugSession.termination, 17);
            sinon.assert.notCalled(taskHarness.getExecution()!.terminate as sinon.SinonStub);
            sinon.assert.notCalled(process.kill as sinon.SinonStub);
            sinon.assert.calledOnceWithExactly(fsRmSync, defaultTempPath, { recursive: true, force: true });
            sinon.assert.notCalled(unrelatedExecution.terminate as sinon.SinonStub);
        } finally {
            await close(statusServer.server);
        }
    });

    test('surfaces exact func task exit before host readiness', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        funcHostStatus = 'Starting';
        taskHarness.endOnStart(23);

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        await assert.rejects(
            azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                [],
                [],
                createLaunchOptions(false),
                createDebugConfiguration(projectPath)),
            /Azure Functions task exited with code 23 before startup completed/);
        sinon.assert.calledOnce(taskHarness.getEndListenerDispose());
        sinon.assert.notCalled(taskHarness.getExecution()!.terminate as sinon.SinonStub);
        sinon.assert.notCalled(process.kill as sinon.SinonStub);
    });

    for (const { platform, expectedExitCode } of [
        { platform: 'darwin', expectedExitCode: 0 },
        { platform: 'linux', expectedExitCode: 0 },
        { platform: 'win32', expectedExitCode: 143 },
    ] as const) {
        test(`normalizes func host SIGTERM task exit on ${platform}`, async () => {
            const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
            const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
            const statusServer = await createFuncHostStatusServer(() => funcHostStatus);

            sinon.stub(process, 'platform').value(platform);
            sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
            sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

            try {
                const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                    createLaunchConfiguration(projectPath),
                    ['--port', String(statusServer.port)],
                    [],
                    createLaunchOptions(false),
                    createDebugConfiguration(projectPath));

                assert.ok(resourceDebugSession);
                taskHarness.end(143);

                assert.strictEqual(await resourceDebugSession.termination, expectedExitCode);
            } finally {
                await close(statusServer.server);
            }
        });
    }

    test('waits for loopback host readiness and returns the worker process ID in run mode', async () => {
        const requests: string[] = [];
        const server = http.createServer((request, response) => {
            requests.push(request.url ?? '');
            response.setHeader('content-type', 'application/json');
            response.end(JSON.stringify({ state: 'Running' }));
        });
        const port = await listen(server);
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        try {
            const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                ['--port', String(port)],
                [],
                createLaunchOptions(false),
                createDebugConfiguration(projectPath, ['--port', String(port)]));

            assert.ok(resourceDebugSession);
            assert.strictEqual(resourceDebugSession.processId, defaultWorkerPid);
            assert.deepStrictEqual(requests, ['/admin/host/status']);
        } finally {
            await close(server);
        }
    });

    test('parses supported func host port argument forms and defaults to 7071', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        for (const form of [
            (port: number) => ['--port', String(port)],
            (port: number) => [`--port=${port}`],
            (port: number) => ['-p', String(port)],
            (port: number) => [`-p=${port}`],
        ]) {
            const statusServer = await createFuncHostStatusServer(() => 'Running');
            const args = form(statusServer.port);
            try {
                const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                    createLaunchConfiguration(projectPath),
                    args,
                    [],
                    createLaunchOptions(false),
                    createDebugConfiguration(projectPath, args));
                const stop = resourceDebugSession?.stopSession();
                taskHarness.end(0);
                await stop;
                assert.deepStrictEqual(statusServer.requests, ['/admin/host/status']);
            } finally {
                await close(statusServer.server);
            }
        }

        const requestedPorts: number[] = [];
        stubFuncHostStatusRequests(() => 'Running', port => requestedPorts.push(port));
        const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(false),
            createDebugConfiguration(projectPath));
        const stop = resourceDebugSession?.stopSession();
        taskHarness.end(0);
        await stop;
        assert.deepStrictEqual(requestedPorts, [7071]);
    });

    test('configures coreclr attach from the latest worker startup JSON and cleans the debug temp directory', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const buildOutputPath = path.dirname(targetPath);
        const debugTempPath = path.join(buildOutputPath, 'aspire-functions-worker-abc123');
        const workerPidPath = path.join(debugTempPath, 'worker-startup.json');
        const workerPidArgument = path.relative(buildOutputPath, workerPidPath).split(path.sep).join('/');
        const getDotNetTargetPath = sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        const buildDotNetProject = sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        fsMkdtempSync.returns(debugTempPath);
        fsReadFileSync.withArgs(workerPidPath, 'utf8').returns([
            'null',
            JSON.stringify({ name: 'unrelated-event', workerProcessId: 1111 }),
            JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: 4242 }),
            JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: 4343 }),
            '{"name":"dotnet-worker-startup","workerProcessId":',
            ''
        ].join('\n'));
        const debugConfiguration = createDebugConfiguration(projectPath, ['--verbose']);

        const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            debugConfiguration.args as string[],
            createEnvironmentVariables(),
            createLaunchOptions(true),
            debugConfiguration);
        assert.ok(getDotNetTargetPath.calledOnceWith(projectPath));
        assert.ok(buildDotNetProject.calledOnceWith(projectPath));
        assert.ok(buildDotNetProject.calledOnceWith(projectPath));
        sinon.assert.calledOnceWithExactly(fsMkdtempSync, path.join(buildOutputPath, 'aspire-functions-worker-'));
        assert.deepStrictEqual(taskHarness.getExecutedTask()?.definition, {
            type: 'func',
            command: 'host start',
            args: ['--verbose', '--dotnet-isolated-debug', '--enable-json-output', '--json-output-file', workerPidArgument]
        });
        assert.strictEqual(debugConfiguration.type, 'coreclr');
        assert.strictEqual(debugConfiguration.request, 'attach');
        assert.strictEqual(debugConfiguration.processId, '4343');
        assert.strictEqual(debugConfiguration.program, undefined);
        assert.strictEqual(debugConfiguration.args, undefined);
        assert.strictEqual(debugConfiguration.cwd, undefined);
        assert.strictEqual(debugConfiguration.console, undefined);
        assert.strictEqual(debugConfiguration.env, undefined);
        assert.strictEqual(resourceDebugSession, undefined);

        cleanupRun('azure-functions-test-run');
        sinon.assert.calledOnceWithExactly(fsRmSync, debugTempPath, { recursive: true, force: true });
        sinon.assert.notCalled(process.kill as sinon.SinonStub);
        sinon.assert.calledOnce(taskHarness.getExecution()!.terminate as sinon.SinonStub);
        taskHarness.end(0);
        await new Promise<void>(resolve => setImmediate(resolve));
    });

    for (const processId of ['worker-1', 0, -1, 1.5, 0x80000000] as const) {
        test(`rejects invalid Azure Functions worker process ID '${processId}'`, async () => {
            const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
            const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
            const buildOutputPath = path.dirname(targetPath);
            const debugTempPath = path.join(buildOutputPath, 'aspire-functions-worker-invalid');
            const workerPidPath = path.join(debugTempPath, 'worker-startup.json');
            sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
            sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
            fsMkdtempSync.returns(debugTempPath);
            fsReadFileSync
                .withArgs(workerPidPath, 'utf8')
                .returns(`${JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: processId })}\n`);

            await assert.rejects(
                azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                    createLaunchConfiguration(projectPath),
                    [],
                    [],
                    createLaunchOptions(true),
                    createDebugConfiguration(projectPath)),
                (error: Error) => error.message === azureFunctionsInvalidProcessId(String(processId)));
        });
    }

    test('continues run cleanup and retries when debug temp directory removal fails', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const buildOutputPath = path.dirname(targetPath);
        const debugTempPath = path.join(buildOutputPath, 'aspire-functions-worker-retry');
        const workerPidPath = path.join(debugTempPath, 'worker-startup.json');
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        fsMkdtempSync.returns(debugTempPath);
        fsReadFileSync
            .withArgs(workerPidPath, 'utf8')
            .returns(`${JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: 4646 })}\n`);
        let resolveRemovalRetried: (() => void) | undefined;
        const removalRetried = new Promise<void>(resolve => resolveRemovalRetried = resolve);
        fsRmSync.onFirstCall().throws(Object.assign(new Error('directory is busy'), { code: 'EBUSY' }));
        fsRmSync.onSecondCall().callsFake(() => resolveRemovalRetried?.());
        const laterCleanup = sinon.stub();
        const debugConfiguration = createDebugConfiguration(projectPath);

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(true),
            debugConfiguration);
        registerRunCleanup('azure-functions-test-run', laterCleanup);

        assert.doesNotThrow(() => cleanupRun('azure-functions-test-run'));
        sinon.assert.calledOnce(laterCleanup);
        sinon.assert.calledOnce(fsRmSync);
        taskHarness.end(0);

        await removalRetried;
        sinon.assert.calledTwice(fsRmSync);
    });

    test('starts one temp directory removal retry chain when cleanup races task exit', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        fsRmSync.throws(Object.assign(new Error('directory is busy'), { code: 'EBUSY' }));

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(true),
            createDebugConfiguration(projectPath));

        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        cleanupRun('azure-functions-test-run');
        taskHarness.end(0);

        sinon.assert.calledOnce(fsRmSync);
        await clock.tickAsync(100);
        sinon.assert.calledTwice(fsRmSync);
    });

    test('bounds debug temp directory retries and makes a final post-shutdown attempt', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const buildOutputPath = path.dirname(targetPath);
        const debugTempPath = path.join(buildOutputPath, 'aspire-functions-worker-busy');
        const workerPidPath = path.join(debugTempPath, 'worker-startup.json');
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        fsMkdtempSync.returns(debugTempPath);
        fsReadFileSync
            .withArgs(workerPidPath, 'utf8')
            .returns(`${JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: 4646 })}\n`);
        fsRmSync.throws(Object.assign(new Error('directory is busy'), { code: 'EBUSY' }));
        const debugConfiguration = createDebugConfiguration(projectPath);

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(true),
            debugConfiguration);

        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        cleanupRun('azure-functions-test-run');
        sinon.assert.calledOnce(fsRmSync);

        await clock.tickAsync(30_000);
        assert.strictEqual(fsRmSync.callCount, 301);
    });

    test('makes a final debug temp directory cleanup attempt after the task exits', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        let directoryLocked = true;
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        fsRmSync.callsFake(() => {
            if (directoryLocked) {
                throw Object.assign(new Error('directory is busy'), { code: 'EBUSY' });
            }
        });

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(true),
            createDebugConfiguration(projectPath));

        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        cleanupRun('azure-functions-test-run');
        await clock.tickAsync(29_900);
        assert.strictEqual(fsRmSync.callCount, 300);

        directoryLocked = false;
        taskHarness.end(0);
        await clock.tickAsync(0);

        assert.strictEqual(fsRmSync.callCount, 301);
    });

    test('bounds startup when VS Code task execution does not resolve', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('azureFunctions').returns({
            get: sinon.stub().withArgs('pickProcessTimeout').returns(0.01)
        } as unknown as vscode.WorkspaceConfiguration);
        (vscode.tasks.executeTask as sinon.SinonStub).resetBehavior();
        (vscode.tasks.executeTask as sinon.SinonStub).returns(new Promise<vscode.TaskExecution>(() => { }));
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });

        const launch = azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            [],
            [],
            createLaunchOptions(true),
            createDebugConfiguration(projectPath));
        await clock.tickAsync(10);

        await assert.rejects(
            launch,
            /Timed out after 0.01 seconds waiting for the Azure Functions worker process to start/);
    });

    test('settles run-mode stop when VS Code task termination throws', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        try {
            const resourceDebugSession = await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                ['--port', String(statusServer.port)],
                [],
                createLaunchOptions(false),
                createDebugConfiguration(projectPath));
            assert.ok(resourceDebugSession);
            (taskHarness.getExecution()!.terminate as sinon.SinonStub).throws(new Error('task termination failed'));

            await resourceDebugSession.stopSession();

            assert.strictEqual(await resourceDebugSession.termination, -1);
        } finally {
            await close(statusServer.server);
        }
    });

    test('does not duplicate existing Core Tools debug flags', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const buildOutputPath = path.dirname(targetPath);
        const existingWorkerPidArgument = 'existing-worker.json';
        const existingWorkerPidPath = path.resolve(buildOutputPath, existingWorkerPidArgument);
        const staleOutput = `${JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: 4545 })}\n`;
        const freshOutput = [
            JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: 4646 }),
            JSON.stringify({ name: 'dotnet-worker-startup', workerProcessId: 4747 }),
            ''
        ].join('\n');
        const args = ['--dotnet-isolated-debug', '--enable-json-output', '--json-output-file', existingWorkerPidArgument];
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        fsReadFileSync.withArgs(existingWorkerPidPath, 'utf8')
            .onFirstCall().returns(staleOutput)
            .onSecondCall().returns(staleOutput + freshOutput);
        fsReadFileSync.withArgs(existingWorkerPidArgument, 'utf8').returns(staleOutput + freshOutput);
        const debugConfiguration = createDebugConfiguration(projectPath, args);

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            args,
            [],
            createLaunchOptions(true),
            debugConfiguration);

        assert.deepStrictEqual(taskHarness.getExecutedTask()?.definition, {
            type: 'func',
            command: 'host start',
            args
        });
        assert.strictEqual(debugConfiguration.processId, '4747');
        sinon.assert.alwaysCalledWithExactly(fsReadFileSync, existingWorkerPidPath, 'utf8');
        sinon.assert.notCalled(fsMkdtempSync);

        cleanupRun('azure-functions-test-run');
        taskHarness.end(0);
        await new Promise<void>(resolve => setImmediate(resolve));
    });

    test('enables required Core Tools flags when explicitly disabled', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const args = ['--dotnet-isolated-debug=false', '--enable-json-output', 'false'];
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();

        await azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            args,
            [],
            createLaunchOptions(true),
            createDebugConfiguration(projectPath, args));

        assert.deepStrictEqual(taskHarness.getExecutedTask()?.definition.args, [
            '--dotnet-isolated-debug',
            '--enable-json-output',
            '--json-output-file',
            defaultWorkerPidArgument
        ]);

        cleanupRun('azure-functions-test-run');
        taskHarness.end(0);
        await new Promise<void>(resolve => setImmediate(resolve));
    });

    test('stops readiness and worker polling after the exact task exits during startup', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const requestedPorts: number[] = [];
        taskHarness.endOnStart(29);
        fsReadFileSync.throws(Object.assign(new Error('worker metadata does not exist'), { code: 'ENOENT' }));
        stubFuncHostStatusRequests(() => 'Starting', port => requestedPorts.push(port));
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('azureFunctions').returns({
            get: sinon.stub().withArgs('pickProcessTimeout').returns(0.3)
        } as unknown as vscode.WorkspaceConfiguration);

        await assert.rejects(
            azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                createLaunchConfiguration(projectPath),
                ['--port', '7137'],
                [],
                createLaunchOptions(false),
                createDebugConfiguration(projectPath)),
            /Azure Functions task exited with code 29 before startup completed/);

        const workerReadCount = fsReadFileSync.callCount;
        const readinessRequestCount = requestedPorts.length;
        await new Promise(resolve => setTimeout(resolve, 150));

        assert.strictEqual(fsReadFileSync.callCount, workerReadCount);
        assert.strictEqual(requestedPorts.length, readinessRequestCount);
    });

    test('aborts readiness and worker polling when startup cleanup is requested', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        let resolveWorkerPollingStarted: (() => void) | undefined;
        const workerPollingStarted = new Promise<void>(resolve => resolveWorkerPollingStarted = resolve);
        fsReadFileSync.callsFake(() => {
            resolveWorkerPollingStarted?.();
            throw Object.assign(new Error('worker metadata does not exist'), { code: 'ENOENT' });
        });
        stubFuncHostStatusRequests(() => 'Starting', () => { });
        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('azureFunctions').returns({
            get: sinon.stub().withArgs('pickProcessTimeout').returns(0.05)
        } as unknown as vscode.WorkspaceConfiguration);

        const launch = azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
            createLaunchConfiguration(projectPath),
            ['--port', '7137'],
            [],
            createLaunchOptions(false),
            createDebugConfiguration(projectPath));
        await workerPollingStarted;

        cleanupRun('azure-functions-test-run');
        const outcome = await Promise.race([
            launch.then(() => 'resolved', () => 'rejected'),
            new Promise<'pending'>(resolve => setTimeout(() => resolve('pending'), 25))
        ]);
        if (outcome === 'pending') {
            await assert.rejects(launch);
        }

        assert.strictEqual(outcome, 'rejected');
    });

    test('surfaces a bounded host readiness timeout from Azure Functions configuration', async () => {
        const projectPath = path.join('/workspace', 'FunctionsApp', 'FunctionsApp.csproj');
        const targetPath = path.join('/workspace', 'FunctionsApp', 'bin', 'Debug', 'net10.0', 'FunctionsApp.dll');
        const statusServer = await createFuncHostStatusServer(() => funcHostStatus);
        const debugConfiguration = createDebugConfiguration(projectPath, ['--port', String(statusServer.port)]);
        funcHostStatus = 'Starting';

        sinon.stub(DotNetService.prototype, 'getDotNetTargetPath').resolves(targetPath);
        sinon.stub(DotNetService.prototype, 'buildDotNetProject').resolves();
        sinon.stub(vscode.workspace, 'getConfiguration').withArgs('azureFunctions').returns({
            get: sinon.stub().withArgs('pickProcessTimeout').returns(0.01)
        } as unknown as vscode.WorkspaceConfiguration);

        try {
            await assert.rejects(
                azureFunctionsDebuggerExtension.createDebugSessionConfigurationCallback!(
                    createLaunchConfiguration(projectPath),
                    debugConfiguration.args as string[],
                    [],
                    createLaunchOptions(false),
                    debugConfiguration),
                /Timed out after 0.01 seconds waiting for the Azure Functions host to start/);
        } finally {
            await close(statusServer.server);
        }
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
        { type: 'func', command: 'host start' },
        vscode.TaskScope.Workspace,
        'func: host start',
        source,
        new vscode.ShellExecution(commandLine, { cwd: buildOutputPath }));
    return {
        task,
        terminate: sinon.stub(),
    } as unknown as vscode.TaskExecution;
}

function stubRegisteredFuncTaskExecution(processId = 4242): {
    getExecutedTask(): vscode.Task | undefined;
    getExecution(): vscode.TaskExecution | undefined;
    getEndListenerDispose(): sinon.SinonStub;
    endExecution(execution: vscode.TaskExecution, exitCode: number | undefined): void;
    endOnStart(exitCode: number | undefined): void;
    end(exitCode: number | undefined): void;
} {
    let startTaskProcess: ((event: vscode.TaskProcessStartEvent) => unknown) | undefined;
    let endTaskProcess: ((event: vscode.TaskProcessEndEvent) => unknown) | undefined;
    let exitCodeOnStart: number | undefined;
    let executedTask: vscode.Task | undefined;
    let execution: vscode.TaskExecution | undefined;
    const endListenerDispose = sinon.stub();

    sinon.stub(vscode.tasks, 'onDidStartTaskProcess').callsFake(listener => {
        startTaskProcess = listener;
        return new vscode.Disposable(() => { });
    });
    sinon.stub(vscode.tasks, 'onDidEndTaskProcess').callsFake(listener => {
        endTaskProcess = listener;
        return new vscode.Disposable(endListenerDispose);
    });
    sinon.stub(vscode.tasks, 'executeTask').callsFake(async task => {
        executedTask = task;
        execution = {
            task,
            terminate: sinon.stub(),
        } as unknown as vscode.TaskExecution;
        const currentExecution = execution;

        // VS Code may report the process before executeTask's promise continuation runs.
        // Keep that ordering here so the production code cannot rely on the returned
        // TaskExecution being assigned before its process event.
        queueMicrotask(() => {
            startTaskProcess?.({ execution: currentExecution, processId });
            if (exitCodeOnStart !== undefined) {
                endTaskProcess?.({ execution: currentExecution, exitCode: exitCodeOnStart });
            }
        });
        return currentExecution;
    });

    return {
        getExecutedTask: () => executedTask,
        getExecution: () => execution,
        getEndListenerDispose: () => endListenerDispose,
        endExecution: (taskExecution, exitCode) => endTaskProcess?.({ execution: taskExecution, exitCode }),
        endOnStart: exitCode => {
            exitCodeOnStart = exitCode;
        },
        end: exitCode => {
            if (!execution) {
                throw new Error('No func task execution was recorded');
            }

            endTaskProcess?.({ execution, exitCode });
        },
    };
}

function stubTaskShell(platform: NodeJS.Platform, profile: { path: string; args?: string[] }): void {
    sinon.stub(process, 'platform').value(platform);
    const settingsPlatform = platform === 'win32' ? 'windows' : platform === 'darwin' ? 'osx' : 'linux';
    const getConfiguration = sinon.stub(vscode.workspace, 'getConfiguration');
    getConfiguration.withArgs('terminal.integrated').returns({
        get: <T>(section: string): T | undefined =>
            section === `automationProfile.${settingsPlatform}` ? profile as T : undefined,
    } as unknown as vscode.WorkspaceConfiguration);
    getConfiguration.withArgs('azureFunctions').returns({
        get: () => undefined,
    } as unknown as vscode.WorkspaceConfiguration);
}

function installAzureFunctionsExtensionStub(): { activate: sinon.SinonStub; getApi: sinon.SinonStub } {
    const activate = sinon.stub().resolves(undefined);
    const getApi = sinon.stub().throws(new Error('The broken startFuncProcess API must not be used'));
    sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
        return extensionId === 'ms-azuretools.vscode-azurefunctions'
            ? {
                id: extensionId,
                isActive: false,
                exports: { getApi },
                activate
            } as unknown as vscode.Extension<unknown>
            : undefined;
    });

    return { activate, getApi };
}

function stubFuncHostStatusRequests(getStatus: () => string, onRequest: (port: number) => void): void {
    const stubRequest = (protocol: typeof http | typeof https): void => {
        sinon.stub(protocol, 'request').callsFake(((options: http.RequestOptions, callback: (response: http.IncomingMessage) => void) => {
            const request = new EventEmitter() as http.ClientRequest;
            request.setTimeout = sinon.stub().returns(request);
            request.destroy = sinon.stub();
            request.end = () => {
                onRequest(Number(options.port));
                const response = new EventEmitter() as http.IncomingMessage;
                response.statusCode = 200;
                response.setEncoding = sinon.stub().returns(response);
                response.resume = sinon.stub().returns(response);
                queueMicrotask(() => {
                    callback(response);
                    response.emit('data', JSON.stringify({ state: getStatus() }));
                    response.emit('end');
                });
                return request;
            };
            return request;
        }) as typeof http.request);
    };

    stubRequest(http);
    stubRequest(https);
}

async function listen(server: http.Server): Promise<number> {
    await new Promise<void>((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => {
            server.off('error', reject);
            resolve();
        });
    });

    const address = server.address();
    if (!address || typeof address === 'string') {
        throw new Error('Expected the loopback status server to have a TCP address');
    }

    return address.port;
}

async function createFuncHostStatusServer(getStatus: () => string): Promise<{
    port: number;
    requests: string[];
    server: http.Server;
}> {
    const requests: string[] = [];
    const server = http.createServer((request, response) => {
        requests.push(request.url ?? '');
        response.setHeader('content-type', 'application/json');
        response.end(JSON.stringify({ state: getStatus() }));
    });

    return {
        port: await listen(server),
        requests,
        server
    };
}

async function close(server: http.Server): Promise<void> {
    await new Promise<void>((resolve, reject) => {
        server.close(error => error ? reject(error) : resolve());
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
