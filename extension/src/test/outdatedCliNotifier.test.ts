import * as assert from 'assert';
import * as vscode from 'vscode';
import * as strings from '../loc/strings';
import { CliVersionStatus, CliVersionStatusOptions } from '../utils/configInfoProvider';
import { windowCliPathTarget } from '../utils/cliPathVariables';
import { minimumSupportedAspireCliVersion, OutdatedCliNotificationSurface, OutdatedCliNotifier } from '../utils/outdatedCliNotifier';

suite('outdatedCliNotifier', () => {
    class FakeVersionProvider {
        result: CliVersionStatus | null = null;
        readonly calls: Array<{ minimumVersion: string; options?: CliVersionStatusOptions }> = [];

        async getCliVersionStatus(minimumVersion: string, options?: CliVersionStatusOptions): Promise<CliVersionStatus | null> {
            this.calls.push({ minimumVersion, options });
            return this.result;
        }
    }

    class FakeSurface implements OutdatedCliNotificationSurface {
        readonly warnings: Array<{ message: string; actions: string[] }> = [];
        readonly commands: string[] = [];
        selection: string | undefined;

        showWarning(message: string, ...actions: string[]): Thenable<string | undefined> {
            this.warnings.push({ message, actions });
            return Promise.resolve(this.selection);
        }

        executeCommand(command: string): Thenable<unknown> {
            this.commands.push(command);
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
            'Aspire CLI 13.4.9 is older than 13.5.0. Update it to avoid a known AppHost startup failure in VS Code.');
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
                `Aspire CLI ${version.version} is older than 13.5.0. Update it to avoid a known AppHost startup failure in VS Code.`));
        notifier.dispose();
    });

    test('invokes the existing update-self command when the action is selected', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        versionProvider.result = {
            cliPath: '/cli/aspire',
            version: '13.4.9',
            status: 'unsupported',
        };
        surface.selection = strings.updateAspireCliAction;

        await notifier.notifyIfOutdated(windowCliPathTarget);

        assert.deepStrictEqual(surface.warnings[0].actions, [strings.updateAspireCliAction]);
        assert.deepStrictEqual(surface.commands, ['aspire-vscode.updateSelf']);
        notifier.dispose();
    });
});
