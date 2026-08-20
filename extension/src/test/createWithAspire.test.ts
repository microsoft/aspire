/// <reference types="mocha" />

import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createWithAspireCommand } from '../commands/createWithAspire';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';

function createEditorCommandProvider(hasWorkspaceFolderWithoutAppHost: boolean): AspireEditorCommandProvider {
    return {
        hasWorkspaceFolderWithoutAppHost: async () => hasWorkspaceFolderWithoutAppHost,
    } as unknown as AspireEditorCommandProvider;
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

    test('offers only the new-app option when every workspace folder already has an AppHost', async () => {
        await createWithAspireCommand(createEditorCommandProvider(false));

        assert.ok(showQuickPickStub.calledOnce);
        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.strictEqual(items.length, 1);
        assert.strictEqual(items[0].command, 'aspire-vscode.new');
    });

    test('also offers the add-to-workspace option when an applicable folder lacks an AppHost', async () => {
        await createWithAspireCommand(createEditorCommandProvider(true));

        assert.ok(showQuickPickStub.calledOnce);
        const items = showQuickPickStub.firstCall.args[0] as { command: string }[];
        assert.strictEqual(items.length, 2);
        assert.deepStrictEqual(items.map(item => item.command), ['aspire-vscode.new', 'aspire-vscode.init']);
    });

    test('invokes aspire-vscode.new when the new-app option is selected', async () => {
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.new'));

        await createWithAspireCommand(createEditorCommandProvider(true));

        assert.ok(executeCommandStub.calledOnceWith('aspire-vscode.new'));
    });

    test('invokes aspire-vscode.init when the add-to-workspace option is selected', async () => {
        showQuickPickStub.callsFake(async (items: { command: string }[]) => items.find(item => item.command === 'aspire-vscode.init'));

        await createWithAspireCommand(createEditorCommandProvider(true));

        assert.ok(executeCommandStub.calledOnceWith('aspire-vscode.init'));
    });

    test('does nothing when the quick pick is dismissed', async () => {
        showQuickPickStub.resolves(undefined);

        await createWithAspireCommand(createEditorCommandProvider(true));

        assert.strictEqual(executeCommandStub.called, false);
    });
});
