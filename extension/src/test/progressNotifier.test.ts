import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { ProgressNotifier } from '../server/progressNotifier';

type ProgressTask = (progress: vscode.Progress<{ message?: string; increment?: number }>, token: vscode.CancellationToken) => Thenable<unknown>;

suite('ProgressNotifier', () => {
    let sandbox: sinon.SinonSandbox;
    let withProgressStub: sinon.SinonStub;
    let reportedMessages: string[];
    let completeTask: (() => void) | undefined;

    setup(() => {
        sandbox = sinon.createSandbox();
        reportedMessages = [];
        completeTask = undefined;

        withProgressStub = sandbox.stub(vscode.window, 'withProgress').callsFake((_options: vscode.ProgressOptions, task: ProgressTask) => {
            const progress = {
                report: (value: { message?: string }) => {
                    if (value.message !== undefined) {
                        reportedMessages.push(value.message);
                    }
                }
            };

            const tokenSource = new vscode.CancellationTokenSource();
            const result = task(progress, tokenSource.token);
            completeTask = () => tokenSource.dispose();

            return result as Thenable<unknown>;
        });
    });

    teardown(() => {
        completeTask?.();
        sandbox.restore();
    });

    function getProgressOptions(callIndex = 0): vscode.ProgressOptions {
        return withProgressStub.getCall(callIndex).args[0] as vscode.ProgressOptions;
    }

    test('CLI status is reported as dismissible window progress rather than a notification', () => {
        const notifier = new ProgressNotifier();

        notifier.show('Building...');

        sinon.assert.calledOnce(withProgressStub);
        const options = getProgressOptions();
        // A notification progress cannot be dismissed while the operation runs, so it covers the
        // editor for the whole run (https://github.com/microsoft/aspire/issues/19036). Window
        // progress renders in the status bar instead.
        assert.strictEqual(options.location, vscode.ProgressLocation.Window);
        assert.notStrictEqual(options.location, vscode.ProgressLocation.Notification);
        assert.strictEqual(options.cancellable, undefined);
        assert.deepStrictEqual(reportedMessages, ['Building...']);

        notifier.clear();
    });

    test('a following status updates the existing progress instead of creating another one', () => {
        const notifier = new ProgressNotifier();

        notifier.show('Building...');
        notifier.show('Starting Dashboard...');

        sinon.assert.calledOnce(withProgressStub);
        assert.deepStrictEqual(reportedMessages, ['Building...', 'Starting Dashboard...']);

        notifier.clear();
    });

    test('clear ends the progress so a later status starts a new one', () => {
        const notifier = new ProgressNotifier();

        notifier.show('Building...');
        assert.strictEqual(notifier.isActive, true);

        notifier.clear();
        assert.strictEqual(notifier.isActive, false);

        notifier.show('Building...');
        assert.strictEqual(withProgressStub.callCount, 2);

        notifier.clear();
    });

    test('a null status defers the clear so a quick follow-up reuses the same progress', async () => {
        const clock = sandbox.useFakeTimers();
        const notifier = new ProgressNotifier();

        notifier.show('Building...');
        notifier.show(null);

        // The clear is deferred by 250ms so that a status that arrives right behind it updates the
        // existing progress rather than tearing it down and immediately recreating it.
        assert.strictEqual(notifier.isActive, true);
        clock.tick(100);
        notifier.show('Starting Dashboard...');
        clock.tick(500);

        sinon.assert.calledOnce(withProgressStub);
        assert.strictEqual(notifier.isActive, true);
        assert.deepStrictEqual(reportedMessages, ['Building...', 'Starting Dashboard...']);

        notifier.clear();
    });

    test('a null status with no follow-up clears the progress once the delay elapses', () => {
        const clock = sandbox.useFakeTimers();
        const notifier = new ProgressNotifier();

        notifier.show('Building...');
        notifier.show(null);
        clock.tick(500);

        assert.strictEqual(notifier.isActive, false);
    });

    test('emoji codes in CLI status are rendered as characters', () => {
        const notifier = new ProgressNotifier();

        notifier.show(':rocket: Starting Dashboard...');

        assert.deepStrictEqual(reportedMessages, ['🚀 Starting Dashboard...']);

        notifier.clear();
    });

    test('CLI status cannot inject icons or extra lines into the status bar', () => {
        const notifier = new ProgressNotifier();

        // Window progress renders in the status bar, which interprets `$(name)` as an icon and
        // shows a single line. The status text comes from the CLI, so neither is safe to pass on.
        notifier.show('Building $(sync~spin)\nwith an\nunexpected shape');
        notifier.show('Restoring $(error) packages');

        assert.deepStrictEqual(reportedMessages, [
            'Building \\$(sync~spin) with an unexpected shape',
            'Restoring \\$(error) packages',
        ]);

        notifier.clear();
    });
});
