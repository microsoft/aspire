/// <reference types="mocha" />

import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import {
    AddIntegrationTestProjectAvailability,
    addIntegrationTestProject,
    addIntegrationTestProjectSupportedContext,
} from '../commands/addIntegrationTestProject';
import {
    addIntegrationTestProjectCapabilityCouldNotBeVerified,
    addIntegrationTestProjectRequiresCSharpAppHost,
    addIntegrationTestProjectUnsupported,
} from '../loc/strings';
import { aspireTestAppHostCapability } from '../types/configInfo';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import {
    windowCliPathTarget,
    workspaceFolderCliPathTarget,
} from '../utils/cliPathVariables';

suite('addIntegrationTestProject', () => {
    let sandbox: sinon.SinonSandbox;
    let terminalProvider: AspireTerminalProvider;
    let configInfoProvider: ConfigInfoProvider;
    let sendCommandStub: sinon.SinonStub;
    let getCapabilityStatusStub: sinon.SinonStub;
    let showErrorMessageStub: sinon.SinonStub;
    let executeCommandStub: sinon.SinonStub;

    setup(() => {
        sandbox = sinon.createSandbox();
        sendCommandStub = sandbox.stub().resolves();
        terminalProvider = {
            sendAspireCommandToAspireTerminal: sendCommandStub,
        } as unknown as AspireTerminalProvider;
        getCapabilityStatusStub = sandbox.stub().resolves('supported');
        configInfoProvider = {
            getCapabilityStatus: getCapabilityStatusStub,
        } as unknown as ConfigInfoProvider;
        showErrorMessageStub = sandbox.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);
        sandbox.stub(vscode.window, 'activeTextEditor').value(undefined);
        sandbox.stub(vscode.workspace, 'workspaceFolders').value(undefined);
    });

    teardown(() => {
        sandbox.restore();
    });

    test('invokes the selected CLI for the selected C# AppHost', async () => {
        const workspaceFolder = createWorkspaceFolder('server', path.join(path.parse(process.cwd()).root, 'repo', 'server'));
        const target = workspaceFolderCliPathTarget(workspaceFolder);
        const appHostPath = path.join(workspaceFolder.uri.fsPath, 'AppHost', 'AppHost.csproj');

        await addIntegrationTestProject(
            terminalProvider,
            configInfoProvider,
            appHostPath,
            target,
            '/selected/aspire');

        assert.ok(getCapabilityStatusStub.calledOnceWith(aspireTestAppHostCapability, {
            cliPath: '/selected/aspire',
            target,
            forceRefresh: true,
            suppressErrors: true,
        }));
        assert.ok(sendCommandStub.calledOnceWith(
            ['new', 'aspire-test'],
            true,
            ['--apphost', appHostPath],
            {
                cliPath: '/selected/aspire',
                target,
            }));
    });

    test('does not invoke a CLI that does not advertise support', async () => {
        getCapabilityStatusStub.resolves('unsupported');

        await assert.rejects(
            () => addIntegrationTestProject(
                terminalProvider,
                configInfoProvider,
                path.join('repo', 'AppHost.csproj'),
                windowCliPathTarget,
                '/selected/aspire'),
            error => error instanceof vscode.CancellationError);

        assert.ok(showErrorMessageStub.calledOnceWith(addIntegrationTestProjectUnsupported));
        assert.strictEqual(sendCommandStub.called, false);
    });

    test('reports when selected CLI support cannot be verified', async () => {
        getCapabilityStatusStub.resolves('unavailable');

        await assert.rejects(
            () => addIntegrationTestProject(
                terminalProvider,
                configInfoProvider,
                path.join('repo', 'AppHost.csproj'),
                windowCliPathTarget,
                '/selected/aspire'),
            error => error instanceof vscode.CancellationError);

        assert.ok(showErrorMessageStub.calledOnceWith(addIntegrationTestProjectCapabilityCouldNotBeVerified));
        assert.strictEqual(sendCommandStub.called, false);
    });

    test('rejects a CSharp single-file AppHost before checking the capability', async () => {
        await assert.rejects(
            () => addIntegrationTestProject(
                terminalProvider,
                configInfoProvider,
                path.join('repo', 'apphost.cs'),
                windowCliPathTarget,
                '/selected/aspire'),
            error => error instanceof vscode.CancellationError);

        assert.ok(showErrorMessageStub.calledOnceWith(addIntegrationTestProjectRequiresCSharpAppHost));
        assert.strictEqual(getCapabilityStatusStub.called, false);
        assert.strictEqual(sendCommandStub.called, false);
    });

    test('publishes command availability from the active CLI capability', async () => {
        const availability = new AddIntegrationTestProjectAvailability(configInfoProvider);

        try {
            await availability.refresh();

            assert.strictEqual(getCapabilityStatusStub.callCount, 1);
            assert.strictEqual(getCapabilityStatusStub.firstCall.args[0], aspireTestAppHostCapability);
            assert.strictEqual(getCapabilityStatusStub.firstCall.args[1].target, windowCliPathTarget);
            assert.strictEqual(getCapabilityStatusStub.firstCall.args[1].forceRefresh, false);
            assert.strictEqual(getCapabilityStatusStub.firstCall.args[1].suppressErrors, true);
            assert.ok(getCapabilityStatusStub.firstCall.args[1].cancellationToken);
            assert.ok(executeCommandStub.firstCall.calledWith(
                'setContext',
                addIntegrationTestProjectSupportedContext,
                false));
            assert.ok(executeCommandStub.lastCall.calledWith(
                'setContext',
                addIntegrationTestProjectSupportedContext,
                true));
        }
        finally {
            availability.dispose();
        }
    });

    test('retries when command availability cannot initially be verified', async () => {
        const clock = sandbox.useFakeTimers({ shouldClearNativeTimers: true });
        getCapabilityStatusStub.onFirstCall().resolves('unavailable');
        getCapabilityStatusStub.onSecondCall().resolves('supported');
        const availability = new AddIntegrationTestProjectAvailability(configInfoProvider);

        try {
            await availability.refresh();

            assert.strictEqual(getCapabilityStatusStub.callCount, 1);
            assert.strictEqual(executeCommandStub.neverCalledWith(
                'setContext',
                addIntegrationTestProjectSupportedContext,
                true), true);

            await clock.tickAsync(5_000);

            assert.strictEqual(getCapabilityStatusStub.callCount, 2);
            assert.strictEqual(getCapabilityStatusStub.firstCall.args[1].forceRefresh, false);
            assert.strictEqual(getCapabilityStatusStub.secondCall.args[1].forceRefresh, true);
            assert.ok(executeCommandStub.lastCall.calledWith(
                'setContext',
                addIntegrationTestProjectSupportedContext,
                true));
        }
        finally {
            availability.dispose();
        }
    });

    test('does not retry command availability for an unsupported CLI', async () => {
        const clock = sandbox.useFakeTimers({ shouldClearNativeTimers: true });
        getCapabilityStatusStub.resolves('unsupported');
        const availability = new AddIntegrationTestProjectAvailability(configInfoProvider);

        try {
            await availability.refresh();
            await clock.tickAsync(10_000);

            assert.strictEqual(getCapabilityStatusStub.callCount, 1);
            assert.strictEqual(executeCommandStub.neverCalledWith(
                'setContext',
                addIntegrationTestProjectSupportedContext,
                true), true);
        }
        finally {
            availability.dispose();
        }
    });

    test('cancels a scheduled availability retry when disposed', async () => {
        const clock = sandbox.useFakeTimers({ shouldClearNativeTimers: true });
        getCapabilityStatusStub.resolves('unavailable');
        const availability = new AddIntegrationTestProjectAvailability(configInfoProvider);

        await availability.refresh();
        availability.dispose();
        await clock.tickAsync(5_000);

        assert.strictEqual(getCapabilityStatusStub.callCount, 1);
        assert.strictEqual(executeCommandStub.neverCalledWith(
            'setContext',
            addIntegrationTestProjectSupportedContext,
            true), true);
    });

    test('cancels an active availability probe when disposed', async () => {
        let cancellationToken: vscode.CancellationToken | undefined;
        getCapabilityStatusStub.callsFake((_capability, options) => {
            cancellationToken = options.cancellationToken;
            return new Promise(resolve => cancellationToken?.onCancellationRequested(() => resolve('unavailable')));
        });
        const availability = new AddIntegrationTestProjectAvailability(configInfoProvider);

        const refresh = availability.refresh();
        await new Promise(resolve => setImmediate(resolve));
        availability.dispose();
        await refresh;

        assert.strictEqual(cancellationToken?.isCancellationRequested, true);
        assert.strictEqual(executeCommandStub.neverCalledWith(
            'setContext',
            addIntegrationTestProjectSupportedContext,
            true), true);
    });

    test('does not publish support from a superseded capability probe', async () => {
        let firstCancellationToken: vscode.CancellationToken | undefined;
        let completeFirstProbe: ((status: 'supported') => void) | undefined;
        getCapabilityStatusStub.onFirstCall().callsFake((_capability, options) => new Promise(resolve => {
            firstCancellationToken = options.cancellationToken;
            completeFirstProbe = resolve;
        }));
        getCapabilityStatusStub.onSecondCall().resolves('unsupported');
        const availability = new AddIntegrationTestProjectAvailability(configInfoProvider);

        try {
            const firstRefresh = availability.refresh();
            await new Promise(resolve => setImmediate(resolve));
            const secondRefresh = availability.refresh();
            await secondRefresh;
            completeFirstProbe?.('supported');
            await firstRefresh;

            assert.strictEqual(firstCancellationToken?.isCancellationRequested, true);
            assert.strictEqual(executeCommandStub.neverCalledWith(
                'setContext',
                addIntegrationTestProjectSupportedContext,
                true), true);
        }
        finally {
            availability.dispose();
        }
    });
});

function createWorkspaceFolder(name: string, fsPath: string): vscode.WorkspaceFolder {
    return {
        uri: vscode.Uri.file(fsPath),
        name,
        index: 0,
    };
}
