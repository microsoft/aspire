import * as assert from 'assert';
import * as crypto from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireExtendedDebugConfiguration } from '../dcp/types';
import { appHostLifecycleBusy } from '../loc/strings';
import { AppHostLaunchService, AppHostLifecycleLockTimeoutError, appHostLifecycleLockMaxHoldMs, appHostLifecycleLockWaitTimeoutMs, externalLaunchReservationTimeoutMs } from '../services/AppHostLaunchService';
import { getAppHostIdentityKey } from '../utils/appHostIdentity';
import * as cliPathModule from '../utils/cliPath';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';

interface RecordedEvent {
    name: string;
    properties?: Record<string, string>;
    measurements?: Record<string, number>;
}

class FakeTelemetryReporter {
    public events: RecordedEvent[] = [];

    public telemetryLevel: 'all' | 'error' | 'crash' | 'off' = 'all';

    sendTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        // Extension code now bypasses this path; recording here would only
        // see a regression to the prefixed channel. Kept as a typed no-op
        // so the fake still satisfies the TelemetryReporter shape.
    }

    sendTelemetryErrorEvent(): void { /* not used here */ }

    sendDangerousTelemetryEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }

    sendDangerousTelemetryErrorEvent(name: string, properties?: Record<string, string>, measurements?: Record<string, number>): void {
        this.events.push({ name, properties, measurements });
    }
    sendRawTelemetryEvent(): void { /* not used here */ }
    dispose(): Promise<void> { return Promise.resolve(); }
}

/**
 * Creates a real directory holding the given entries.
 *
 * AppHost identity is decided from the containing directory's contents - a project file
 * only aliases a source file when the directory forces exactly one pairing - so the tests
 * that exercise that relationship cannot use fabricated paths.
 */
function createAppHostDirectory(...entries: readonly string[]): string {
    const fixtureRoot = path.resolve(__dirname, '..', '..', '.test-workspace', 'launch-service');
    const directory = path.join(fixtureRoot, `apphost-${crypto.randomBytes(6).toString('hex')}`);
    fs.mkdirSync(directory, { recursive: true });
    for (const entry of entries) {
        fs.writeFileSync(path.join(directory, entry), '');
    }

    return directory;
}

