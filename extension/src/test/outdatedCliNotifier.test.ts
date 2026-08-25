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

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        versionProvider.result = null;
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

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

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(surface.warnings.length, 1);
        assert.strictEqual(
            surface.warnings[0].message,
            "Aspire CLI 13.4.9 at /cli/aspire is older than 13.5.0. Update the CLI and the AppHost's Aspire packages to 13.5.0 or later to avoid a known startup failure in VS Code.");
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
                `Aspire CLI ${version.version} at ${version.cliPath} is older than 13.5.0. Update the CLI and the AppHost's Aspire packages to 13.5.0 or later to avoid a known startup failure in VS Code.`));
        assert.notStrictEqual(surface.warnings[0].message, surface.warnings[1].message);
        notifier.dispose();
    });

    test('coalesces shared multi-root paths and bounds distinct version probes', async () => {
        let activeProbeCount = 0;
        let maximumActiveProbeCount = 0;
        const probedPaths: string[] = [];
        const releaseProbes: Array<() => void> = [];
        const versionProvider = {
            getCliVersionStatus: async (
                _minimumVersion: string,
                options?: CliVersionStatusOptions,
            ): Promise<CliVersionStatus | null> => {
                const cliPath = options?.cliPath;
                assert.ok(cliPath);
                probedPaths.push(cliPath);
                activeProbeCount++;
                maximumActiveProbeCount = Math.max(maximumActiveProbeCount, activeProbeCount);
                return await new Promise(resolve => {
                    releaseProbes.push(() => {
                        activeProbeCount--;
                        resolve({
                            cliPath,
                            version: '13.5.0',
                            status: 'supported',
                        });
                    });
                });
            },
        };
        const surface = new FakeSurface();
        const notifier = new OutdatedCliNotifier(versionProvider, surface);
        const requests = Array.from({ length: 40 }, (_, index) => {
            const target = workspaceFolderCliPathTarget({
                uri: vscode.Uri.file(`/workspace/${index}`),
                name: `folder-${index}`,
                index,
            });
            const cliPath = index < 20 ? '/shared/aspire' : `/cli/${index}/aspire`;
            return notifier.notifyIfOutdated(target, cliPath);
        });
        const expectedProbeCount = 21;
        let releasedProbeCount = 0;

        while (releasedProbeCount < expectedProbeCount) {
            await new Promise(resolve => setImmediate(resolve));
            const batch = releaseProbes.splice(0);
            assert.ok(batch.length > 0, 'Expected a bounded batch of version probes to be ready.');
            releasedProbeCount += batch.length;
            batch.forEach(release => release());
        }
        await Promise.all(requests);

        assert.strictEqual(probedPaths.filter(cliPath => cliPath === '/shared/aspire').length, 1);
        assert.strictEqual(probedPaths.length, expectedProbeCount);
        assert.strictEqual(maximumActiveProbeCount, 4);
        assert.deepStrictEqual(surface.warnings, []);
        notifier.dispose();
    });

    test('transfers a released probe slot without exposing it to a new arrival', async () => {
        const notifier = new OutdatedCliNotifier(new FakeVersionProvider(), new FakeSurface());
        const slots = notifier as unknown as {
            _activeProbeCount: number;
            _acquireProbeSlot(): Promise<boolean>;
            _releaseProbeSlot(): void;
        };

        assert.deepStrictEqual(
            await Promise.all(Array.from({ length: 4 }, () => slots._acquireProbeSlot())),
            [true, true, true, true]);
        const firstWaiter = slots._acquireProbeSlot();

        slots._releaseProbeSlot();
        const newArrival = slots._acquireProbeSlot();
        let newArrivalResolved = false;
        void newArrival.then(() => newArrivalResolved = true);
        assert.strictEqual(await firstWaiter, true);
        await Promise.resolve();

        assert.strictEqual(slots._activeProbeCount, 4);
        assert.strictEqual(newArrivalResolved, false);

        slots._releaseProbeSlot();
        assert.strictEqual(await newArrival, true);
        for (let i = 0; i < 4; i++) {
            slots._releaseProbeSlot();
        }
        assert.strictEqual(slots._activeProbeCount, 0);
        notifier.dispose();
    });

    test('reprobes the same active CLI path and observes an in-place replacement', async () => {
        const results: CliVersionStatus[] = [{
            cliPath: '/cli/aspire',
            version: '13.5.0',
            status: 'supported',
        }, {
            cliPath: '/cli/aspire',
            version: '13.4.9',
            status: 'unsupported',
        }];
        let probeIndex = 0;
        const versionProvider = {
            getCliVersionStatus: async (): Promise<CliVersionStatus> => results[probeIndex++],
        };
        const surface = new FakeSurface();
        const notifier = new OutdatedCliNotifier(versionProvider, surface);

        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');
        await notifier.notifyIfOutdated(windowCliPathTarget, '/cli/aspire');

        assert.strictEqual(probeIndex, 2);
        assert.strictEqual(surface.warnings.length, 1);
        assert.ok(surface.warnings[0].message.includes('Aspire CLI 13.4.9 at /cli/aspire is older than 13.5.0'));
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

        await notifier.notifyIfOutdated(target, '/workspace/a/.aspire/bin/aspire');

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
        await new Promise(resolve => setImmediate(resolve));
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
