import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as strings from '../loc/strings';
import {
    CliIdentityInfo,
    CliUpdateRecommendation,
    CliUpdateRecommendationOptions,
    CliVersionInfo,
    CliVersionStatusOptions,
} from '../utils/configInfoProvider';
import { windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { OutdatedCliNotificationSurface, OutdatedCliNotifier } from '../utils/outdatedCliNotifier';
import { onDidResolveCliForOperation, startCliOperationResolutionHeartbeat } from '../utils/cliOperationResolution';

suite('outdatedCliNotifier', () => {
    class FakeVersionProvider {
        identity: CliIdentityInfo | null = {
            cliPath: '/cli/aspire',
            version: '13.5.0',
        };
        identityPromise: Promise<CliIdentityInfo | null> | undefined;
        currentVersion: CliVersionInfo | null = {
            cliPath: '/cli/aspire',
            version: '13.5.0',
        };
        recommendation: CliUpdateRecommendation = {
            status: 'available',
            currentVersion: '13.5.0',
            version: '13.6.0',
        };
        recommendationPromise: Promise<CliUpdateRecommendation> | undefined;
        readonly identityCalls: Array<CliVersionStatusOptions | undefined> = [];
        readonly versionCalls: Array<CliVersionStatusOptions | undefined> = [];
        readonly recommendationCalls: Array<CliUpdateRecommendationOptions | undefined> = [];

        async getCliIdentity(options?: CliVersionStatusOptions): Promise<CliIdentityInfo | null> {
            this.identityCalls.push(options);
            return await (this.identityPromise ?? this.identity);
        }

        async getCliVersion(options?: CliVersionStatusOptions): Promise<CliVersionInfo | null> {
            this.versionCalls.push(options);
            return this.currentVersion;
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

    function createMemento(values = new Map<string, unknown>()): vscode.Memento {
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
            version: '13.5.0',
        };
        versionProvider.currentVersion = versionProvider.identity;
        surface.selection = strings.updateAspireCliAction;

        await notifier.notifyIfOutdated(target, '/workspace/a/.aspire/bin/aspire');
        await notifier.notifyIfOutdated(target, '/workspace/a/.aspire/bin/aspire');

        assert.strictEqual(surface.warnings.length, 1);
        assert.strictEqual(
            surface.warnings[0].message,
            'Aspire CLI 13.5.0 at /workspace/a/.aspire/bin/aspire has a newer version available for its current channel: 13.6.0.');
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
        assert.strictEqual(second.versionProvider.identityCalls.length, 1);
        assert.deepStrictEqual(second.versionProvider.recommendationCalls, []);
        second.notifier.dispose();

        const third = createNotifier(Date.now, globalState);
        third.versionProvider.identity = {
            cliPath: '/cli/aspire',
            version: '13.5.1',
        };
        third.versionProvider.recommendation = {
            status: 'available',
            currentVersion: '13.5.1',
            version: '13.6.0',
        };
        await third.notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(third.surface.warnings.length, 1);
        third.notifier.dispose();
    });

    test("Don't Show Again evicts the oldest persisted suppression", async () => {
        let now = 0;
        const values = new Map<string, unknown>();
        const globalState = createMemento(values);
        const { notifier, versionProvider, surface } = createNotifier(() => now, globalState);
        surface.selection = strings.dontShowAgainLabel;

        for (let index = 0; index <= 100; index++) {
            const cliPath = `/cli/${index}/aspire`;
            versionProvider.identity = {
                cliPath,
                version: '13.5.0',
            };
            await notifier.notifyIfOutdated(windowCliPathTarget, cliPath);
            now++;
        }

        assert.strictEqual(values.size, 100);
        assert.strictEqual([...values.values()].includes(0), false);
        assert.strictEqual([...values.values()].includes(100), true);
        notifier.dispose();
    });

    test("Don't Show Again persists concurrent suppressions independently across windows", async () => {
        const values = new Map<string, unknown>();
        const pendingUpdates: Array<{ key: string; value: unknown; complete: () => void }> = [];
        const globalState: vscode.Memento = {
            keys: () => [...values.keys()],
            get: <T>(key: string, defaultValue?: T): T | undefined =>
                values.has(key) ? values.get(key) as T : defaultValue,
            update: (key: string, value: unknown): Thenable<void> =>
                new Promise(resolve => pendingUpdates.push({ key, value, complete: resolve })),
        };
        const first = createNotifier(Date.now, globalState);
        const second = createNotifier(Date.now, globalState);
        first.surface.selection = strings.dontShowAgainLabel;
        second.surface.selection = strings.dontShowAgainLabel;
        second.versionProvider.identity = {
            cliPath: '/other/aspire',
            version: '13.5.0',
        };

        const suppressions = [
            first.notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire'),
            second.notifier.notifyIfOutdated(windowCliPathTarget, '/other/aspire'),
        ];
        await waitFor(() => pendingUpdates.length === 2, 'Expected independent suppression writes.');
        for (const update of pendingUpdates.splice(0)) {
            if (update.value === undefined) {
                values.delete(update.key);
            } else {
                values.set(update.key, update.value);
            }
            update.complete();
        }
        await Promise.all(suppressions);
        first.notifier.dispose();
        second.notifier.dispose();

        const third = createNotifier(Date.now, globalState);
        await third.notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        third.versionProvider.identity = {
            cliPath: '/other/aspire',
            version: '13.5.0',
        };
        await third.notifier.notifyIfOutdated(windowCliPathTarget, '/other/aspire');

        assert.deepStrictEqual(third.surface.warnings, []);
        assert.deepStrictEqual(third.versionProvider.recommendationCalls, []);
        third.notifier.dispose();
    });

    test("Don't Show Again on a stale warning does not suppress the replacement version", async () => {
        let now = 0;
        const globalState = createMemento();
        const { notifier, versionProvider, surface } = createNotifier(() => now, globalState);
        let resolveOldSelection!: (selection: string | undefined) => void;
        surface.selectionPromise = new Promise(resolve => resolveOldSelection = resolve);

        const oldWarning = notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await waitFor(() => surface.warnings.length === 1, 'Expected the old CLI warning to open.');

        now = 5 * 60 * 1_000;
        versionProvider.identity = {
            cliPath: '/cli/aspire',
            version: '13.5.1',
        };
        versionProvider.recommendation = {
            status: 'available',
            currentVersion: '13.5.1',
            version: '13.6.0',
        };
        surface.selectionPromise = undefined;
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        resolveOldSelection(strings.dontShowAgainLabel);
        await oldWarning;

        now += 6 * 60 * 60 * 1_000;
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(versionProvider.recommendationCalls.length, 3);
        notifier.dispose();
    });

    test('warns when stable 13.4.0 is behind stable 15.3.2', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        versionProvider.identity = {
            cliPath: '/cli/aspire',
            version: '13.4.0',
        };
        versionProvider.recommendation = {
            status: 'available',
            currentVersion: '13.4.0',
            version: '15.3.2',
        };

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(
            surface.warnings[0].message,
            'Aspire CLI 13.4.0 at /cli/aspire has a newer version available for its current channel: 15.3.2.');
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
        assert.strictEqual(versionProvider.identityCalls.length, 1);
        assert.strictEqual(versionProvider.recommendationCalls.length, 1);

        now++;
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        assert.strictEqual(versionProvider.identityCalls.length, 2);
        assert.strictEqual(versionProvider.recommendationCalls.length, 1);

        now = 6 * 60 * 60 * 1_000;
        versionProvider.recommendation = {
            status: 'available',
            currentVersion: '13.5.0',
            version: '13.7.0',
        };
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(versionProvider.identityCalls.length, 3);
        assert.strictEqual(versionProvider.recommendationCalls.length, 2);
        assert.strictEqual(surface.warnings.length, 1);
        notifier.dispose();
    });

    test('active-operation heartbeat rechecks five minutes from check start', async () => {
        const clock = sinon.useFakeTimers({ now: 0 });
        const { notifier, versionProvider } = createNotifier(() => Date.now());
        versionProvider.recommendation = {
            status: 'none',
            currentVersion: '13.5.0',
        };
        let resolveIdentity!: (identity: CliIdentityInfo | null) => void;
        versionProvider.identityPromise = new Promise(resolve => resolveIdentity = resolve);
        const heartbeatChecks: Promise<void>[] = [];
        const subscription = onDidResolveCliForOperation(resolution => {
            heartbeatChecks.push(notifier.notifyIfOutdated(resolution.target, resolution.cliPath));
        });
        const heartbeat = startCliOperationResolutionHeartbeat(
            windowCliPathTarget,
            '/cli/aspire',
            () => true);

        try {
            const initialCheck = notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
            await clock.tickAsync(1_000);
            resolveIdentity(versionProvider.identity);
            await initialCheck;
            versionProvider.identityPromise = undefined;

            await clock.tickAsync(5 * 60 * 1_000 - 1_000);
            await Promise.all(heartbeatChecks);

            assert.strictEqual(versionProvider.identityCalls.length, 2);
        }
        finally {
            heartbeat.dispose();
            subscription.dispose();
            notifier.dispose();
            clock.restore();
        }
    });

    test('active-operation heartbeat stops itself when the operation becomes inactive', async () => {
        const clock = sinon.useFakeTimers();
        let active = true;
        const resolutions: string[] = [];
        const subscription = onDidResolveCliForOperation(resolution => resolutions.push(resolution.cliPath));
        const heartbeat = startCliOperationResolutionHeartbeat(
            windowCliPathTarget,
            '/cli/aspire',
            () => active);

        try {
            clock.tick(5 * 60 * 1_000);
            assert.deepStrictEqual(resolutions, ['/cli/aspire']);

            active = false;
            clock.tick(5 * 60 * 1_000);
            assert.deepStrictEqual(resolutions, ['/cli/aspire']);
            assert.strictEqual(clock.countTimers(), 0);
        }
        finally {
            heartbeat.dispose();
            subscription.dispose();
            clock.restore();
        }
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

        assert.strictEqual(versionProvider.identityCalls.length, 6);
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

    test('changed version or channel override resets update state', async () => {
        let now = 0;
        const { notifier, versionProvider } = createNotifier(() => now);
        versionProvider.recommendation = {
            status: 'none',
            currentVersion: '13.5.0',
        };
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        now = 5 * 60 * 1_000;
        versionProvider.identity = {
            cliPath: '/cli/aspire',
            version: '13.5.1',
        };
        versionProvider.recommendation = {
            status: 'none',
            currentVersion: '13.5.1',
        };
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        now = 10 * 60 * 1_000;
        versionProvider.identity = {
            ...versionProvider.identity,
            identityChannelOverride: 'daily',
        };
        versionProvider.recommendation = {
            status: 'none',
            currentVersion: '13.5.1',
        };
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(versionProvider.recommendationCalls.length, 3);
        assert.strictEqual(versionProvider.recommendationCalls[2]?.identityChannelOverride, 'daily');
        notifier.dispose();
    });

    test('ineligible CLI never reruns doctor for the same identity', async () => {
        let now = 0;
        const { notifier, versionProvider, surface } = createNotifier(() => now);
        versionProvider.recommendation = {
            status: 'ineligible',
            currentVersion: '13.6.0-dev',
        };
        versionProvider.identity = {
            cliPath: '/cli/aspire',
            version: '13.6.0-dev',
        };

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        now = 5 * 60 * 1_000;
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        now = 24 * 60 * 60 * 1_000;
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(versionProvider.identityCalls.length, 3);
        assert.strictEqual(versionProvider.recommendationCalls.length, 1);
        assert.deepStrictEqual(surface.warnings, []);
        notifier.dispose();
    });

    test('coalesces same-path checks and serializes distinct doctors', async () => {
        const versionProvider = new FakeVersionProvider();
        versionProvider.getCliIdentity = async options => {
            versionProvider.identityCalls.push(options);
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

        assert.strictEqual(versionProvider.identityCalls.length, 2);
        assert.strictEqual(versionProvider.recommendationCalls.length, 2);
        assert.strictEqual(maximumActiveDoctors, 1);
        notifier.dispose();
    });

    test('does not warn when physical and effective versions disagree', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        versionProvider.identity = {
            cliPath: '/cli/aspire',
            version: '13.7.0-preview.1',
            identityChannelOverride: 'daily',
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

    test('dispose cancels queued and active probes and suppresses warning actions', async () => {
        const { notifier, versionProvider, surface } = createNotifier();
        let resolveIdentity!: (identity: CliIdentityInfo | null) => void;
        versionProvider.identityPromise = new Promise(resolve => resolveIdentity = resolve);

        const active = notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await waitFor(() => versionProvider.identityCalls.length === 1, 'Expected identity probe to start.');
        const cancellationToken = versionProvider.identityCalls[0]?.cancellationToken;
        notifier.dispose();
        resolveIdentity(versionProvider.identity);
        await active;

        assert.strictEqual(cancellationToken?.isCancellationRequested, true);
        assert.deepStrictEqual(surface.warnings, []);
        assert.deepStrictEqual(surface.commands, []);
    });

    test('dispose prevents queued version work from starting', async () => {
        const versionProvider = new FakeVersionProvider();
        const resolveIdentities: Array<(identity: CliIdentityInfo | null) => void> = [];
        versionProvider.getCliIdentity = async options => {
            versionProvider.identityCalls.push(options);
            return await new Promise(resolve => resolveIdentities.push(resolve));
        };
        const notifier = new OutdatedCliNotifier(versionProvider, new FakeSurface());
        const notifications = Array.from({ length: 5 }, (_, index) =>
            notifier.notifyIfOutdated(windowCliPathTarget, `/cli/${index}/aspire`));
        await waitFor(() => versionProvider.identityCalls.length === 4, 'Expected the version pool to fill.');

        notifier.dispose();
        resolveIdentities.forEach(resolve => resolve(null));
        await Promise.all(notifications);

        assert.strictEqual(versionProvider.identityCalls.length, 4);
        assert.deepStrictEqual(versionProvider.recommendationCalls, []);
    });
});
