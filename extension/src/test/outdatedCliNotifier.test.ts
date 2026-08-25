import * as assert from 'assert';
import * as vscode from 'vscode';
import * as strings from '../loc/strings';
import { CliVersionStatus, CliVersionStatusOptions } from '../utils/configInfoProvider';
import { windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { minimumSupportedAspireCliVersion, OutdatedCliNotificationSurface, OutdatedCliNotifier } from '../utils/outdatedCliNotifier';

suite('outdatedCliNotifier', () => {
    class FakeVersionProvider {
        result: CliVersionStatus | null = null;
        resultPromise: Promise<CliVersionStatus | null> | undefined;
        readonly calls: Array<{ minimumVersion: string; options?: CliVersionStatusOptions }> = [];

        async getCliVersionStatus(minimumVersion: string, options?: CliVersionStatusOptions): Promise<CliVersionStatus | null> {
            this.calls.push({ minimumVersion, options });
            return await (this.resultPromise ?? this.result);
        }
    }

    class FakeSurface implements OutdatedCliNotificationSurface {
        readonly warnings: Array<{ message: string; actions: string[] }> = [];
        readonly commands: Array<{ command: string; args: unknown[] }> = [];
        selection: string | undefined;
        selectionPromise: Promise<string | undefined> | undefined;

        showWarning(message: string, ...actions: string[]): Thenable<string | undefined> {
            this.warnings.push({ message, actions });
            return this.selectionPromise ?? Promise.resolve(this.selection);
        }

        executeCommand(command: string, ...args: unknown[]): Thenable<unknown> {
            this.commands.push({ command, args });
            return Promise.resolve(undefined);
        }
    }

    function createNotifier(): {
        notifier: OutdatedCliNotifier;
        versionProvider: FakeVersionProvider;
        surface: FakeSurface;
    } {
        const versionProvider = new FakeVersionProvider();
        const surface = new FakeSurface();
        return {
            notifier: new OutdatedCliNotifier(versionProvider, surface),
            versionProvider,
            surface,
        };
    }

    test('does not warn for supported or unavailable CLI versions', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        versionProvider.result = {
            cliPath: '/cli/aspire',
            version: '13.5.0',
            status: 'supported',
        };

        await notifier.notifyIfOutdated(windowCliPathTarget);
        versionProvider.result = null;
        await notifier.notifyIfOutdated(windowCliPathTarget);

        assert.deepStrictEqual(surface.warnings, []);
        assert.deepStrictEqual(
            versionProvider.calls.map(call => call.minimumVersion),
            [minimumSupportedAspireCliVersion, minimumSupportedAspireCliVersion]);
        notifier.dispose();
    });

    test('warns only once for the same resolved executable and version', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        versionProvider.result = {
            cliPath: '/cli/aspire',
            version: '13.4.9',
            status: 'unsupported',
        };

        await notifier.notifyIfOutdated(windowCliPathTarget);
        await notifier.notifyIfOutdated(windowCliPathTarget);
        await notifier.notifyIfOutdated(windowCliPathTarget);

        assert.strictEqual(surface.warnings.length, 1);
        assert.strictEqual(
            surface.warnings[0].message,
            "Aspire CLI 13.4.9 is older than 13.5.0. Update the CLI and the AppHost's Aspire packages to 13.5.0 or later to avoid a known startup failure in VS Code.");
        notifier.dispose();
    });

    test('warns for distinct CLI paths and replacements at the same path', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        const outdatedVersions = [
            { cliPath: '/cli/first/aspire', version: '13.4.8' },
            { cliPath: '/cli/second/aspire', version: '13.4.8' },
            { cliPath: '/cli/first/aspire', version: '13.4.9' },
        ];

        for (const version of outdatedVersions) {
            versionProvider.result = { ...version, status: 'unsupported' };
            await notifier.notifyIfOutdated(windowCliPathTarget, version.cliPath);
        }

        assert.strictEqual(surface.warnings.length, outdatedVersions.length);
        assert.deepStrictEqual(
            surface.warnings.map(warning => warning.message),
            outdatedVersions.map(version =>
                `Aspire CLI ${version.version} is older than 13.5.0. Update the CLI and the AppHost's Aspire packages to 13.5.0 or later to avoid a known startup failure in VS Code.`));
        notifier.dispose();
    });

    test('updates the exact workspace CLI that triggered the warning', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        const target = workspaceFolderCliPathTarget({
            uri: vscode.Uri.file('/workspace/a'),
            name: 'a',
            index: 0,
        });
        versionProvider.result = {
            cliPath: '/workspace/a/.aspire/bin/aspire',
            version: '13.4.9',
            status: 'unsupported',
        };
        surface.selection = strings.updateAspireCliAction;

        await notifier.notifyIfOutdated(target);

        assert.deepStrictEqual(surface.warnings[0].actions, [strings.updateAspireCliAction]);
        assert.deepStrictEqual(surface.commands, [{
            command: 'aspire-vscode.updateSelf',
            args: [target, '/workspace/a/.aspire/bin/aspire'],
        }]);
        notifier.dispose();
    });

    test('dispose cancels an in-flight version probe and suppresses its warning', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        let resolveProbe!: (result: CliVersionStatus | null) => void;
        versionProvider.resultPromise = new Promise(resolve => resolveProbe = resolve);

        const notification = notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        const cancellationToken = versionProvider.calls[0].options?.cancellationToken;
        assert.ok(cancellationToken);
        assert.strictEqual(cancellationToken.isCancellationRequested, false);

        notifier.dispose();
        assert.strictEqual(cancellationToken.isCancellationRequested, true);
        resolveProbe({
            cliPath: '/cli/aspire',
            version: '13.4.9',
            status: 'unsupported',
        });
        await notification;

        assert.deepStrictEqual(surface.warnings, []);
        assert.deepStrictEqual(surface.commands, []);
    });

    test('dispose while the warning awaits selection suppresses the update action', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        versionProvider.result = {
            cliPath: '/cli/aspire',
            version: '13.4.9',
            status: 'unsupported',
        };
        let resolveSelection!: (selection: string | undefined) => void;
        surface.selectionPromise = new Promise(resolve => resolveSelection = resolve);

        const notification = notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await new Promise(resolve => setImmediate(resolve));
        assert.strictEqual(surface.warnings.length, 1);

        notifier.dispose();
        resolveSelection(strings.updateAspireCliAction);
        await notification;

        assert.deepStrictEqual(surface.commands, []);
    });
});
