/// <reference types="mocha" />

import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createWithAspireCommand } from '../commands/createWithAspire';

function createWorkspaceFolder(name: string, index: number): vscode.WorkspaceFolder {
    return {
        uri: vscode.Uri.file(`/repo/${name}`),
        name,
        index,
    };
}

suite('createWithAspireCommand', () => {
    let sandbox: sinon.SinonSandbox;
    let showQuickPickStub: sinon.SinonStub;
    let executeCommandStub: sinon.SinonStub;

    setup(() => {
        sandbox = sinon.createSandbox();
        showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick');
        executeCommandStub = sandbox.stub(vscode.commands, 'executeCommand').resolves(undefined);
    });

    teardown(() => {
        sandbox.restore();
    });

    test('offers both actions when no workspace is open', async () => {
        sandbox.stub(vscode.workspace, 'workspaceFolders').value(undefined);
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.init'));

        await createWithAspireCommand();

        assert.ok(showQuickPickStub.calledOnce);
        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new', 'aspire-vscode.init']);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', undefined, 'tree'));
    });

    test('offers both actions when a workspace is open', async () => {
        const folder = createWorkspaceFolder('with-apphost', 0);
        sandbox.stub(vscode.workspace, 'workspaceFolders').value([folder]);
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.init'));

        await createWithAspireCommand();

        assert.ok(showQuickPickStub.calledOnce);
        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new', 'aspire-vscode.init']);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', undefined, 'tree'));
    });

    test('delegates to new when the new-app option is selected', async () => {
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.new'));

        await createWithAspireCommand();

        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.new', 'tree'));
    });

    test('returns the handled cancellation from delegated new', async () => {
        const handledCancellation = { success: false as const, canceled: true };
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.new'));
        executeCommandStub.resolves(handledCancellation);

        const result = await createWithAspireCommand();

        assert.strictEqual(result, handledCancellation);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.new', 'tree'));
    });

    test('returns the handled error from delegated init', async () => {
        const handledError = { success: false as const, errorKind: 'Error' };
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.init'));
        executeCommandStub.resolves(handledError);

        const result = await createWithAspireCommand();

        assert.strictEqual(result, handledError);
        assert.ok(executeCommandStub.calledOnceWithExactly('aspire-vscode.init', undefined, 'tree'));
    });

    test('throws cancellation when the action picker is dismissed', async () => {
        showQuickPickStub.resolves(undefined);

        await assert.rejects(
            () => createWithAspireCommand(),
            error => error instanceof vscode.CancellationError);

        assert.strictEqual(executeCommandStub.called, false);
    });
});
