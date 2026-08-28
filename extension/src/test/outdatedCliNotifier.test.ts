import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as strings from '../loc/strings';
import {
    CliUpdateRecommendation,
    CliUpdateRecommendationOptions,
    CliVersionInfo,
    CliVersionStatusOptions,
} from '../utils/configInfoProvider';
import { windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { OutdatedCliNotificationSurface, OutdatedCliNotifier } from '../utils/outdatedCliNotifier';

suite('outdatedCliNotifier', () => {
    class FakeVersionProvider {
        identity: CliVersionInfo | null = {
            cliPath: '/cli/aspire',
            version: '13.5.0',
        };
        identityPromise: Promise<CliVersionInfo | null> | undefined;
        currentVersion: CliVersionInfo | null | undefined;
        recommendation: CliUpdateRecommendation = {
            status: 'available',
            currentVersion: '13.5.0',
            version: '13.6.0',
        };
        readonly versionCalls: Array<CliVersionStatusOptions | undefined> = [];
        readonly recommendationCalls: Array<CliUpdateRecommendationOptions | undefined> = [];

        async getCliVersion(options?: CliVersionStatusOptions): Promise<CliVersionInfo | null> {
            this.versionCalls.push(options);
            return this.currentVersion !== undefined
                ? this.currentVersion
                : await (this.identityPromise ?? this.identity);
        }

        async getCliUpdateRecommendation(options?: CliUpdateRecommendationOptions): Promise<CliUpdateRecommendation> {
            this.recommendationCalls.push(options);
            return this.recommendation;
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

    function createNotifier(now: () => number = Date.now, globalState?: vscode.Memento): {
        notifier: OutdatedCliNotifier;
        versionProvider: FakeVersionProvider;
        surface: FakeSurface;
    } {
        const versionProvider = new FakeVersionProvider();
        const surface = new FakeSurface();
        return {
            notifier: new OutdatedCliNotifier(versionProvider, surface, now, globalState),
            versionProvider,
            surface,
        };
    }

    function createMemento(): vscode.Memento {
        const values = new Map<string, unknown>();
        return {
            keys: () => [...values.keys()],
            get: <T>(key: string, defaultValue?: T): T | undefined =>
                values.has(key) ? values.get(key) as T : defaultValue,
            update: async (key: string, value: unknown): Promise<void> => {
                if (value === undefined) {
                    values.delete(key);
                } else {
                    values.set(key, value);
                }
            },
        };
    }

    async function waitFor(predicate: () => boolean, message: string): Promise<void> {
        for (let attempt = 0; attempt < 100; attempt++) {
            if (predicate()) {
                return;
            }
            await new Promise(resolve => setImmediate(resolve));
        }
        assert.fail(message);
    }

    test('warns once and forwards the exact target and path', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        const target = workspaceFolderCliPathTarget({
            uri: vscode.Uri.file('/workspace/a'),
            name: 'a',
            index: 0,
        });
        versionProvider.identity = {
            cliPath: '/workspace/a/.aspire/bin/aspire',
            version: '13.4.0',
        };
        versionProvider.recommendation = {
            status: 'available',
            currentVersion: '13.4.0',
            version: '15.3.2',
        };
        versionProvider.currentVersion = versionProvider.identity;
        surface.selection = strings.updateAspireCliAction;

        await notifier.notifyIfOutdated(target, '/workspace/a/.aspire/bin/aspire');
        await notifier.notifyIfOutdated(target, '/workspace/a/.aspire/bin/aspire');

        assert.strictEqual(surface.warnings.length, 1);
        assert.strictEqual(
            surface.warnings[0].message,
            'Aspire CLI 13.4.0 at /workspace/a/.aspire/bin/aspire has a newer version available for its current channel: 15.3.2.');
        assert.deepStrictEqual(surface.warnings[0].actions, [
            strings.updateAspireCliAction,
            strings.dontShowAgainLabel,
        ]);
        assert.deepStrictEqual(surface.commands, [{
            command: 'aspire-vscode.updateSelf',
            args: [target, '/workspace/a/.aspire/bin/aspire'],
        }]);
        notifier.dispose();
    });

    test("Don't Show Again persists for the exact CLI path and version", async () => {
        const globalState = createMemento();
        const first = createNotifier(Date.now, globalState);
        first.surface.selection = strings.dontShowAgainLabel;

        await first.notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(first.surface.warnings.length, 1);
        assert.deepStrictEqual(first.surface.commands, []);
        first.notifier.dispose();

        const second = createNotifier(Date.now, globalState);
        await second.notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.deepStrictEqual(second.surface.warnings, []);
        assert.strictEqual(second.versionProvider.versionCalls.length, 1);
        assert.deepStrictEqual(second.versionProvider.recommendationCalls, []);
        second.notifier.dispose();
    });

    test('uses five-minute version and six-hour update refresh intervals', async () => {
        let now = 0;
        const { notifier, versionProvider, surface } = createNotifier(() => now);
        versionProvider.recommendation = {
            status: 'none',
            currentVersion: '13.5.0',
        };

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        now = 5 * 60 * 1_000 - 1;
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        assert.strictEqual(versionProvider.versionCalls.length, 1);
        assert.strictEqual(versionProvider.recommendationCalls.length, 1);

        now++;
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        assert.strictEqual(versionProvider.versionCalls.length, 2);
        assert.strictEqual(versionProvider.recommendationCalls.length, 1);

        now = 6 * 60 * 60 * 1_000;
        versionProvider.recommendation = {
            status: 'available',
            currentVersion: '13.5.0',
            version: '13.7.0',
        };
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(versionProvider.versionCalls.length, 3);
        assert.strictEqual(versionProvider.recommendationCalls.length, 2);
        assert.strictEqual(surface.warnings.length, 1);
        notifier.dispose();
    });

    test('samples version independently and caps unavailable doctor attempts per identity', async () => {
        let now = 0;
        const { notifier, versionProvider } = createNotifier(() => now);
        versionProvider.recommendation = { status: 'unavailable' };

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        for (const minute of [5, 10, 15, 20, 25]) {
            now = minute * 60 * 1_000;
            await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        }

        assert.strictEqual(versionProvider.versionCalls.length, 6);
        assert.strictEqual(versionProvider.recommendationCalls.length, 3);

        now = 30 * 60 * 1_000;
        versionProvider.identity = {
            cliPath: '/cli/aspire',
            version: '13.5.1',
        };
        versionProvider.recommendation = {
            status: 'none',
            currentVersion: '13.5.1',
        };
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(versionProvider.recommendationCalls.length, 4);
        notifier.dispose();
    });

    test('coalesces same-path checks and serializes distinct doctors', async () => {
        const versionProvider = new FakeVersionProvider();
        versionProvider.getCliVersion = async options => {
            versionProvider.versionCalls.push(options);
            return {
                cliPath: options?.cliPath ?? '/cli/aspire',
                version: '13.5.0',
            };
        };
        let activeDoctors = 0;
        let maximumActiveDoctors = 0;
        const releaseDoctors: Array<() => void> = [];
        versionProvider.getCliUpdateRecommendation = async options => {
            versionProvider.recommendationCalls.push(options);
            activeDoctors++;
            maximumActiveDoctors = Math.max(maximumActiveDoctors, activeDoctors);
            return await new Promise(resolve => {
                releaseDoctors.push(() => {
                    activeDoctors--;
                    resolve({
                        status: 'none',
                        currentVersion: '13.5.0',
                    });
                });
            });
        };
        const notifier = new OutdatedCliNotifier(versionProvider, new FakeSurface());

        const shared = Array.from({ length: 10 }, () =>
            notifier.notifyIfOutdated(windowCliPathTarget, '/shared/aspire'));
        const distinct = notifier.notifyIfOutdated(windowCliPathTarget, '/other/aspire');
        await waitFor(() => releaseDoctors.length === 1, 'Expected first serialized doctor.');
        releaseDoctors.shift()?.();
        await waitFor(() => releaseDoctors.length === 1, 'Expected second serialized doctor.');
        releaseDoctors.shift()?.();
        await Promise.all([...shared, distinct]);

        assert.strictEqual(versionProvider.versionCalls.length, 2);
        assert.strictEqual(versionProvider.recommendationCalls.length, 2);
        assert.strictEqual(maximumActiveDoctors, 1);
        notifier.dispose();
    });

    test('isolates same-path update recommendations by resolution target', async () => {
        const folderA: vscode.WorkspaceFolder = {
            uri: vscode.Uri.file('/workspace/a'),
            name: 'a',
            index: 0,
        };
        const folderB: vscode.WorkspaceFolder = {
            uri: vscode.Uri.file('/workspace/b'),
            name: 'b',
            index: 1,
        };
        const targetA = workspaceFolderCliPathTarget(folderA);
        const targetB = workspaceFolderCliPathTarget(folderB);
        const versionProvider = new FakeVersionProvider();
        versionProvider.identity = {
            cliPath: '/shared/aspire',
            version: '13.5.0',
        };
        versionProvider.getCliUpdateRecommendation = async options => {
            versionProvider.recommendationCalls.push(options);
            return options?.target === targetA
                ? { status: 'none', currentVersion: '13.5.0' }
                : { status: 'available', currentVersion: '13.5.0', version: '13.6.0' };
        };
        const surface = new FakeSurface();
        const notifier = new OutdatedCliNotifier(versionProvider, surface);

        await notifier.notifyIfOutdated(targetA, '/shared/aspire');
        await notifier.notifyIfOutdated(targetB, '/shared/aspire');

        assert.deepStrictEqual(
            versionProvider.recommendationCalls.map(call => call?.target),
            [targetA, targetB]);
        assert.strictEqual(surface.warnings.length, 1);
        notifier.dispose();
    });

    test('refreshes the window-scoped recommendation when its Doctor working directory changes', async () => {
        const folderA: vscode.WorkspaceFolder = {
            uri: vscode.Uri.file('/workspace/a'),
            name: 'a',
            index: 0,
        };
        const folderB: vscode.WorkspaceFolder = {
            uri: vscode.Uri.file('/workspace/b'),
            name: 'b',
            index: 0,
        };
        let workspaceFolders: readonly vscode.WorkspaceFolder[] = [];
        const workspaceFoldersStub = sinon.stub(vscode.workspace, 'workspaceFolders').get(() => workspaceFolders);
        const versionProvider = new FakeVersionProvider();
        versionProvider.identity = {
            cliPath: '/shared/aspire',
            version: '13.5.0',
        };
        versionProvider.getCliUpdateRecommendation = async options => {
            versionProvider.recommendationCalls.push(options);
            return options?.workingDirectory === folderB.uri.fsPath
                ? { status: 'available', currentVersion: '13.5.0', version: '13.6.0' }
                : { status: 'none', currentVersion: '13.5.0' };
        };
        const surface = new FakeSurface();
        const notifier = new OutdatedCliNotifier(versionProvider, surface);

        try {
            await notifier.notifyIfOutdated(windowCliPathTarget, '/shared/aspire');
            workspaceFolders = [folderA];
            await notifier.notifyIfOutdated(windowCliPathTarget, '/shared/aspire');
            workspaceFolders = [folderB];
            await notifier.notifyIfOutdated(windowCliPathTarget, '/shared/aspire');

            assert.deepStrictEqual(
                versionProvider.recommendationCalls.map(call => call?.workingDirectory),
                [process.cwd(), folderA.uri.fsPath, folderB.uri.fsPath]);
            assert.strictEqual(surface.warnings.length, 1);
        }
        finally {
            notifier.dispose();
            workspaceFoldersStub.restore();
        }
    });

    test('does not warn when the version probe and Doctor disagree', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        versionProvider.identity = {
            cliPath: '/cli/aspire',
            version: '13.7.0-preview.1',
        };
        versionProvider.recommendation = {
            status: 'available',
            currentVersion: '13.6.0-preview.1',
            version: '13.7.0-preview.2',
        };

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.deepStrictEqual(surface.warnings, []);
        notifier.dispose();
    });

    test('stale or inconclusive warning actions are suppressed', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        let resolveSelection!: (selection: string | undefined) => void;
        surface.selectionPromise = new Promise(resolve => resolveSelection = resolve);

        const notification = notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await waitFor(() => surface.warnings.length === 1, 'Expected warning to open.');
        versionProvider.currentVersion = {
            cliPath: '/cli/aspire',
            version: '13.5.1',
        };
        resolveSelection(strings.updateAspireCliAction);
        await notification;
        assert.deepStrictEqual(surface.commands, []);

        const second = createNotifier();
        second.surface.selection = strings.updateAspireCliAction;
        second.versionProvider.currentVersion = null;
        await second.notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        assert.deepStrictEqual(second.surface.commands, []);
        second.notifier.dispose();
        notifier.dispose();
    });

    test('dispose cancels an active probe and suppresses continuations', async () => {
        const versionProvider = new FakeVersionProvider();
        let resolveVersion!: (version: CliVersionInfo | null) => void;
        versionProvider.getCliVersion = async options => {
            versionProvider.versionCalls.push(options);
            return await new Promise(resolve => resolveVersion = resolve);
        };
        const surface = new FakeSurface();
        const notifier = new OutdatedCliNotifier(versionProvider, surface);
        const notification = notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await waitFor(() => versionProvider.versionCalls.length === 1, 'Expected the version probe to start.');

        notifier.dispose();
        resolveVersion(null);
        await notification;

        assert.strictEqual(versionProvider.versionCalls[0]?.cancellationToken?.isCancellationRequested, true);
        assert.deepStrictEqual(versionProvider.recommendationCalls, []);
        assert.deepStrictEqual(surface.warnings, []);
        assert.deepStrictEqual(surface.commands, []);
    });
});
