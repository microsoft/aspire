import * as assert from 'assert';
import * as vscode from 'vscode';
import { CliPathRejectionNotifier, CliPathRejectionNotificationSurface, CliPathRejectionState } from '../utils/cliPathRejectionNotifier';
import { CliPathResolutionTarget, windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { configuredCliPathRejectedOpenSetting } from '../loc/strings';

suite('cliPathRejectionNotifier', () => {

    class FakeSurface implements CliPathRejectionNotificationSurface {
        readonly warnings: string[] = [];
        readonly openedSettings: string[] = [];
        selection: string | undefined;

        showWarning(message: string, ..._actions: string[]): Thenable<string | undefined> {
            this.warnings.push(message);
            return Promise.resolve(this.selection);
        }

        openSetting(settingId: string): Thenable<unknown> {
            this.openedSettings.push(settingId);
            return Promise.resolve(undefined);
        }
    }

    class FakeState implements CliPathRejectionState {
        configuredPath = '';
        rejected = false;
        listener: ((target: CliPathResolutionTarget) => void) | undefined;

        getConfiguredPath(): string {
            return this.configuredPath;
        }

        isRejected(configuredPath: string): boolean {
            return this.rejected && configuredPath === this.configuredPath;
        }

        onDidChangeRejection(listener: (target: CliPathResolutionTarget) => void): vscode.Disposable {
            this.listener = listener;
            return { dispose: () => { this.listener = undefined; } };
        }
    }

    function createNotifier(): { notifier: CliPathRejectionNotifier; surface: FakeSurface; state: FakeState } {
        const surface = new FakeSurface();
        const state = new FakeState();
        const notifier = new CliPathRejectionNotifier(surface, state);
        return { notifier, surface, state };
    }

    test('warns with the rejected path when the configured CLI path is rejected', async () => {
        const { notifier, surface, state } = createNotifier();
        state.configuredPath = '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0';
        state.rejected = true;

        await notifier.notifyIfRejected(windowCliPathTarget);

        assert.strictEqual(surface.warnings.length, 1);
        assert.ok(surface.warnings[0].includes('/repo/artifacts/bin/Aspire.Cli/Debug/net10.0'));
        notifier.dispose();
    });

    test('does not warn when the configured path resolved successfully', async () => {
        const { notifier, surface, state } = createNotifier();
        state.configuredPath = '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire';
        state.rejected = false;

        await notifier.notifyIfRejected(windowCliPathTarget);

        assert.deepStrictEqual(surface.warnings, []);
        notifier.dispose();
    });

    test('does not warn when no path is configured', async () => {
        const { notifier, surface, state } = createNotifier();
        state.configuredPath = '';
        state.rejected = true;

        await notifier.notifyIfRejected(windowCliPathTarget);

        assert.deepStrictEqual(surface.warnings, []);
        notifier.dispose();
    });

    test('warns only once for the same rejected path', async () => {
        const { notifier, surface, state } = createNotifier();
        state.configuredPath = '/bad/path';
        state.rejected = true;

        await notifier.notifyIfRejected(windowCliPathTarget);
        await notifier.notifyIfRejected(windowCliPathTarget);
        await notifier.notifyIfRejected(windowCliPathTarget);

        assert.strictEqual(surface.warnings.length, 1);
        notifier.dispose();
    });

    test('warns again when the user changes the setting to a different bad path', async () => {
        const { notifier, surface, state } = createNotifier();
        state.configuredPath = '/bad/first';
        state.rejected = true;
        await notifier.notifyIfRejected(windowCliPathTarget);

        state.configuredPath = '/bad/second';
        await notifier.notifyIfRejected(windowCliPathTarget);

        assert.strictEqual(surface.warnings.length, 2);
        assert.ok(surface.warnings[0].includes('/bad/first'));
        assert.ok(surface.warnings[1].includes('/bad/second'));
        notifier.dispose();
    });

    test('warns again for the same path after it recovers and is rejected once more', async () => {
        const { notifier, surface, state } = createNotifier();
        state.configuredPath = '/bad/path';
        state.rejected = true;
        await notifier.notifyIfRejected(windowCliPathTarget);

        state.rejected = false;
        await notifier.notifyIfRejected(windowCliPathTarget);

        state.rejected = true;
        await notifier.notifyIfRejected(windowCliPathTarget);

        assert.strictEqual(surface.warnings.length, 2);
        notifier.dispose();
    });

    test('tracks rejections per resolution scope', async () => {
        const { notifier, surface, state } = createNotifier();
        const folderTarget = workspaceFolderCliPathTarget({
            uri: vscode.Uri.file('/repo/app'),
            name: 'app',
            index: 0,
        });
        state.configuredPath = '/bad/path';
        state.rejected = true;

        await notifier.notifyIfRejected(windowCliPathTarget);
        await notifier.notifyIfRejected(folderTarget);

        assert.strictEqual(surface.warnings.length, 2);
        notifier.dispose();
    });

    test('opens the setting when the user selects the action', async () => {
        const { notifier, surface, state } = createNotifier();
        state.configuredPath = '/bad/path';
        state.rejected = true;
        surface.selection = configuredCliPathRejectedOpenSetting;

        await notifier.notifyIfRejected(windowCliPathTarget);

        assert.deepStrictEqual(surface.openedSettings, ['aspire.aspireCliExecutablePath']);
        notifier.dispose();
    });

    test('does not open the setting when the warning is dismissed', async () => {
        const { notifier, surface, state } = createNotifier();
        state.configuredPath = '/bad/path';
        state.rejected = true;
        surface.selection = undefined;

        await notifier.notifyIfRejected(windowCliPathTarget);

        assert.deepStrictEqual(surface.openedSettings, []);
        notifier.dispose();
    });

    test('notifies when the resolver reports a rejection change', async () => {
        const { notifier, surface, state } = createNotifier();
        state.configuredPath = '/bad/path';
        state.rejected = true;

        assert.ok(state.listener, 'notifier should subscribe to rejection changes');
        state.listener!(windowCliPathTarget);
        await new Promise(resolve => setImmediate(resolve));

        assert.strictEqual(surface.warnings.length, 1);
        notifier.dispose();
    });

    test('unsubscribes on dispose', () => {
        const { notifier, state } = createNotifier();

        notifier.dispose();

        assert.strictEqual(state.listener, undefined);
    });
});
