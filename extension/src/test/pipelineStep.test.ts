import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { doCommand } from '../commands/do';
import { AppHostCliRunner } from '../data/appHostCliRunner';
import { AspireEditorCommandProvider } from '../editor/AspireEditorCommandProvider';
import { enterPipelineStep, loadingPipelineSteps, pipelineStepRequired, selectPipelineStep } from '../loc/strings';
import { ConfigInfoProvider } from '../utils/configInfoProvider';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { resolvePipelineStep, selectPipelineStepFromCli } from '../utils/pipelineStep';

suite('pipeline step resolution', () => {
    let sandbox: sinon.SinonSandbox;
    let configInfoProvider: ConfigInfoProvider;
    let hasCapabilityStub: sinon.SinonStub;
    const cliPath = '/repo/b/tools/aspire';
    const appHostPath = '/repo/b/AppHost/AppHost.csproj';
    const workspaceFolder: vscode.WorkspaceFolder = {
        uri: vscode.Uri.file('/repo/b'),
        name: 'b',
        index: 1,
    };
    const target = workspaceFolderCliPathTarget(workspaceFolder);

    setup(() => {
        sandbox = sinon.createSandbox();
        hasCapabilityStub = sandbox.stub().resolves(false);
        configInfoProvider = {
            hasCapability: hasCapabilityStub,
        } as unknown as ConfigInfoProvider;
    });

    teardown(() => {
        sandbox.restore();
    });

    test('capable CLI uses its interaction service with the exact target and CLI path', async () => {
        hasCapabilityStub.resolves(true);
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox');

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath);

        assert.strictEqual(step, null);
        assert.ok(hasCapabilityStub.calledOnceWithExactly('pipelines', {
            target,
            cliPath,
            suppressErrors: true,
            forceRefresh: true,
        }));
        assert.strictEqual(showInputBoxStub.called, false);
    });

    test('known interaction support does not re-probe the CLI', async () => {
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox');

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath, true);

        assert.strictEqual(step, null);
        assert.strictEqual(hasCapabilityStub.called, false);
        assert.strictEqual(showInputBoxStub.called, false);
    });

    test('known legacy support uses local input without re-probing the CLI', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves('  deploy  ');

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath, false);

        assert.strictEqual(step, 'deploy');
        assert.strictEqual(hasCapabilityStub.called, false);
    });

    test('legacy CLI trims locally entered pipeline steps', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves('  deploy  ');

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath);

        assert.strictEqual(step, 'deploy');
    });

    test('legacy CLI rejects whitespace-only pipeline steps with the localized validation message', async () => {
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox').callsFake(async options => {
            assert.strictEqual(await options?.validateInput?.('   '), pipelineStepRequired);
            return undefined;
        });

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath);

        assert.strictEqual(step, undefined);
        assert.strictEqual(showInputBoxStub.calledOnce, true);
    });

    test('input cancellation returns undefined', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves(undefined);

        const step = await resolvePipelineStep(configInfoProvider, target, cliPath);

        assert.strictEqual(step, undefined);
    });

    test('structured list uses the pinned CLI and maps metadata to a quick pick', async () => {
        const runCliCommandStub = sandbox.stub().resolves({
            stdout: JSON.stringify([
                {
                    name: 'publish',
                    description: 'Publish artifacts',
                    dependsOn: ['build'],
                    tags: ['publish'],
                    resourceName: 'api',
                },
                {
                    name: 'deploy',
                    dependsOn: ['publish'],
                    tags: [],
                },
            ]),
            stderr: '',
        });
        const cliRunner = {
            withNoLogo: sandbox.stub().callsFake(args => [...args, '--nologo']),
            runCliCommand: runCliCommandStub,
        } as unknown as AppHostCliRunner;
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items =>
            (items as readonly vscode.QuickPickItem[])[1]);

        const step = await selectPipelineStepFromCli(cliRunner, appHostPath, target, cliPath);

        assert.strictEqual(step, 'deploy');
        assert.strictEqual(runCliCommandStub.callCount, 1);
        assert.strictEqual(runCliCommandStub.firstCall.args[0], 'list pipeline steps');
        assert.deepStrictEqual(runCliCommandStub.firstCall.args[1],
            ['do', '--list-steps', '--format', 'json', '--apphost', appHostPath, '--nologo']);
        assert.strictEqual(runCliCommandStub.firstCall.args[2].target, target);
        assert.strictEqual(runCliCommandStub.firstCall.args[2].cliPath, cliPath);
        assert.strictEqual(runCliCommandStub.firstCall.args[2].timeoutMs, null);
        assert.ok(runCliCommandStub.firstCall.args[2].cancellationToken);
        const items = showQuickPickStub.firstCall.args[0] as readonly vscode.QuickPickItem[];
        assert.deepStrictEqual(items.map(item => ({
            label: item.label,
            description: item.description,
            detail: item.detail,
        })), [
                { label: 'publish', description: 'Publish artifacts', detail: 'api' },
                { label: 'deploy', description: undefined, detail: undefined },
                { label: enterPipelineStep, description: undefined, detail: undefined },
            ]);
        assert.deepStrictEqual(showQuickPickStub.firstCall.args[1], {
            placeHolder: selectPipelineStep,
            matchOnDescription: true,
            matchOnDetail: true,
        });
    });

    test('structured list supports entering a pipeline step that was not discovered', async () => {
        const cliRunner = {
            withNoLogo: sandbox.stub().returns(['do']),
            runCliCommand: sandbox.stub().resolves({
                stdout: '[{"name":"deploy","dependsOn":[],"tags":[]}]',
                stderr: '',
            }),
        } as unknown as AppHostCliRunner;
        sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items => {
            const quickPickItems = items as readonly vscode.QuickPickItem[];
            assert.strictEqual(quickPickItems.at(-1)?.label, enterPipelineStep);
            return quickPickItems.at(-1);
        });
        sandbox.stub(vscode.window, 'showInputBox').resolves('  dynamic-step  ');

        const step = await selectPipelineStepFromCli(cliRunner, appHostPath, target, cliPath);

        assert.strictEqual(step, 'dynamic-step');
    });

    test('structured list cancellation returns undefined', async () => {
        const cliRunner = {
            withNoLogo: sandbox.stub().returns(['do']),
            runCliCommand: sandbox.stub().resolves({
                stdout: '[{"name":"deploy","dependsOn":[],"tags":[]}]',
                stderr: '',
            }),
        } as unknown as AppHostCliRunner;
        sandbox.stub(vscode.window, 'showQuickPick').resolves(undefined);
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox');

        const step = await selectPipelineStepFromCli(cliRunner, appHostPath, target, cliPath);

        assert.strictEqual(step, undefined);
        assert.strictEqual(showInputBoxStub.called, false);
    });

    test('structured manual entry cancellation returns undefined', async () => {
        const cliRunner = {
            withNoLogo: sandbox.stub().returns(['do']),
            runCliCommand: sandbox.stub().resolves({
                stdout: '[{"name":"deploy","dependsOn":[],"tags":[]}]',
                stderr: '',
            }),
        } as unknown as AppHostCliRunner;
        sandbox.stub(vscode.window, 'showQuickPick').callsFake(async items =>
            (items as readonly vscode.QuickPickItem[]).at(-1));
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox').resolves(undefined);

        const step = await selectPipelineStepFromCli(cliRunner, appHostPath, target, cliPath);

        assert.strictEqual(step, undefined);
        assert.strictEqual(showInputBoxStub.calledOnce, true);
    });

    test('structured listing can be canceled before the picker opens', async () => {
        const cancellationSource = new vscode.CancellationTokenSource();
        const runCliCommandStub = sandbox.stub().callsFake(async (_command, _args, options) => {
            assert.strictEqual(options.cancellationToken, cancellationSource.token);
            throw new vscode.CancellationError();
        });
        const cliRunner = {
            withNoLogo: sandbox.stub().returns(['do']),
            runCliCommand: runCliCommandStub,
        } as unknown as AppHostCliRunner;
        const withProgressStub = sandbox.stub(vscode.window, 'withProgress').callsFake(async (options, task) => {
            assert.deepStrictEqual(options, {
                location: vscode.ProgressLocation.Notification,
                title: loadingPipelineSteps,
                cancellable: true,
            });
            return task({ report: () => { } }, cancellationSource.token);
        });
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick');

        await assert.rejects(
            selectPipelineStepFromCli(cliRunner, appHostPath, target, cliPath),
            error => error instanceof vscode.CancellationError);

        assert.strictEqual(withProgressStub.calledOnce, true);
        assert.strictEqual(runCliCommandStub.calledOnce, true);
        assert.strictEqual(showQuickPickStub.called, false);
        cancellationSource.dispose();
    });

    test('structured empty list directly prompts for a pipeline step', async () => {
        const cliRunner = {
            withNoLogo: sandbox.stub().returns(['do']),
            runCliCommand: sandbox.stub().resolves({ stdout: '[]', stderr: '' }),
        } as unknown as AppHostCliRunner;
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick');
        const showInformationMessageStub = sandbox.stub(vscode.window, 'showInformationMessage');
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox').callsFake(async options => {
            assert.strictEqual(options?.prompt, enterPipelineStep);
            assert.strictEqual(options?.placeHolder, 'deploy');
            assert.strictEqual(await options?.validateInput?.('   '), pipelineStepRequired);
            assert.strictEqual(await options?.validateInput?.('dynamic-step'), undefined);
            return '  dynamic-step  ';
        });

        const step = await selectPipelineStepFromCli(cliRunner, appHostPath, target, cliPath);

        assert.strictEqual(step, 'dynamic-step');
        assert.strictEqual(showQuickPickStub.called, false);
        assert.strictEqual(showInformationMessageStub.called, false);
        assert.strictEqual(showInputBoxStub.calledOnce, true);
    });

    test('structured list rejects malformed pipeline step metadata', async () => {
        const cliRunner = {
            withNoLogo: sandbox.stub().returns(['do']),
            runCliCommand: sandbox.stub().resolves({
                stdout: '[{"name":"deploy","dependsOn":"publish","tags":[]}]',
                stderr: '',
            }),
        } as unknown as AppHostCliRunner;
        const showQuickPickStub = sandbox.stub(vscode.window, 'showQuickPick');

        await assert.rejects(selectPipelineStepFromCli(cliRunner, appHostPath, target, cliPath));

        assert.strictEqual(showQuickPickStub.called, false);
    });

    test('non-cancellation errors propagate', async () => {
        const error = new Error('capability probe failed');
        hasCapabilityStub.rejects(error);
        const showInputBoxStub = sandbox.stub(vscode.window, 'showInputBox');

        await assert.rejects(resolvePipelineStep(configInfoProvider, target, cliPath), error);

        assert.strictEqual(showInputBoxStub.called, false);
    });

    test('doCommand preserves its five arguments through resolution and launch', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves('  release  ');
        const tryExecuteDoAppHostStub = sandbox.stub().resolves();
        const editorCommandProvider = {
            tryExecuteDoAppHost: tryExecuteDoAppHostStub,
        } as unknown as AspireEditorCommandProvider;

        await doCommand(configInfoProvider, editorCommandProvider, appHostPath, target, cliPath);

        assert.ok(hasCapabilityStub.calledOnceWithExactly('pipelines', {
            target,
            cliPath,
            suppressErrors: true,
            forceRefresh: true,
        }));
        assert.ok(tryExecuteDoAppHostStub.calledOnceWithExactly(false, 'release', appHostPath, target, cliPath));
    });

    test('doCommand treats pipeline-step cancellation as cancellation without launching', async () => {
        sandbox.stub(vscode.window, 'showInputBox').resolves(undefined);
        const tryExecuteDoAppHostStub = sandbox.stub().resolves();
        const editorCommandProvider = {
            tryExecuteDoAppHost: tryExecuteDoAppHostStub,
        } as unknown as AspireEditorCommandProvider;

        await assert.rejects(
            doCommand(configInfoProvider, editorCommandProvider, appHostPath, target, cliPath),
            error => error instanceof vscode.CancellationError);

        assert.strictEqual(tryExecuteDoAppHostStub.called, false);
    });
});
