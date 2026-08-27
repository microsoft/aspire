import * as assert from 'assert';
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

    function createNotifier(now: () => number = Date.now): {
        notifier: OutdatedCliNotifier;
        versionProvider: FakeVersionProvider;
        surface: FakeSurface;
    } {
        const versionProvider = new FakeVersionProvider();
        const surface = new FakeSurface();
        return {
            notifier: new OutdatedCliNotifier(versionProvider, surface, now),
            versionProvider,
            surface,
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
        assert.deepStrictEqual(surface.warnings[0].actions, [strings.updateAspireCliAction]);
        assert.deepStrictEqual(surface.commands, [{
            command: 'aspire-vscode.updateSelf',
            args: [target, '/workspace/a/.aspire/bin/aspire'],
        }]);
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

    test('samples version independently while unavailable doctor checks back off', async () => {
        let now = 0;
        const { notifier, versionProvider } = createNotifier(() => now);
        versionProvider.recommendation = { status: 'unavailable' };

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        for (const minute of [5, 10, 15, 20]) {
            now = minute * 60 * 1_000;
            await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        }

        assert.strictEqual(versionProvider.identityCalls.length, 5);
        // Retries at 0, 5, 10 and 15 minutes establish 1/2/4/8-minute backoff. The 20-minute
        // version sample remains independent but does not run doctor before minute 23.
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

    test('coalesces same-path checks and does not queue behind the active doctor', async () => {
        let now = 0;
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
        const notifier = new OutdatedCliNotifier(versionProvider, new FakeSurface(), () => now);

        const shared = Array.from({ length: 10 }, () =>
            notifier.notifyIfOutdated(windowCliPathTarget, '/shared/aspire'));
        const distinct = notifier.notifyIfOutdated(windowCliPathTarget, '/other/aspire');
        await waitFor(() => releaseDoctors.length === 1, 'Expected first serialized doctor.');
        releaseDoctors.shift()?.();
        await Promise.all([...shared, distinct]);

        assert.strictEqual(versionProvider.identityCalls.length, 2);
        assert.strictEqual(versionProvider.recommendationCalls.length, 1);
        assert.strictEqual(maximumActiveDoctors, 1);

        const checkedPath = versionProvider.recommendationCalls[0]?.cliPath;
        const skippedPath = checkedPath === '/shared/aspire' ? '/other/aspire' : '/shared/aspire';
        now = 5 * 60 * 1_000;
        const retry = notifier.notifyIfOutdated(windowCliPathTarget, skippedPath);
        await waitFor(() => releaseDoctors.length === 1, 'Expected skipped path to retry doctor.');
        releaseDoctors.shift()?.();
        await retry;

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