suite('AppHostLaunchService', () => {
    let service: AppHostLaunchService;
    let startDebuggingStub: sinon.SinonStub;
    let resolveCliPathStub: sinon.SinonStub;
    let onDidTerminateDebugSessionStub: sinon.SinonStub;
    let onDidTerminateDebugSessionCallback: ((session: vscode.DebugSession) => void) | undefined;

    setup(() => {
        onDidTerminateDebugSessionStub = sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            onDidTerminateDebugSessionCallback = callback;
            return new vscode.Disposable(() => { });
        });
        service = new AppHostLaunchService();
        startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        resolveCliPathStub = sinon.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: 'aspire', available: true, source: 'path' });
    });

    teardown(() => {
        service.dispose();
        startDebuggingStub.restore();
        resolveCliPathStub.restore();
        onDidTerminateDebugSessionStub.restore();
        onDidTerminateDebugSessionCallback = undefined;
    });

    test('isLaunching returns false before launch', () => {
        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('launch marks path as launching', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', true);

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), true);
    });

    test('launch fires onDidChangeLaunchingState event', async () => {
        let fired = false;
        service.onDidChangeLaunchingState(() => { fired = true; });

        await service.launch('/repo/AppHost.csproj', 'run', true);

        assert.strictEqual(fired, true);
    });

    test('launch starts a debug session with correct configuration', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', false);

        assert.ok(startDebuggingStub.calledOnce);
        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.type, 'aspire');
        assert.strictEqual(config.request, 'launch');
        assert.strictEqual(config.program, '/repo/AppHost.csproj');
        assert.strictEqual(config.command, 'run');
        assert.strictEqual(config.noDebug, false);
        assert.strictEqual(config.step, undefined);
        assert.strictEqual(config.skipCliAvailabilityCheck, true);
    });

    test('launch includes step when doStep is provided', async () => {
        await service.launch('/repo/AppHost.csproj', 'do', true, 'deploy');

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.command, 'do');
        assert.strictEqual(config.step, 'deploy');
    });

    test('launch owns CLI availability probe', async () => {
        resolveCliPathStub.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
        const showErrorMessageStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);

        try {
            await assert.rejects(service.launch('/repo/AppHost.csproj', 'deploy', false), vscode.CancellationError);

            assert.strictEqual(resolveCliPathStub.calledOnce, true);
            assert.strictEqual(startDebuggingStub.called, false);
        }
        finally {
            showErrorMessageStub.restore();
        }
    });

    test('clearLaunching removes the path from launching state', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', true);
        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), true);

        service.clearLaunching('/repo/AppHost.csproj');

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('clearLaunching fires onDidChangeLaunchingState event', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', true);

        let fired = false;
        service.onDidChangeLaunchingState(() => { fired = true; });
        service.clearLaunching('/repo/AppHost.csproj');

        assert.strictEqual(fired, true);
    });

    test('clearLaunching does not fire event when path was not launching', () => {
        let fired = false;
        service.onDidChangeLaunchingState(() => { fired = true; });

        service.clearLaunching('/repo/nonexistent.csproj');

        assert.strictEqual(fired, false);
    });

    test('clearMatchingLaunching matches project paths to AppHost source files in the same directory', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        await service.launch(path.join(directory, 'AppHost.csproj'), 'run', true);

        service.clearMatchingLaunching(path.join(directory, 'Program.cs'));

        assert.strictEqual(service.isLaunching(path.join(directory, 'AppHost.csproj')), false);
    });

    test('isLaunching matches project paths to AppHost source files in the same directory', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        await service.launch(path.join(directory, 'Program.cs'), 'run', true);

        assert.strictEqual(service.isLaunching(path.join(directory, 'AppHost.csproj')), true);
    });

    test('clearMatchingLaunching does not clear unrelated paths in the same directory', async () => {
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj', 'Program.cs');
        await service.launch(path.join(directory, 'First.csproj'), 'run', true);
        await service.launch(path.join(directory, 'Second.csproj'), 'run', true);

        service.clearMatchingLaunching(path.join(directory, 'Program.cs'));

        assert.strictEqual(service.isLaunching(path.join(directory, 'First.csproj')), true);
        assert.strictEqual(service.isLaunching(path.join(directory, 'Second.csproj')), true);
    });

    test('isLaunching reports an unprovable project/source association as launching', async () => {
        // Two projects share the directory, so `Program.cs` cannot be attributed to either.
        // Reporting "not launching" would let a second process start against whichever one
        // it actually belongs to.
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj', 'Program.cs');
        await service.launch(path.join(directory, 'First.csproj'), 'run', true);

        assert.strictEqual(service.isLaunching(path.join(directory, 'Program.cs')), true);
    });

    test('refuses an external launch claim once a lifecycle launch holds the AppHost', async () => {
        // A lifecycle caller that already passed `tryReserveLaunch` is on its way to
        // `startDebugging` and cannot be called back, so an F5 arriving second has to lose
        // or two AppHosts start for one project.
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');

        assert.strictEqual(service.tryReserveLaunch(projectPath), true);

        assert.strictEqual(service.tryReserveExternalLaunch(projectPath), false);
    });

    test('refuses an external launch claim addressed through the sibling AppHost source file', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');

        assert.strictEqual(service.tryReserveLaunch(path.join(directory, 'AppHost.csproj')), true);

        assert.strictEqual(service.tryReserveExternalLaunch(path.join(directory, 'Program.cs')), false);
    });

    test('allows an external launch claim for an unrelated AppHost', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const otherDirectory = createAppHostDirectory('AppHost.csproj', 'Program.cs');

        assert.strictEqual(service.tryReserveLaunch(path.join(directory, 'AppHost.csproj')), true);

        assert.strictEqual(service.tryReserveExternalLaunch(path.join(otherDirectory, 'AppHost.csproj')), true);
    });

    test('allows an external launch claim after the lifecycle claim is cleared', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');

        assert.strictEqual(service.tryReserveLaunch(projectPath), true);
        service.clearLaunching(projectPath);

        assert.strictEqual(service.tryReserveExternalLaunch(projectPath), true);
    });

    test('an external launch claim does not itself block a later external launch claim', async () => {
        // Only a lifecycle claim refuses. `reserveLaunch` on its own just makes the launch
        // visible, and refusing the user's F5 because an earlier F5 was recorded would break
        // the ordinary restart flow.
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');

        assert.strictEqual(service.tryReserveExternalLaunch(projectPath), true);

        assert.strictEqual(service.tryReserveExternalLaunch(projectPath), true);
    });

    test('multiple paths can be tracked independently', async () => {
        await service.launch('/repo/AppHost1.csproj', 'run', true);
        await service.launch('/repo/AppHost2.csproj', 'run', true);

        assert.strictEqual(service.isLaunching('/repo/AppHost1.csproj'), true);
        assert.strictEqual(service.isLaunching('/repo/AppHost2.csproj'), true);

        service.clearLaunching('/repo/AppHost1.csproj');

        assert.strictEqual(service.isLaunching('/repo/AppHost1.csproj'), false);
        assert.strictEqual(service.isLaunching('/repo/AppHost2.csproj'), true);
    });

    test('an expiring external reservation does not clear a newer launch of the same AppHost', async () => {
        // Repeated reservations are allowed and an internal launch can reuse the same key,
        // so an unconditional delete scheduled by the first reservation would clear a launch
        // that is still in flight and reopen the duplicate-launch window.
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
            const projectPath = path.join(directory, 'AppHost.csproj');

            assert.strictEqual(service.tryReserveExternalLaunch(projectPath), true);
            clock.tick(externalLaunchReservationTimeoutMs - 1);
            assert.strictEqual(service.tryReserveExternalLaunch(projectPath), true);

            clock.tick(2);

            assert.strictEqual(service.isLaunching(projectPath), true);
        }
        finally {
            clock.restore();
        }
    });

    test('an expiring external reservation does not clear a lifecycle claim taken afterwards', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
            const projectPath = path.join(directory, 'AppHost.csproj');

            assert.strictEqual(service.tryReserveExternalLaunch(projectPath), true);
            service.clearLaunching(projectPath);
            assert.strictEqual(service.tryReserveLaunch(projectPath), true);

            clock.tick(externalLaunchReservationTimeoutMs + 1);

            assert.strictEqual(service.isLaunching(projectPath), true);
            assert.strictEqual(service.hasLifecycleLaunchClaim(projectPath), true);
        }
        finally {
            clock.restore();
        }
    });

    test('an external reservation still expires on its own when nothing supersedes it', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
            const projectPath = path.join(directory, 'AppHost.csproj');

            assert.strictEqual(service.tryReserveExternalLaunch(projectPath), true);

            clock.tick(externalLaunchReservationTimeoutMs + 1);

            assert.strictEqual(service.isLaunching(projectPath), false);
        }
        finally {
            clock.restore();
        }
    });

    test('marks its own debug configurations so the shared resolver does not claim them as external', async () => {
        // `launchCore` reserves before `startDebugging`, and the configuration provider is
        // the same hook a `launch.json`/F5 launch goes through. Without the marker the
        // provider would refuse the launch against the caller's own claim.
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');

        await service.launch(projectPath, 'run', true);

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.launchedByExtension, true);
    });

    test('serializes editor and tool launch work for the same AppHost identity', async () => {
        let releaseFirst: (() => void) | undefined;
        let signalFirstStarted: (() => void) | undefined;
        let firstActionStarted = false;
        let secondActionStarted = false;
        const firstAction = new Promise<void>(resolve => { releaseFirst = resolve; });
        const firstStarted = new Promise<void>(resolve => { signalFirstStarted = resolve; });

        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const editorLaunch = service.runWithAppHostLifecycleLock(path.join(directory, 'AppHost.csproj'), new vscode.CancellationTokenSource().token, async () => {
            firstActionStarted = true;
            signalFirstStarted?.();
            await firstAction;
            return 'editor';
        });
        const toolLaunch = service.runWithAppHostLifecycleLock(path.join(directory, 'Program.cs'), new vscode.CancellationTokenSource().token, async () => {
            secondActionStarted = true;
            return 'tool';
        });
        await firstStarted;

        assert.strictEqual(firstActionStarted, true);
        assert.strictEqual(secondActionStarted, false);

        releaseFirst?.();
        assert.deepStrictEqual(await Promise.all([editorLaunch, toolLaunch]), ['editor', 'tool']);
        assert.strictEqual(secondActionStarted, true);
    });

    test('cancels a queued lifecycle operation without waiting for the active operation', async () => {
        const activeOperation = new Promise<void>(() => { });
        const active = service.runWithAppHostLifecycleLock('/repo/AppHost/AppHost.csproj', new vscode.CancellationTokenSource().token, () => activeOperation);
        const tokenSource = new vscode.CancellationTokenSource();
        const queued = service.runWithAppHostLifecycleLock('/repo/AppHost/AppHost.csproj', tokenSource.token, async () => 'queued');
        tokenSource.cancel();

        await assert.rejects(queued, vscode.CancellationError);
        assert.strictEqual(service.pendingLifecycleOperationCount, 1);
        void active;
    });

    test('bounds lifecycle lock waits when the active operation does not settle', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const clock = sinon.useFakeTimers();
        let releaseActive: (() => void) | undefined;
        try {
            const active = service.runWithAppHostLifecycleLock(
                path.join(directory, 'AppHost.csproj'),
                new vscode.CancellationTokenSource().token,
                () => new Promise<void>(resolve => { releaseActive = resolve; }));
            await Promise.resolve();

            const queued = service.runWithAppHostLifecycleLock(
                path.join(directory, 'Program.cs'),
                new vscode.CancellationTokenSource().token,
                async () => 'queued');
            const rejection = assert.rejects(queued, AppHostLifecycleLockTimeoutError);

            await clock.tickAsync(appHostLifecycleLockWaitTimeoutMs);
            await rejection;

            releaseActive?.();
            await active;
        }
        finally {
            releaseActive?.();
            clock.restore();
        }
    });

    test('surfaces a localized message when the editor launch path times out on the lifecycle lock', async () => {
        const clock = sinon.useFakeTimers();
        let releaseActive: (() => void) | undefined;
        try {
            const active = service.runWithAppHostLifecycleLock(
                '/repo/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token,
                () => new Promise<void>(resolve => { releaseActive = resolve; }));
            await Promise.resolve();

            const blockedLaunch = service.launch('/repo/AppHost/AppHost.csproj', 'run', true);
            const rejection = assert.rejects(blockedLaunch, (error: unknown) => {
                assert.ok(error instanceof AppHostLifecycleLockTimeoutError);
                assert.strictEqual(error.message, appHostLifecycleBusy);
                return true;
            });

            await clock.tickAsync(appHostLifecycleLockWaitTimeoutMs);
            await rejection;

            releaseActive?.();
            await active;
        }
        finally {
            releaseActive?.();
            clock.restore();
        }
    });

    test('serializes lifecycle work across every path shape that names one AppHost', async () => {
        // The lock key must be a pure function of the path, so it is derived from the
        // directory listing rather than by scanning the keys already in flight. Scanning
        // would make the key depend on insertion order and could hand a later caller its
        // own lock while an operation was already running against the same AppHost.
        const directory = createAppHostDirectory('AppHost.csproj', 'apphost.cs');
        const started: string[] = [];
        let releaseFirst: (() => void) | undefined;
        let signalFirstStarted: (() => void) | undefined;
        const firstAction = new Promise<void>(resolve => { releaseFirst = resolve; });
        const firstStarted = new Promise<void>(resolve => { signalFirstStarted = resolve; });

        const first = service.runWithAppHostLifecycleLock(path.join(directory, 'apphost.cs'), new vscode.CancellationTokenSource().token, async () => {
            started.push('apphost.cs');
            signalFirstStarted?.();
            await firstAction;
            return 'apphost.cs';
        });
        await firstStarted;

        const second = service.runWithAppHostLifecycleLock(path.join(directory, 'AppHost.csproj'), new vscode.CancellationTokenSource().token, async () => {
            started.push('AppHost.csproj');
            return 'AppHost.csproj';
        });

        assert.deepStrictEqual(started, ['apphost.cs']);

        releaseFirst?.();
        await Promise.all([first, second]);
        assert.deepStrictEqual(started, ['apphost.cs', 'AppHost.csproj']);
    });

    test('does not share a lifecycle lock between sibling AppHost projects in one directory', async () => {
        // Keying the lock on the directory would serialize two AppHosts that identity
        // comparison proves are distinct, so a slow start of one would make starting the
        // other fail with `busy` once the 10s wait budget expired.
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj');
        const started: string[] = [];
        const active = service.runWithAppHostLifecycleLock(path.join(directory, 'First.csproj'), new vscode.CancellationTokenSource().token, async () => {
            started.push('first');
            await new Promise<void>(() => { });
        });

        await service.runWithAppHostLifecycleLock(path.join(directory, 'Second.csproj'), new vscode.CancellationTokenSource().token, async () => {
            started.push('second');
        });

        assert.deepStrictEqual(started, ['first', 'second']);
        void active;
    });

    test('does not share a lifecycle lock between AppHosts in different directories', async () => {
        const started: string[] = [];
        const active = service.runWithAppHostLifecycleLock('/repo/First/AppHost.csproj', new vscode.CancellationTokenSource().token, async () => {
            started.push('first');
            await new Promise<void>(() => { });
        });

        await service.runWithAppHostLifecycleLock('/repo/Second/AppHost.csproj', new vscode.CancellationTokenSource().token, async () => {
            started.push('second');
        });

        assert.deepStrictEqual(started, ['first', 'second']);
        void active;
    });

    test('keeps lifecycle lock ownership stable when sibling files are added or removed', async () => {
        async function assertMutationKeepsSecondOperationQueued(
            initialEntries: readonly string[],
            firstPath: string,
            secondPath: string,
            mutateDirectory: (directory: string) => void,
        ): Promise<void> {
            const directory = createAppHostDirectory(...initialEntries);
            const started: string[] = [];
            let releaseFirst: (() => void) | undefined;
            let signalFirstStarted: (() => void) | undefined;
            const firstStarted = new Promise<void>(resolve => { signalFirstStarted = resolve; });
            const first = service.runWithAppHostLifecycleLock(
                path.join(directory, firstPath),
                new vscode.CancellationTokenSource().token,
                async () => {
                    started.push('first');
                    signalFirstStarted?.();
                    await new Promise<void>(resolve => { releaseFirst = resolve; });
                    return 'first';
                });
            await firstStarted;

            mutateDirectory(directory);

            const second = service.runWithAppHostLifecycleLock(
                path.join(directory, secondPath),
                new vscode.CancellationTokenSource().token,
                async () => {
                    started.push('second');
                    return 'second';
                });

            await Promise.resolve();
            await Promise.resolve();
            assert.deepStrictEqual(started, ['first']);

            releaseFirst?.();
            assert.deepStrictEqual(await Promise.all([first, second]), ['first', 'second']);
            assert.deepStrictEqual(started, ['first', 'second']);
        }

        await assertMutationKeepsSecondOperationQueued(
            ['AppHost.csproj', 'Program.cs'],
            'Program.cs',
            'AppHost.csproj',
            directory => fs.writeFileSync(path.join(directory, 'Second.csproj'), ''));

        await assertMutationKeepsSecondOperationQueued(
            ['AppHost.csproj', 'Second.csproj', 'Program.cs'],
            'AppHost.csproj',
            'Program.cs',
            directory => fs.rmSync(path.join(directory, 'Second.csproj')));
    });

    test('cancels a lifecycle operation that outruns its budget instead of releasing the lock beside it', async () => {
        const clock = sinon.useFakeTimers();
        try {
            let observedCancellation = false;
            let settleWedged!: () => void;
            const wedged = service.runWithAppHostLifecycleLock(
                '/repo/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token,
                lockToken => new Promise<void>(resolve => {
                    settleWedged = resolve;
                    lockToken.onCancellationRequested(() => { observedCancellation = true; });
                }));
            await Promise.resolve();

            // A caller already waiting still gives up on its own 10s budget.
            const queued = service.runWithAppHostLifecycleLock(
                '/repo/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token,
                async () => 'queued');
            const queuedRejection = assert.rejects(queued, AppHostLifecycleLockTimeoutError);
            await clock.tickAsync(appHostLifecycleLockWaitTimeoutMs);
            await queuedRejection;

            // The backstop cancels the operation. It must not hand the lock to someone
            // else while the first operation is still in flight: that is the duplicate
            // start/stop the lock exists to prevent.
            await clock.tickAsync(appHostLifecycleLockMaxHoldMs);
            assert.strictEqual(observedCancellation, true, 'the backstop should cancel the operation');

            const blocked = service.runWithAppHostLifecycleLock(
                '/repo/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token,
                async () => 'blocked');
            const blockedRejection = assert.rejects(blocked, AppHostLifecycleLockTimeoutError);
            await clock.tickAsync(appHostLifecycleLockWaitTimeoutMs);
            await blockedRejection;

            // Once the cancelled operation actually settles, the AppHost is usable again.
            settleWedged();
            await wedged;
            const recovered = service.runWithAppHostLifecycleLock(
                '/repo/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token,
                async () => 'recovered');
            await clock.tickAsync(appHostLifecycleLockWaitTimeoutMs);
            assert.strictEqual(await recovered, 'recovered');
        }
        finally {
            clock.restore();
        }
    });

    test('cancels the lifecycle operation when the caller cancels', async () => {
        const source = new vscode.CancellationTokenSource();
        let observedCancellation = false;
        let signalStarted!: () => void;
        const started = new Promise<void>(resolve => { signalStarted = resolve; });
        const running = service.runWithAppHostLifecycleLock(
            '/repo/AppHost/AppHost.csproj',
            source.token,
            lockToken => new Promise<string>(resolve => {
                lockToken.onCancellationRequested(() => {
                    observedCancellation = true;
                    resolve('cancelled');
                });
                signalStarted();
            }));
        await started;
        source.cancel();

        assert.strictEqual(await running, 'cancelled');
        assert.strictEqual(observedCancellation, true);
        source.dispose();
    });

    test('matches an editor session whose program is the workspace folder through its resolved AppHost', () => {
        // `Aspire: Configure launch.json` writes `program: '${workspaceFolder}'`, so for
        // the standard configure-then-F5 flow the session path is a directory and can
        // never equal the AppHost file an agent names.
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const otherDirectory = createAppHostDirectory('AppHost.csproj');
        const folderSession = {
            appHostPath: path.dirname(directory),
            resolvedAppHostPath: path.join(directory, 'AppHost.csproj'),
            operationKind: 'run' as const,
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { },
        };
        service.setEditorSessionProvider(() => [folderSession]);

        assert.deepStrictEqual(service.getEditorRunSessions(path.join(directory, 'AppHost.csproj')), { sessions: [folderSession], ambiguous: false });
        assert.deepStrictEqual(service.getEditorRunSessions(path.join(directory, 'Program.cs')), { sessions: [folderSession], ambiguous: false });
        assert.deepStrictEqual(service.getEditorRunSessions(path.join(otherDirectory, 'AppHost.csproj')), { sessions: [], ambiguous: false });
    });

    test('does not match a folder session that has no resolved AppHost', () => {
        // Without a resolved candidate the extension genuinely does not know which
        // AppHost under the folder is running, so it must not guess.
        const directory = createAppHostDirectory('AppHost.csproj');
        const folderSession = {
            appHostPath: path.dirname(directory),
            resolvedAppHostPath: undefined,
            operationKind: 'run' as const,
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { },
        };
        service.setEditorSessionProvider(() => [folderSession]);

        assert.deepStrictEqual(service.getEditorRunSessions(path.join(directory, 'AppHost.csproj')), { sessions: [], ambiguous: false });
    });

    test('reports an unprovable session association as ambiguous rather than owned', () => {
        // Two AppHost projects share the directory, so a session started for `First.csproj`
        // cannot be attributed to `Program.cs`. Reporting it as owned would let the stop
        // tool terminate a session the caller never named.
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj', 'Program.cs');
        const session = {
            appHostPath: path.join(directory, 'First.csproj'),
            resolvedAppHostPath: undefined,
            operationKind: 'run' as const,
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { },
        };
        service.setEditorSessionProvider(() => [session]);

        assert.deepStrictEqual(service.getEditorRunSessions(path.join(directory, 'Program.cs')), { sessions: [], ambiguous: true });
        assert.deepStrictEqual(service.getEditorRunSessions(path.join(directory, 'Second.csproj')), { sessions: [], ambiguous: false });
        assert.deepStrictEqual(service.getEditorRunSessions(path.join(directory, 'First.csproj')), { sessions: [session], ambiguous: false });
    });

    test('prefers the resolved AppHost over the session program when both are present', () => {
        // `appHostPath` is whatever the debug configuration named; only the resolved path
        // is authoritative, and trusting the former would attribute the session to the
        // wrong AppHost in a directory that holds more than one.
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj');
        const session = {
            appHostPath: path.join(directory, 'First.csproj'),
            resolvedAppHostPath: path.join(directory, 'Second.csproj'),
            operationKind: 'run' as const,
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { },
        };
        service.setEditorSessionProvider(() => [session]);

        assert.deepStrictEqual(service.getEditorRunSessions(path.join(directory, 'Second.csproj')), { sessions: [session], ambiguous: false });
        assert.deepStrictEqual(service.getEditorRunSessions(path.join(directory, 'First.csproj')), { sessions: [], ambiguous: false });
    });

    test('matches project and AppHost source identities without matching sibling projects', () => {
        const singlePair = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const singleSourcePair = createAppHostDirectory('AppHost.csproj', 'apphost.cs');
        const siblingProjects = createAppHostDirectory('First.csproj', 'Second.csproj', 'Program.cs');
        const siblingSources = createAppHostDirectory('apphost.ts', 'apphost.mts');

        assert.strictEqual(service.compareAppHostIdentity(path.join(singlePair, 'AppHost.csproj'), path.join(singlePair, 'Program.cs')), 'same');
        assert.strictEqual(service.compareAppHostIdentity(path.join(singleSourcePair, 'AppHost.csproj'), path.join(singleSourcePair, 'apphost.cs')), 'same');
        assert.strictEqual(service.compareAppHostIdentity(path.join(siblingProjects, 'First.csproj'), path.join(siblingProjects, 'Second.csproj')), 'different');
        assert.strictEqual(service.compareAppHostIdentity(path.join(siblingSources, 'apphost.ts'), path.join(siblingSources, 'apphost.mts')), 'different');
        // One project cannot be paired with one of two candidate sources, or one source
        // with one of two candidate projects, so neither relation can be proven.
        assert.strictEqual(service.compareAppHostIdentity(path.join(siblingProjects, 'First.csproj'), path.join(siblingProjects, 'Program.cs')), 'ambiguous');
    });

    test('refuses to prove an identity it cannot enumerate', () => {
        // A directory that cannot be listed gives no evidence either way, and answering
        // `different` there would let two operations run against one AppHost.
        assert.strictEqual(service.compareAppHostIdentity('/repo/AppHost/AppHost.csproj', '/repo/AppHost/Program.cs'), 'ambiguous');
        assert.strictEqual(service.compareAppHostIdentity('/repo/AppHost/AppHost.csproj', '/repo/AppHost/AppHost.csproj'), 'same');
        assert.strictEqual(service.compareAppHostIdentity('/repo/First/AppHost.csproj', '/repo/Second/AppHost.csproj'), 'different');
    });


    test('treats a symlink and its target as one AppHost for identity and locking', function () {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const realProject = path.join(directory, 'AppHost.csproj');
        const linkedProject = path.join(directory, 'Linked.csproj');
        try {
            fs.symlinkSync(realProject, linkedProject);
        }
        catch {
            // Creating a symlink needs elevation or developer mode on Windows.
            this.skip();
            return;
        }

        // Lexical keys would report `different` here, so a lifecycle caller holding the
        // link would miss the running session and the lock guarding the real file, and
        // start a second process for one AppHost.
        assert.strictEqual(service.compareAppHostIdentity(linkedProject, realProject), 'same');
        assert.strictEqual(getAppHostIdentityKey(linkedProject), getAppHostIdentityKey(realProject));
    });

    test('returns only editor-owned run sessions for the requested AppHost identity', () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const runSession = {
            appHostPath: path.join(directory, 'Program.cs'),
            operationKind: 'run' as const,
            resolvedAppHostPath: undefined,
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { },
        };
        const publishSession = {
            appHostPath: path.join(directory, 'AppHost.csproj'),
            operationKind: 'publish' as const,
            resolvedAppHostPath: undefined,
            startupCompleted: true,
            configuration: { noDebug: true },
            stopDebugging: async () => { },
        };
        const testSession = {
            appHostPath: path.join(directory, 'AppHost.csproj'),
            operationKind: 'test' as const,
            resolvedAppHostPath: undefined,
            startupCompleted: true,
            configuration: { noDebug: true },
            stopDebugging: async () => { },
        };
        service.setEditorSessionProvider(() => [runSession, publishSession, testSession]);

        assert.deepStrictEqual(service.getEditorRunSessions(path.join(directory, 'AppHost.csproj')), { sessions: [runSession], ambiguous: false });
    });


    test('reads an authoritative running snapshot independent of tree visibility', async () => {
        const expected = [{ appHostPath: path.resolve('/repo/AppHost/AppHost.csproj') }];
        service.setRunningAppHostProvider(async (token: vscode.CancellationToken) => {
            assert.strictEqual(token.isCancellationRequested, false);
            return expected;
        });

        const actual = await service.getRunningAppHosts(new vscode.CancellationTokenSource().token);

        assert.deepStrictEqual(actual, expected);
    });

    test('launch clears launching state and throws when startDebugging returns false', async () => {
        // vscode.debug.startDebugging returns Promise<boolean> and resolves false when
        // the debug adapter rejects or no provider matches — no terminate event is
        // emitted in that case. Without explicit cleanup the tree item would be stuck
        // showing the "Starting..." spinner forever.
        startDebuggingStub.resolves(false);

        await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), /did not start the Aspire run session/);

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('launch reports error telemetry when startDebugging returns false', async () => {
        startDebuggingStub.resolves(false);
        const fake = new FakeTelemetryReporter();
        const restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
        try {
            await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), /did not start the Aspire run session/);

            const appHostLaunchEvents = fake.events.filter(e => e.name === 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(appHostLaunchEvents.length, 1);
            const event = appHostLaunchEvents[0];
            assert.strictEqual(event.name, 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(event.properties?.outcome, 'error');
            assert.strictEqual(event.properties?.error_kind, 'StartDebuggingDeclined');
            assert.ok(typeof event.measurements?.duration_ms === 'number');
        }
        finally {
            restore();
            __resetCommonPropertiesForTests();
        }
    });

    test('launch cancels before starting debug session when CLI is unavailable', async () => {
        resolveCliPathStub.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });
        const showErrorMessageStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const fake = new FakeTelemetryReporter();
        const restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
        try {
            await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), vscode.CancellationError);

            assert.strictEqual(startDebuggingStub.called, false);
            assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
            const appHostLaunchEvents = fake.events.filter(e => e.name === 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(appHostLaunchEvents.length, 1);
            const event = appHostLaunchEvents[0];
            assert.strictEqual(event.name, 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(event.properties?.outcome, 'canceled');
            assert.strictEqual(event.properties?.error_kind, undefined);
            assert.ok(typeof event.measurements?.duration_ms === 'number');
        }
        finally {
            showErrorMessageStub.restore();
            restore();
            __resetCommonPropertiesForTests();
        }
    });

    test('launch clears launching state and rethrows when startDebugging throws', async () => {
        startDebuggingStub.rejects(new Error('boom'));

        await assert.rejects(service.launch('/repo/AppHost.csproj', 'run', true), /boom/);

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('launch emits one bounded result telemetry event', async () => {
        const fake = new FakeTelemetryReporter();
        const restore = __setReporterForTests(fake as unknown as Parameters<typeof __setReporterForTests>[0]);
        try {
            await service.launch('/repo/AppHost.csproj', 'custom' as Parameters<AppHostLaunchService['launch']>[1], true);

            const appHostLaunchEvents = fake.events.filter(e => e.name === 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(appHostLaunchEvents.length, 1);
            const event = appHostLaunchEvents[0];
            assert.strictEqual(event.name, 'aspire/vscode/apphost/launch/result');
            assert.strictEqual(event.properties?.command, 'other');
            assert.strictEqual(event.properties?.outcome, 'success');
            assert.strictEqual(event.properties?.mode, 'run');
            assert.strictEqual(event.properties?.apphost_language, 'csharp');
            assert.strictEqual(event.properties?.execution_suppressed, 'false');
            assert.ok(typeof event.measurements?.duration_ms === 'number');
        }
        finally {
            restore();
            __resetCommonPropertiesForTests();
        }
    });

    test('terminated run sessions include appHostPath and stop refresh semantics', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
                command: 'run',
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: 'run',
            shouldRequestStopRefresh: true,
        });
    });

    test('terminated non-run sessions do not request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
                command: 'publish',
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: 'publish',
            shouldRequestStopRefresh: false,
        });
    });

    test('terminated Aspire sessions default missing command to run and request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: 'run',
            shouldRequestStopRefresh: true,
        });
    });

    test('terminated Aspire sessions drop invalid command values and do not request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
                command: 'invalid',
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: undefined,
            shouldRequestStopRefresh: false,
        });
    });
});
