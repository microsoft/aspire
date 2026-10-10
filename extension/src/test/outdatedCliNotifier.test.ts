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
import {
    OutdatedCliNotificationClaim,
    OutdatedCliSuppressionStore,
} from '../utils/outdatedCliSuppressionStore';

suite('outdatedCliNotifier', () => {
    const defaultExecutableIdentity = 'identity-1';

    class FakeVersionProvider {
        identity: CliVersionInfo | null = {
            cliPath: '/cli/aspire',
            version: '13.5.0',
            executableIdentity: defaultExecutableIdentity,
        };
        identityPromise: Promise<CliVersionInfo | null> | undefined;
        currentVersion: CliVersionInfo | null | undefined;
        recommendation: CliUpdateRecommendation = {
            status: 'available',
            currentVersion: '13.5.0',
            version: '13.6.0',
        };
        recommendationPromise: Promise<CliUpdateRecommendation> | undefined;
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
            return await (this.recommendationPromise ?? this.recommendation);
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

    function createNotifier(now: () => number = Date.now, suppressionStore?: OutdatedCliSuppressionStore): {
        notifier: OutdatedCliNotifier;
        versionProvider: FakeVersionProvider;
        surface: FakeSurface;
    } {
        const versionProvider = new FakeVersionProvider();
        const surface = new FakeSurface();
        return {
            notifier: new OutdatedCliNotifier(versionProvider, surface, now, suppressionStore),
            versionProvider,
            surface,
        };
    }

    function createSuppressionStore(values = new Set<string>()): OutdatedCliSuppressionStore {
        return {
            readAll: async () => [...values],
            add: async notificationKey => void values.add(notificationKey),
            tryClaimNotification: async notificationKey => values.has(notificationKey)
                ? undefined
                : createNotificationClaim(),
        };
    }

    function createNotificationClaim(): OutdatedCliNotificationClaim {
        return {
            isValid: () => true,
            release: async () => undefined,
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
            executableIdentity: defaultExecutableIdentity,
        };
        versionProvider.recommendation = {
            status: 'available',
            currentVersion: '13.4.0',
            version: '13.5.2',
        };
        versionProvider.currentVersion = versionProvider.identity;
        surface.selection = strings.updateAspireCliAction;

        await notifier.notifyIfOutdated(target, '/workspace/a/.aspire/bin/aspire');
        await notifier.notifyIfOutdated(target, '/workspace/a/.aspire/bin/aspire');

        assert.strictEqual(surface.warnings.length, 1);
        assert.strictEqual(
            surface.warnings[0].message,
            'Aspire CLI 13.4.0 at /workspace/a/.aspire/bin/aspire has a newer version available for its current channel: 13.5.2.');
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
        const values = new Set<string>();
        const first = createNotifier(Date.now, createSuppressionStore(values));
        const second = createNotifier(Date.now, createSuppressionStore(values));
        first.surface.selection = strings.dontShowAgainLabel;
        let completeSecondRecommendation!: (recommendation: CliUpdateRecommendation) => void;
        second.versionProvider.recommendationPromise = new Promise(resolve => completeSecondRecommendation = resolve);

        const secondNotification = second.notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await waitFor(
            () => second.versionProvider.recommendationCalls.length === 1,
            'Expected the second window update probe to start.');

        await first.notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        completeSecondRecommendation({
            status: 'available',
            currentVersion: '13.5.0',
            version: '13.6.0',
        });
        await secondNotification;

        assert.strictEqual(first.surface.warnings.length, 1);
        assert.deepStrictEqual(first.surface.commands, []);
        first.notifier.dispose();

        assert.deepStrictEqual(second.surface.warnings, []);
        assert.strictEqual(second.versionProvider.versionCalls.length, 1);
        assert.strictEqual(second.versionProvider.recommendationCalls.length, 1);
        second.notifier.dispose();
    });

    test('reserves the notification before awaiting a cross-window claim', async () => {
        const target = workspaceFolderCliPathTarget({
            uri: vscode.Uri.file('/workspace/a'),
            name: 'a',
            index: 0,
        });
        let completeClaim!: (claim: OutdatedCliNotificationClaim) => void;
        let claimCalls = 0;
        const suppressionStore: OutdatedCliSuppressionStore = {
            readAll: async () => [],
            add: async () => undefined,
            tryClaimNotification: async () => {
                claimCalls++;
                return claimCalls === 1
                    ? await new Promise(resolve => completeClaim = resolve)
                    : createNotificationClaim();
            },
        };
        const { notifier, surface } = createNotifier(Date.now, suppressionStore);

        const first = notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await waitFor(() => claimCalls === 1, 'Expected the first notification claim.');
        const second = notifier.notifyIfOutdated(target, '/cli/aspire');
        await second;
        completeClaim(createNotificationClaim());
        await first;

        assert.strictEqual(claimCalls, 1);
        assert.strictEqual(surface.warnings.length, 1);
        notifier.dispose();
    });

    test('holds the cross-window claim only through warning dispatch', async () => {
        let claimReleased = false;
        let completeSelection!: (selection: string | undefined) => void;
        const suppressionStore: OutdatedCliSuppressionStore = {
            readAll: async () => [],
            add: async () => undefined,
            tryClaimNotification: async () => ({
                isValid: () => true,
                release: async () => {
                    claimReleased = true;
                },
            }),
        };
        const { notifier, surface } = createNotifier(Date.now, suppressionStore);
        surface.selectionPromise = new Promise(resolve => completeSelection = resolve);
        const showWarning = surface.showWarning.bind(surface);
        surface.showWarning = (message, ...actions) => {
            assert.strictEqual(claimReleased, false);
            return showWarning(message, ...actions);
        };

        const notification = notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await waitFor(() => claimReleased, 'Expected the claim to be released after warning dispatch.');
        assert.strictEqual(surface.warnings.length, 1);

        completeSelection(undefined);
        await notification;
        notifier.dispose();
    });

    test('does not warn from an expired cross-window claim', async () => {
        let released = false;
        const suppressionStore: OutdatedCliSuppressionStore = {
            readAll: async () => [],
            add: async () => undefined,
            tryClaimNotification: async () => ({
                isValid: () => false,
                release: async () => {
                    released = true;
                },
            }),
        };
        const { notifier, surface, versionProvider } = createNotifier(Date.now, suppressionStore);

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(released, true);
        assert.deepStrictEqual(surface.warnings, []);
        assert.strictEqual(versionProvider.versionCalls.length, 2);
        assert.strictEqual(versionProvider.recommendationCalls.length, 2);
        notifier.dispose();
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
            executableIdentity: 'identity-2',
        };
        versionProvider.recommendation = {
            status: 'none',
            currentVersion: '13.5.1',
        };
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(versionProvider.recommendationCalls.length, 4);
        notifier.dispose();
    });

    test('refreshes the recommendation when the executable changes without a version change', async () => {
        let now = 0;
        const { notifier, versionProvider, surface } = createNotifier(() => now);
        versionProvider.recommendation = {
            status: 'ineligible',
            currentVersion: '13.5.0',
        };

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        now = 5 * 60 * 1_000;
        versionProvider.identity = {
            cliPath: '/cli/aspire',
            version: '13.5.0',
            executableIdentity: 'identity-2',
        };
        versionProvider.recommendation = {
            status: 'available',
            currentVersion: '13.5.0',
            version: '13.6.0',
        };
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(versionProvider.recommendationCalls.length, 2);
        assert.strictEqual(surface.warnings.length, 1);
        notifier.dispose();
    });

    test('coalesces same-path checks and serializes distinct doctors', async () => {
        const versionProvider = new FakeVersionProvider();
        let activeVersions = 0;
        let maximumActiveVersions = 0;
        const releaseVersions: Array<() => void> = [];
        versionProvider.getCliVersion = async options => {
            versionProvider.versionCalls.push(options);
            activeVersions++;
            maximumActiveVersions = Math.max(maximumActiveVersions, activeVersions);
            return await new Promise(resolve => releaseVersions.push(() => {
                activeVersions--;
                resolve({
                    cliPath: options?.cliPath ?? '/cli/aspire',
                    version: '13.5.0',
                    executableIdentity: options?.cliPath ?? defaultExecutableIdentity,
                });
            }));
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
        await waitFor(() => releaseVersions.length === 1, 'Expected first serialized version probe.');
        releaseVersions.shift()?.();
        await waitFor(() => releaseVersions.length === 1, 'Expected second serialized version probe.');
        releaseVersions.shift()?.();
        await waitFor(() => releaseDoctors.length === 1, 'Expected first serialized doctor.');
        releaseDoctors.shift()?.();
        await waitFor(() => releaseDoctors.length === 1, 'Expected second serialized doctor.');
        releaseDoctors.shift()?.();
        await Promise.all([...shared, distinct]);

        assert.strictEqual(versionProvider.versionCalls.length, 2);
        assert.strictEqual(maximumActiveVersions, 1);
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
            executableIdentity: defaultExecutableIdentity,
        };
        versionProvider.getCliUpdateRecommendation = async options => {
            versionProvider.recommendationCalls.push(options);
            return options?.target === targetA
                ? { status: 'none', currentVersion: '13.5.0' }
                : { status: 'available', currentVersion: '13.5.0', version: '13.6.0' };
        };
        const surface = new FakeSurface();
        const notifier = new OutdatedCliNotifier(versionProvider, surface);

        await Promise.all([
            notifier.notifyIfOutdated(targetA, '/shared/aspire'),
            notifier.notifyIfOutdated(targetB, '/shared/aspire'),
        ]);

        assert.strictEqual(versionProvider.versionCalls.length, 1);
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
            executableIdentity: defaultExecutableIdentity,
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
            executableIdentity: defaultExecutableIdentity,
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
        versionProvider.versionCalls.length = 0;
        let releaseOlderProbe!: () => void;
        versionProvider.getCliVersion = async options => {
            versionProvider.versionCalls.push(options);
            if (versionProvider.versionCalls.length === 1) {
                return await new Promise(resolve => {
                    releaseOlderProbe = () => resolve({
                        cliPath: '/cli/aspire',
                        version: '13.5.0',
                        executableIdentity: defaultExecutableIdentity,
                    });
                });
            }
            return {
                cliPath: '/cli/aspire',
                version: '13.5.0',
                executableIdentity: 'identity-2',
            };
        };
        const backgroundCheck = notifier.notifyIfOutdated(
            workspaceFolderCliPathTarget({
                uri: vscode.Uri.file('/other'),
                name: 'other',
                index: 0,
            }),
            '/cli/aspire');
        await waitFor(() => versionProvider.versionCalls.length === 1, 'Expected older probe to start.');
        resolveSelection(strings.updateAspireCliAction);
        await new Promise(resolve => setImmediate(resolve));
        releaseOlderProbe();
        await Promise.all([notification, backgroundCheck]);

        assert.strictEqual(versionProvider.versionCalls.length, 2);
        assert.deepStrictEqual(surface.commands, []);

        surface.selectionPromise = undefined;
        surface.selection = undefined;
        await notifier.notifyIfOutdated(
            workspaceFolderCliPathTarget({
                uri: vscode.Uri.file('/replacement'),
                name: 'replacement',
                index: 0,
            }),
            '/cli/aspire');
        assert.strictEqual(surface.warnings.length, 2);
        assert.strictEqual(versionProvider.versionCalls.length, 3);
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
