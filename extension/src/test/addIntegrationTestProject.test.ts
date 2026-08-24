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
    let hasCapabilityStub: sinon.SinonStub;
    let showErrorMessageStub: sinon.SinonStub;
    let executeCommandStub: sinon.SinonStub;

    setup(() => {
        sandbox = sinon.createSandbox();
        sendCommandStub = sandbox.stub().resolves();
        terminalProvider = {
            sendAspireCommandToAspireTerminal: sendCommandStub,
        } as unknown as AspireTerminalProvider;
        hasCapabilityStub = sandbox.stub().resolves(true);
        configInfoProvider = {
            hasCapability: hasCapabilityStub,
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

        assert.ok(hasCapabilityStub.calledOnceWith(aspireTestAppHostCapability, {
            cliPath: '/selected/aspire',
            target,
            forceRefresh: true,
            suppressErrors: true,
        }));
        assert.ok(sendCommandStub.calledOnceWith(
            ['new', 'aspire-test'],
            true,
            ['--apphost', appHostPath],
            { cliPath: '/selected/aspire', target }));
    });

    test('does not invoke a CLI that does not advertise support', async () => {
        hasCapabilityStub.resolves(false);

        await addIntegrationTestProject(
            terminalProvider,
            configInfoProvider,
            path.join('repo', 'AppHost.csproj'),
            windowCliPathTarget,
            '/selected/aspire');

        assert.ok(showErrorMessageStub.calledOnceWith(addIntegrationTestProjectUnsupported));
        assert.strictEqual(sendCommandStub.called, false);
    });

    test('rejects a non-CSharp AppHost before checking the capability', async () => {
        await addIntegrationTestProject(
            terminalProvider,
            configInfoProvider,
            path.join('repo', 'app.ts'),
            windowCliPathTarget,
            '/selected/aspire');

        assert.ok(showErrorMessageStub.calledOnceWith(addIntegrationTestProjectRequiresCSharpAppHost));
        assert.strictEqual(hasCapabilityStub.called, false);
        assert.strictEqual(sendCommandStub.called, false);
    });

    test('publishes command availability from the active CLI capability', async () => {
        const availability = new AddIntegrationTestProjectAvailability(configInfoProvider);

        try {
            await availability.refresh();

            assert.ok(hasCapabilityStub.calledOnceWith(aspireTestAppHostCapability, {
                target: windowCliPathTarget,
                forceRefresh: false,
                suppressErrors: true,
            }));
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

    test('does not publish support from a superseded capability probe', async () => {
        let completeFirstProbe: ((supported: boolean) => void) | undefined;
        hasCapabilityStub.onFirstCall().returns(new Promise(resolve => {
            completeFirstProbe = resolve;
        }));
        hasCapabilityStub.onSecondCall().resolves(false);
        const availability = new AddIntegrationTestProjectAvailability(configInfoProvider);

        try {
            const firstRefresh = availability.refresh();
            await new Promise(resolve => setImmediate(resolve));
            const secondRefresh = availability.refresh();
            await secondRefresh;
            completeFirstProbe?.(true);
            await firstRefresh;

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
