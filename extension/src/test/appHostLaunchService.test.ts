import * as assert from 'assert';
import * as crypto from 'crypto';
import fs = require('fs');
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireExtendedDebugConfiguration, type AspireResourceDebugSession } from '../dcp/types';
import { appHostLaunchReservationIdConfigKey, appHostRestartSourceSessionIdConfigKey, appHostTelemetryTargetPathConfigKey } from '../debugger/AspireDebugConfigurationMetadata';
import { isAspireDebugConfigurationExtensionOwned } from '../debugger/AspireDebugConfigurationProviderInternal';
import * as locStrings from '../loc/strings';
import { appHostLifecycleBusy } from '../loc/strings';
import { AppHostLaunchService, AppHostLifecycleLockTimeoutError, AppHostStopCancellationError, appHostLifecycleLockMaxHoldMs, appHostLifecycleLockWaitTimeoutMs, externalLaunchReservationTimeoutMs, type AppHostLaunchCapabilityProvider, type AppHostLaunchRequestedEvent, type AppHostLaunchSession } from '../services/AppHostLaunchService';
import { getAppHostIdentityKey } from '../utils/appHostIdentity';
import * as cliPathModule from '../utils/cliPath';
import { isolatedLaunchCapability, launchProfileCapability, type CapabilityStatus } from '../types/configInfo';
import { getCliPathTargetKey, windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import { __resetCommonPropertiesForTests, __setReporterForTests } from '../utils/telemetry';
import { writeLinkedWorktreeMetadata } from './testGitWorktree';

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
    // Stop the ancestor walk so a checkout that is itself a linked worktree
    // does not make every launch() infer --isolated.
    fs.mkdirSync(path.join(directory, '.git'));
    for (const entry of entries) {
        fs.writeFileSync(path.join(directory, entry), '');
    }

    return directory;
}

class FakeCapabilityProvider implements AppHostLaunchCapabilityProvider {
    readonly calls: Array<{
        capability: string;
        options: { suppressErrors?: boolean; forceRefresh?: boolean; cliPath?: string; cancellationToken?: vscode.CancellationToken; minimumVersion?: string; target?: import('../utils/cliPathVariables').CliPathResolutionTarget } | undefined;
    }> = [];
    capabilityStatus: CapabilityStatus = 'supported';
    launchProfileCapabilityStatus: CapabilityStatus = 'supported';

    async getCapabilityStatus(
        capability: string,
        options?: { suppressErrors?: boolean; forceRefresh?: boolean; cliPath?: string; cancellationToken?: vscode.CancellationToken; minimumVersion?: string; target?: import('../utils/cliPathVariables').CliPathResolutionTarget },
    ): Promise<CapabilityStatus> {
        this.calls.push({ capability, options });
        return capability === isolatedLaunchCapability
            ? this.capabilityStatus
            : capability === launchProfileCapability ? this.launchProfileCapabilityStatus : 'unsupported';
    }
}

interface LaunchArgumentPreparer {
    prepareLaunchArguments(
        appHostPath: string,
        command: 'run' | 'deploy' | 'publish' | 'do',
        args: string[] | undefined,
        token: vscode.CancellationToken,
        cliPath?: string,
        target?: import('../utils/cliPathVariables').CliPathResolutionTarget,
        isolated?: boolean,
        isolationPolicy?: 'explicit-only' | 'linked-worktree-default',
        launchProfile?: string,
    ): Promise<{
        args: string[] | undefined;
        isolation: {
            effective: boolean;
            option: boolean | undefined;
        };
    }>;
}

suite('AppHostLaunchService', () => {
    let service: AppHostLaunchService;
    let capabilityProvider: FakeCapabilityProvider;
    let startDebuggingStub: sinon.SinonStub;
    let stopDebuggingStub: sinon.SinonStub;
    let resolveCliPathStub: sinon.SinonStub;
    let onDidStartDebugSessionStub: sinon.SinonStub;
    let onDidStartDebugSessionCallback: ((session: vscode.DebugSession) => void) | undefined;
    let onDidTerminateDebugSessionStub: sinon.SinonStub;
    let onDidTerminateDebugSessionCallback: ((session: vscode.DebugSession) => void) | undefined;

    setup(() => {
        onDidStartDebugSessionStub = sinon.stub(vscode.debug, 'onDidStartDebugSession').callsFake(callback => {
            onDidStartDebugSessionCallback = callback;
            return new vscode.Disposable(() => { });
        });
        onDidTerminateDebugSessionStub = sinon.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(callback => {
            onDidTerminateDebugSessionCallback = callback;
            return new vscode.Disposable(() => { });
        });
        capabilityProvider = new FakeCapabilityProvider();
        service = new AppHostLaunchService(capabilityProvider);
        startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
        stopDebuggingStub = sinon.stub(vscode.debug, 'stopDebugging').resolves();
        resolveCliPathStub = sinon.stub(cliPathModule, 'resolveCliPath').resolves({ cliPath: '/path/bin/aspire', available: true, source: 'path' });
    });

    teardown(() => {
        service.dispose();
        startDebuggingStub.restore();
        stopDebuggingStub.restore();
        resolveCliPathStub.restore();
        onDidStartDebugSessionStub.restore();
        onDidTerminateDebugSessionStub.restore();
        onDidStartDebugSessionCallback = undefined;
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
        assert.strictEqual(config.resolvedCliPath, '/path/bin/aspire');
        assert.strictEqual(config.__aspireAppHostSelectionOrigin, 'user-selection');
    });

    test('suppressed lifecycle launch does not report verified isolation', async () => {
        const environmentVariables = [
            'ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE',
            'ASPIRE_EXTENSION_E2E_STATE_FILE',
            'ASPIRE_EXTENSION_E2E_CONTROL_FILE',
            'ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH',
        ] as const;
        const originalValues = new Map(environmentVariables.map(name => [name, process.env[name]]));

        try {
            process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE = 'true';
            process.env.ASPIRE_EXTENSION_E2E_STATE_FILE = 'state.json';
            process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE = 'control.json';
            process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH = 'true';

            const isolation = await service.launchFromLifecycleOwner(
                '/repo/AppHost.csproj',
                'run',
                true,
                true,
                new vscode.CancellationTokenSource().token);

            assert.strictEqual(isolation, undefined);
            assert.strictEqual(startDebuggingStub.called, false);
        }
        finally {
            for (const [name, value] of originalValues) {
                if (value === undefined) {
                    delete process.env[name];
                }
                else {
                    process.env[name] = value;
                }
            }
        }
    });

    test('launch reuses an already-verified CLI path', async () => {
        const folder = { name: 'a', index: 0, uri: vscode.Uri.file('/repo') } as vscode.WorkspaceFolder;
        const target = workspaceFolderCliPathTarget(folder);

        await service.launch('/repo/AppHost.csproj', 'do', false, undefined, target, '/repo/bin/aspire');

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(resolveCliPathStub.called, false);
        assert.strictEqual(config.resolvedCliPath, '/repo/bin/aspire');
        assert.strictEqual(config.skipCliAvailabilityCheck, true);
    });

    test('launch request event records the verified CLI path and target', async () => {
        const folder = { name: 'a', index: 0, uri: vscode.Uri.file('/repo') } as vscode.WorkspaceFolder;
        const target = workspaceFolderCliPathTarget(folder);
        const events: AppHostLaunchRequestedEvent[] = [];
        service.onDidRequestLaunch(event => events.push(event));

        await service.launch('/repo/AppHost.csproj', 'do', true, 'deploy', target, '/repo/bin/aspire');

        assert.strictEqual(events.length, 1);
        assert.strictEqual(events[0].cliPath, '/repo/bin/aspire');
        assert.strictEqual(events[0].cliTargetKey, getCliPathTargetKey(target));
    });

    test('lifecycle-owned launch does not replace an existing workspace default', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        assert.strictEqual(service.tryReserveLaunch(appHostPath), true);

        await service.launchFromLifecycleOwner(appHostPath, 'run', true, undefined, new vscode.CancellationTokenSource().token);

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.__aspireAppHostSelectionOrigin, 'explicit-launch-configuration');
        assert.strictEqual(config.args, undefined);
    });

    test('lifecycle-owned launch forwards explicit isolation false', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        assert.strictEqual(service.tryReserveLaunch(appHostPath), true);

        await service.launchFromLifecycleOwner(appHostPath, 'run', true, false, new vscode.CancellationTokenSource().token);

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.__aspireAppHostSelectionOrigin, 'explicit-launch-configuration');
        assert.deepStrictEqual(config.args, ['--isolated', 'false']);
    });

    test('lifecycle-owned launch forwards --isolated', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        assert.strictEqual(service.tryReserveLaunch(appHostPath), true);

        await service.launchFromLifecycleOwner(appHostPath, 'run', true, true, new vscode.CancellationTokenSource().token);

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.deepStrictEqual(config.args, ['--isolated']);
    });

    test('lifecycle-owned launch forwards a selected launch profile', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        assert.strictEqual(service.tryReserveLaunch(appHostPath), true);

        await service.launchFromLifecycleOwner(
            appHostPath,
            'run',
            true,
            undefined,
            new vscode.CancellationTokenSource().token,
            'Development HTTPS');

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.launchProfile, 'Development HTTPS');
        assert.deepStrictEqual(config.args, ['--launch-profile=Development HTTPS']);
        assert.strictEqual(capabilityProvider.calls.at(-1)?.capability, launchProfileCapability);
        assert.strictEqual(capabilityProvider.calls.at(-1)?.options?.cliPath, '/path/bin/aspire');
    });

    test('typed launch profile replaces root profile options and preserves AppHost arguments', async () => {
        const prepared = await (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
            '/repo/AppHost.csproj',
            'run',
            [
                '--verbose',
                '--launch-profile', 'First',
                '--launch-profile=Second',
                '-lp', 'Third',
                '-lp=Fourth',
                '--',
                '--launch-profile', 'AppHostValue',
            ],
            new vscode.CancellationTokenSource().token,
            '/path/bin/aspire',
            undefined,
            undefined,
            undefined,
            'Development HTTPS');

        assert.deepStrictEqual(prepared.args, [
            '--verbose',
            '--launch-profile=Development HTTPS',
            '--',
            '--launch-profile', 'AppHostValue',
        ]);
    });

    test('typed launch profile preserves root options after a valueless profile option', async () => {
        const prepared = await (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
            '/repo/AppHost.csproj',
            'run',
            ['--launch-profile', '--isolated'],
            new vscode.CancellationTokenSource().token,
            '/path/bin/aspire',
            undefined,
            undefined,
            undefined,
            'Development HTTPS');

        assert.deepStrictEqual(prepared.args, [
            '--isolated',
            '--launch-profile=Development HTTPS',
        ]);
    });

    test('typed launch profile rejects an older or unverifiable exact CLI', async () => {
        for (const status of ['unsupported', 'unavailable'] as const) {
            capabilityProvider.launchProfileCapabilityStatus = status;

            await assert.rejects(
                (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
                    '/repo/AppHost.csproj',
                    'run',
                    undefined,
                    new vscode.CancellationTokenSource().token,
                    '/path/bin/aspire',
                    undefined,
                    undefined,
                    undefined,
                    'Development HTTPS'),
                status === 'unsupported'
                    ? /does not support the requested launch profile/
                    : /launch profile capability could not be verified/);
        }
    });

    test('non-run launch rejects a typed launch profile', async () => {
        await assert.rejects(
            (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
                '/repo/AppHost.csproj',
                'publish',
                ['--verbose'],
                new vscode.CancellationTokenSource().token,
                '/path/bin/aspire',
                undefined,
                undefined,
                undefined,
                'Development HTTPS'),
            /Launch profiles are only supported for the run command/);
        assert.deepStrictEqual(capabilityProvider.calls, []);
    });

    test('prepareLaunchArguments does not infer root isolation when only app args specify isolated', async () => {
        const directory = createAppHostDirectory('AppHost.csproj');
        fs.rmSync(path.join(directory, '.git'), { recursive: true, force: true });
        writeLinkedWorktreeMetadata(directory, path.join(directory, 'common', '.git'));
        const appHostPath = path.join(directory, 'AppHost.csproj');
        const cancellation = new vscode.CancellationTokenSource();

        const prepared = await (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
            appHostPath,
            'run',
            ['--', '--isolated', 'false'],
            cancellation.token,
            '/path/bin/aspire');

        const rootArguments = prepared.args?.[0] === '--'
            ? undefined
            : prepared.args?.slice(0, prepared.args?.indexOf('--'));
        const appHostArguments = prepared.args?.[0] === '--'
            ? prepared.args
            : prepared.args?.slice(prepared.args.indexOf('--'));

        assert.strictEqual(rootArguments, undefined);
        assert.deepStrictEqual(appHostArguments, ['--', '--isolated', 'false']);
        assert.deepStrictEqual(prepared.isolation, { effective: false, option: undefined });
        assert.deepStrictEqual(capabilityProvider.calls, []);
    });

    test('prepareLaunchArguments preserves explicit root isolated false', async () => {
        const directory = createAppHostDirectory('AppHost.csproj');
        fs.rmSync(path.join(directory, '.git'), { recursive: true, force: true });
        writeLinkedWorktreeMetadata(directory, path.join(directory, 'common', '.git'));
        const appHostPath = path.join(directory, 'AppHost.csproj');

        const prepared = await (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
            appHostPath,
            'run',
            ['--isolated', 'false', '--', '--isolated'],
            new vscode.CancellationTokenSource().token,
            '/path/bin/aspire');

        assert.deepStrictEqual(prepared, {
            args: ['--isolated', 'false', '--', '--isolated'],
            isolation: { effective: false, option: false },
        });
    });

    test('older CLI external launch removes separate root isolation while preserving AppHost args', async () => {
        capabilityProvider.capabilityStatus = 'unsupported';
        const prepared = await (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
            '/repo/AppHost.csproj',
            'run',
            ['--verbose', '--isolated', 'false', '--', '--isolated', 'false'],
            new vscode.CancellationTokenSource().token,
            '/path/bin/aspire');

        assert.deepStrictEqual(prepared, {
            args: ['--verbose', '--', '--isolated', 'false'],
            isolation: { effective: false, option: undefined },
        });
    });

    test('older CLI external launch removes equals root isolation while preserving AppHost args', async () => {
        capabilityProvider.capabilityStatus = 'unsupported';
        const prepared = await (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
            '/repo/AppHost.csproj',
            'run',
            ['--isolated=false', '--', '--isolated=false'],
            new vscode.CancellationTokenSource().token,
            '/path/bin/aspire');

        assert.deepStrictEqual(prepared, {
            args: ['--', '--isolated=false'],
            isolation: { effective: false, option: undefined },
        });
    });

    test('non-authoritative isolation probes preserve explicit choices from stale capability data', async () => {
        const directory = createAppHostDirectory('AppHost.csproj');
        fs.rmSync(path.join(directory, '.git'), { recursive: true, force: true });
        writeLinkedWorktreeMetadata(directory, path.join(directory, 'common', '.git'));
        const appHostPath = path.join(directory, 'AppHost.csproj');

        for (const capabilityStatus of ['unsupported', 'unavailable'] as const) {
            capabilityProvider.capabilityStatus = capabilityStatus;
            for (const isolated of [true, false]) {
                const cancellation = new vscode.CancellationTokenSource();
                const isolation = await service.resolveLaunchIsolation(appHostPath, isolated, cancellation.token);

                assert.deepStrictEqual(isolation, { effective: isolated, option: isolated });
                assert.strictEqual(capabilityProvider.calls.at(-1)?.options?.forceRefresh, false);
                assert.strictEqual(capabilityProvider.calls.at(-1)?.options?.cliPath, undefined);
                assert.strictEqual(capabilityProvider.calls.at(-1)?.options?.cancellationToken, cancellation.token);
                assert.strictEqual(capabilityProvider.calls.at(-1)?.options?.minimumVersion, '13.2.0');
            }
        }
    });

    test('minimum CLI version fallback honors explicit and inferred isolation at launch time', async () => {
        capabilityProvider.capabilityStatus = 'supported';
        const explicitTruePath = '/repo/ExplicitTrue.csproj';
        const explicitFalsePath = '/repo/ExplicitFalse.csproj';
        const directory = createAppHostDirectory('Inferred.csproj');
        fs.rmSync(path.join(directory, '.git'), { recursive: true, force: true });
        writeLinkedWorktreeMetadata(directory, path.join(directory, 'common', '.git'));
        const inferredPath = path.join(directory, 'Inferred.csproj');
        assert.strictEqual(service.tryReserveLaunch(explicitTruePath), true);
        assert.strictEqual(service.tryReserveLaunch(explicitFalsePath), true);
        assert.strictEqual(service.tryReserveLaunch(inferredPath), true);
        const inferredCancellation = new vscode.CancellationTokenSource();

        const explicitTrue = await service.launchFromLifecycleOwner(
            explicitTruePath,
            'run',
            true,
            true,
            new vscode.CancellationTokenSource().token);
        const explicitFalse = await service.launchFromLifecycleOwner(
            explicitFalsePath,
            'run',
            true,
            false,
            new vscode.CancellationTokenSource().token);
        await service.launchFromLifecycleOwner(inferredPath, 'run', true, undefined, inferredCancellation.token);

        assert.deepStrictEqual(explicitTrue, { effective: true, option: true });
        assert.deepStrictEqual(explicitFalse, { effective: false, option: false });
        assert.deepStrictEqual(
            startDebuggingStub.getCalls().map(call => (call.args[1] as AspireExtendedDebugConfiguration).args),
            [['--isolated'], ['--isolated', 'false'], ['--isolated']]);
        assert.deepStrictEqual(capabilityProvider.calls.map(call => ({
            capability: call.capability,
            cliPath: call.options?.cliPath,
            forceRefresh: call.options?.forceRefresh,
            minimumVersion: call.options?.minimumVersion,
        })), [
            { capability: isolatedLaunchCapability, cliPath: '/path/bin/aspire', forceRefresh: true, minimumVersion: '13.2.0' },
            { capability: isolatedLaunchCapability, cliPath: '/path/bin/aspire', forceRefresh: true, minimumVersion: '13.2.0' },
            { capability: isolatedLaunchCapability, cliPath: '/path/bin/aspire', forceRefresh: true, minimumVersion: '13.2.0' },
        ]);
    });

    test('lifecycle-owned launch downgrades inferred isolation for an older CLI', async () => {
        capabilityProvider.capabilityStatus = 'unsupported';
        const directory = createAppHostDirectory('AppHost.csproj');
        fs.rmSync(path.join(directory, '.git'), { recursive: true, force: true });
        writeLinkedWorktreeMetadata(directory, path.join(directory, 'common', '.git'));
        const appHostPath = path.join(directory, 'AppHost.csproj');
        const cancellation = new vscode.CancellationTokenSource();
        assert.strictEqual(service.tryReserveLaunch(appHostPath), true);

        const isolation = await service.launchFromLifecycleOwner(appHostPath, 'run', true, undefined, cancellation.token);

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.args, undefined);
        assert.strictEqual(config.resolvedCliPath, '/path/bin/aspire');
        assert.deepStrictEqual(isolation, { effective: false, option: undefined });
        assert.deepStrictEqual(capabilityProvider.calls, [{
            capability: isolatedLaunchCapability,
            options: {
                suppressErrors: true,
                forceRefresh: true,
                cliPath: '/path/bin/aspire',
                cancellationToken: cancellation.token,
                minimumVersion: '13.2.0',
                target: windowCliPathTarget,
            },
        }]);
    });

    test('lifecycle-owned launch honors explicit isolation false for an older CLI', async () => {
        capabilityProvider.capabilityStatus = 'unsupported';
        const directory = createAppHostDirectory('AppHost.csproj');
        fs.rmSync(path.join(directory, '.git'), { recursive: true, force: true });
        writeLinkedWorktreeMetadata(directory, path.join(directory, 'common', '.git'));
        const appHostPath = path.join(directory, 'AppHost.csproj');
        assert.strictEqual(service.tryReserveLaunch(appHostPath), true);

        const isolation = await service.launchFromLifecycleOwner(
            appHostPath,
            'run',
            true,
            false,
            new vscode.CancellationTokenSource().token);

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.args, undefined);
        assert.deepStrictEqual(isolation, { effective: false, option: undefined });
    });

    test('lifecycle-owned launch rejects explicit isolation true for an older CLI', async () => {
        capabilityProvider.capabilityStatus = 'unsupported';
        const appHostPath = '/repo/AppHost.csproj';
        assert.strictEqual(service.tryReserveLaunch(appHostPath), true);
        const message = (locStrings as Record<string, unknown>).appHostLifecycleIsolationModeNotSupported;

        assert.strictEqual(message, 'The selected Aspire CLI does not support the requested isolation mode.');

        await assert.rejects(
            service.launchFromLifecycleOwner(
                appHostPath,
                'run',
                true,
                true,
                new vscode.CancellationTokenSource().token),
            (error: Error) => {
                assert.strictEqual(error.message, message);
                return true;
            });

        assert.strictEqual(startDebuggingStub.called, false);
    });

    test('lifecycle-owned launch fails safely when exact CLI support is unavailable', async () => {
        capabilityProvider.capabilityStatus = 'unavailable';
        const directory = createAppHostDirectory('AppHost.csproj');
        fs.rmSync(path.join(directory, '.git'), { recursive: true, force: true });
        writeLinkedWorktreeMetadata(directory, path.join(directory, 'common', '.git'));
        const appHostPath = path.join(directory, 'AppHost.csproj');
        const message = (locStrings as Record<string, unknown>).appHostLifecycleIsolationCapabilityCouldNotBeVerified;

        assert.strictEqual(message, 'The selected Aspire CLI isolation capability could not be verified.');

        for (const isolated of [true, false, undefined]) {
            assert.strictEqual(service.tryReserveLaunch(appHostPath), true);
            await assert.rejects(
                service.launchFromLifecycleOwner(
                    appHostPath,
                    'run',
                    true,
                    isolated,
                    new vscode.CancellationTokenSource().token),
                (error: Error) => {
                    assert.strictEqual(error.message, message);
                    return true;
                });
        }

        assert.strictEqual(startDebuggingStub.called, false);
    });

    test('launch omits inferred isolation for a primary checkout AppHost path', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');

        await service.launch(path.join(directory, 'AppHost.csproj'), 'run', true);

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.args, undefined);
    });

    test('launch does not infer --isolated for run commands in a linked worktree', async () => {
        const directory = createAppHostDirectory('Run.csproj', 'Deploy.csproj', 'Publish.csproj', 'Do.csproj');
        fs.rmSync(path.join(directory, '.git'), { recursive: true, force: true });
        writeLinkedWorktreeMetadata(directory, path.join(directory, 'common', '.git'));

        await service.launch(path.join(directory, 'Run.csproj'), 'run', true);
        await service.launch(path.join(directory, 'Deploy.csproj'), 'deploy', true);
        await service.launch(path.join(directory, 'Publish.csproj'), 'publish', true);
        await service.launch(path.join(directory, 'Do.csproj'), 'do', true, 'deploy');

        const configs = startDebuggingStub.getCalls()
            .map(call => call.args[1] as AspireExtendedDebugConfiguration)
            .map(config => ({ command: config.command, args: config.args, step: config.step }));
        assert.deepStrictEqual(configs, [
            { command: 'run', args: undefined, step: undefined },
            { command: 'deploy', args: undefined, step: undefined },
            { command: 'publish', args: undefined, step: undefined },
            { command: 'do', args: undefined, step: 'deploy' },
        ]);
    });

    test('launch argument preparation honors explicit --isolated in a linked worktree', async () => {
        const directory = createAppHostDirectory('AppHost.csproj');
        fs.rmSync(path.join(directory, '.git'), { recursive: true, force: true });
        writeLinkedWorktreeMetadata(directory, path.join(directory, 'common', '.git'));
        const appHostPath = path.join(directory, 'AppHost.csproj');
        const cancellation = new vscode.CancellationTokenSource();

        const prepared = await (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
            appHostPath,
            'run',
            ['--isolated'],
            cancellation.token,
            '/path/bin/aspire');

        assert.deepStrictEqual(prepared, {
            args: ['--isolated'],
            isolation: { effective: true, option: true },
        });
    });

    test('launch argument capability probes use the target workspace folder', async () => {
        const folder = { name: 'target', index: 1, uri: vscode.Uri.file('/repo/target') } as vscode.WorkspaceFolder;
        const target = workspaceFolderCliPathTarget(folder);

        await (service as unknown as LaunchArgumentPreparer).prepareLaunchArguments(
            '/repo/target/AppHost.csproj',
            'run',
            ['--isolated'],
            new vscode.CancellationTokenSource().token,
            '/path/bin/aspire',
            target);

        assert.strictEqual(
            (capabilityProvider.calls.at(-1)?.options as { target?: typeof target } | undefined)?.target,
            target);
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

    test('CLI availability probe resolves the target from the AppHost path workspace folder', async () => {
        const folder = { name: 'a', index: 0, uri: vscode.Uri.file('/repo') } as vscode.WorkspaceFolder;
        const getWorkspaceFolderStub = sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(folder);

        try {
            await service.launch('/repo/AppHost.csproj', 'run', true);

            assert.ok(resolveCliPathStub.calledOnceWith(workspaceFolderCliPathTarget(folder)));
        }
        finally {
            getWorkspaceFolderStub.restore();
        }
    });

    test('CLI availability probe falls back to the window target when no folder owns the AppHost path', async () => {
        const getWorkspaceFolderStub = sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);

        try {
            await service.launch('/outside/AppHost.csproj', 'run', true);

            assert.ok(resolveCliPathStub.calledOnceWith(windowCliPathTarget));
        }
        finally {
            getWorkspaceFolderStub.restore();
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

    test('refuses an external launch claim while a lifecycle operation owns the AppHost', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');
        let releaseOperation: (() => void) | undefined;
        let signalOperationStarted: (() => void) | undefined;
        const operationStarted = new Promise<void>(resolve => { signalOperationStarted = resolve; });
        const operation = service.runWithAppHostLifecycleLock(
            projectPath,
            new vscode.CancellationTokenSource().token,
            () => new Promise<void>(resolve => {
                releaseOperation = resolve;
                signalOperationStarted?.();
            }));
        await operationStarted;

        assert.strictEqual(service.tryReserveExternalLaunch(path.join(directory, 'Program.cs')), false);

        releaseOperation?.();
        await operation;
    });

    test('refuses an ambiguous external launch claim while a lifecycle operation is active', async () => {
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj', 'Program.cs');
        let releaseOperation: (() => void) | undefined;
        let signalOperationStarted: (() => void) | undefined;
        const operationStarted = new Promise<void>(resolve => { signalOperationStarted = resolve; });
        const operation = service.runWithAppHostLifecycleLock(
            path.join(directory, 'First.csproj'),
            new vscode.CancellationTokenSource().token,
            () => new Promise<void>(resolve => {
                releaseOperation = resolve;
                signalOperationStarted?.();
            }));
        await operationStarted;

        assert.strictEqual(service.tryReserveExternalLaunch(path.join(directory, 'Program.cs')), false);

        releaseOperation?.();
        await operation;
    });

    test('refuses an external launch claim while an editor run session owns the AppHost', () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');
        service.setEditorSessionProvider(() => [{
            appHostPath: projectPath,
            resolvedAppHostPath: projectPath,
            operationKind: 'run',
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { },
        }]);

        assert.strictEqual(service.tryReserveExternalLaunch(path.join(directory, 'Program.cs')), false);
    });

    test('refuses an external launch claim when an editor session association is ambiguous', () => {
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj', 'Program.cs');
        service.setEditorSessionProvider(() => [{
            appHostPath: path.join(directory, 'Program.cs'),
            resolvedAppHostPath: undefined,
            operationKind: 'run',
            startupCompleted: true,
            configuration: { noDebug: true },
            stopDebugging: async () => { },
        }]);

        assert.strictEqual(service.tryReserveExternalLaunch(path.join(directory, 'First.csproj')), false);
    });

    test('allows an external launch claim for an unrelated AppHost', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const otherDirectory = createAppHostDirectory('AppHost.csproj', 'Program.cs');

        assert.strictEqual(service.tryReserveLaunch(path.join(directory, 'AppHost.csproj')), true);

        assert.strictEqual(typeof service.tryReserveExternalLaunch(path.join(otherDirectory, 'AppHost.csproj')), 'string');
    });

    test('allows an external launch claim after the lifecycle claim is cleared', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');

        assert.strictEqual(service.tryReserveLaunch(projectPath), true);
        service.clearLaunching(projectPath);

        assert.strictEqual(typeof service.tryReserveExternalLaunch(projectPath), 'string');
    });

    test('an external launch claim blocks a distinct overlapping external launch', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');

        assert.strictEqual(typeof service.tryReserveExternalLaunch(projectPath), 'string');

        assert.strictEqual(service.tryReserveExternalLaunch(projectPath), false);
    });

    test('a repeated external reservation refreshes its pending expiry', () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
            const projectPath = path.join(directory, 'AppHost.csproj');
            const reservationId = service.tryReserveExternalLaunch(projectPath);
            assert.strictEqual(typeof reservationId, 'string');
            clock.tick(externalLaunchReservationTimeoutMs - 1);

            const refreshedReservationId = service.validateOrReacquireExternalLaunchReservation(projectPath, reservationId || '');

            assert.strictEqual(refreshedReservationId, reservationId);
            clock.tick(2);
            assert.strictEqual(service.isLaunching(projectPath), true);
            clock.tick(externalLaunchReservationTimeoutMs);
            assert.strictEqual(service.isLaunching(projectPath), false);
        }
        finally {
            clock.restore();
        }
    });

    test('an expired repeated external reservation reacquires a fresh launch generation', () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
            const projectPath = path.join(directory, 'AppHost.csproj');
            const expiredReservationId = service.tryReserveExternalLaunch(projectPath);
            assert.strictEqual(typeof expiredReservationId, 'string');
            clock.tick(externalLaunchReservationTimeoutMs + 1);
            assert.strictEqual(service.isLaunching(projectPath), false);

            const reacquiredReservationId = service.validateOrReacquireExternalLaunchReservation(projectPath, expiredReservationId || '');

            assert.strictEqual(typeof reacquiredReservationId, 'string');
            assert.notStrictEqual(reacquiredReservationId, expiredReservationId);
            assert.strictEqual(service.isLaunching(projectPath), true);
        }
        finally {
            clock.restore();
        }
    });

    test('a stale repeated external reservation does not replace a newer claimant', () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
            const projectPath = path.join(directory, 'AppHost.csproj');
            const staleReservationId = service.tryReserveExternalLaunch(projectPath);
            assert.strictEqual(typeof staleReservationId, 'string');
            clock.tick(externalLaunchReservationTimeoutMs + 1);
            const currentReservationId = service.tryReserveExternalLaunch(projectPath);
            assert.strictEqual(typeof currentReservationId, 'string');

            const repeatedReservationId = service.validateOrReacquireExternalLaunchReservation(projectPath, staleReservationId || '');

            assert.strictEqual(repeatedReservationId, false);
            service.releaseExternalLaunchReservation(projectPath, staleReservationId || '');
            assert.strictEqual(service.isLaunching(projectPath), true);
            service.releaseExternalLaunchReservation(projectPath, currentReservationId || '');
            assert.strictEqual(service.isLaunching(projectPath), false);
        }
        finally {
            clock.restore();
        }
    });

    test('an unresolved workspace launch blocks lifecycle operations for AppHosts inside it', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');

        assert.strictEqual(typeof service.tryReserveExternalLaunch(directory, true), 'string');

        assert.strictEqual(service.isLaunching(projectPath), true);
        assert.strictEqual(service.tryReserveLaunch(projectPath), false);
        assert.deepStrictEqual(await service.stopAppHost(projectPath), {
            outcome: 'alreadyStarting',
            controller: 'editor',
        });
    });

    test('an unresolved workspace launch loses to an existing concrete launch inside it', () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');

        assert.strictEqual(service.tryReserveLaunch(projectPath), true);

        assert.strictEqual(service.tryReserveExternalLaunch(directory, true), false);
    });

    test('a started unresolved workspace reservation persists until its concrete AppHost appears', () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
            const projectPath = path.join(directory, 'AppHost.csproj');
            const reservationId = service.tryReserveExternalLaunch(directory, true);
            assert.strictEqual(typeof reservationId, 'string');

            assert.ok(onDidStartDebugSessionCallback);
            onDidStartDebugSessionCallback({
                id: 'workspace-launch',
                configuration: {
                    type: 'aspire',
                    program: directory,
                    command: 'run',
                    [appHostLaunchReservationIdConfigKey]: reservationId,
                },
            } as unknown as vscode.DebugSession);
            clock.tick(externalLaunchReservationTimeoutMs + 1);

            assert.strictEqual(service.isLaunching(projectPath), true);

            service.setEditorSessionProvider(() => [{
                appHostPath: directory,
                resolvedAppHostPath: projectPath,
                operationKind: 'run',
                startupCompleted: true,
                configuration: {
                    [appHostLaunchReservationIdConfigKey]: reservationId,
                },
                stopDebugging: async () => { },
            }]);
            service.clearLaunchingForRunningAppHost(projectPath);

            assert.strictEqual(service.isLaunching(projectPath), false);
        }
        finally {
            clock.restore();
        }
    });

    test('replacing an external launch reservation releases the previous AppHost immediately', () => {
        const firstDirectory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const secondDirectory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const firstPath = path.join(firstDirectory, 'AppHost.csproj');
        const secondPath = path.join(secondDirectory, 'AppHost.csproj');
        const firstReservationId = service.tryReserveExternalLaunch(firstPath);
        assert.strictEqual(typeof firstReservationId, 'string');

        const secondReservationId = service.replaceExternalLaunchReservation(firstPath, firstReservationId || '', secondPath);

        assert.strictEqual(typeof secondReservationId, 'string');
        assert.notStrictEqual(secondReservationId, firstReservationId);
        assert.strictEqual(service.isLaunching(firstPath), false);
        assert.strictEqual(service.isLaunching(secondPath), true);
    });

    test('a refused external launch replacement still releases the previous AppHost', () => {
        const firstDirectory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const secondDirectory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const firstPath = path.join(firstDirectory, 'AppHost.csproj');
        const secondPath = path.join(secondDirectory, 'AppHost.csproj');
        const firstReservationId = service.tryReserveExternalLaunch(firstPath);
        assert.strictEqual(typeof firstReservationId, 'string');
        service.setEditorSessionProvider(() => [{
            appHostPath: secondPath,
            resolvedAppHostPath: secondPath,
            operationKind: 'run',
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { },
        }]);

        const replacement = service.replaceExternalLaunchReservation(firstPath, firstReservationId || '', secondPath);

        assert.strictEqual(replacement, false);
        assert.strictEqual(service.isLaunching(firstPath), false);
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

    test('a refused overlapping external launch does not extend the original reservation', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
            const projectPath = path.join(directory, 'AppHost.csproj');

            assert.strictEqual(typeof service.tryReserveExternalLaunch(projectPath), 'string');
            clock.tick(externalLaunchReservationTimeoutMs - 1);
            assert.strictEqual(service.tryReserveExternalLaunch(projectPath), false);

            clock.tick(2);

            assert.strictEqual(service.isLaunching(projectPath), false);
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

            assert.strictEqual(typeof service.tryReserveExternalLaunch(projectPath), 'string');
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

            assert.strictEqual(typeof service.tryReserveExternalLaunch(projectPath), 'string');

            clock.tick(externalLaunchReservationTimeoutMs + 1);

            assert.strictEqual(service.isLaunching(projectPath), false);
        }
        finally {
            clock.restore();
        }
    });

    test('a stale launch completion does not clear a newer reservation', () => {
        const projectPath = '/repo/AppHost/AppHost.csproj';
        const clearReservation = service.clearMatchingLaunching.bind(service) as (appHostPath: string, reservationId: unknown) => void;
        const firstReservation = service.tryReserveExternalLaunch(projectPath);
        clearReservation(projectPath, firstReservation);
        const secondReservation = service.tryReserveExternalLaunch(projectPath);

        clearReservation(projectPath, firstReservation);

        assert.strictEqual(typeof firstReservation, 'string');
        assert.strictEqual(typeof secondReservation, 'string');
        assert.notStrictEqual(secondReservation, firstReservation);
        assert.strictEqual(service.isLaunching(projectPath), true);
    });

    test('marks its own debug configurations so the shared resolver does not claim them as external', async () => {
        // `launchCore` reserves before `startDebugging`, and the configuration provider is
        // the same hook a `launch.json`/F5 launch goes through. Without the marker the
        // provider would refuse the launch against the caller's own claim. The marker has
        // to be a per-activation token rather than a forgeable launch.json boolean.
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');

        await service.launch(projectPath, 'run', true);

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(isAspireDebugConfigurationExtensionOwned(config), true);
        assert.notStrictEqual(config.launchedByExtension, true);
        assert.strictEqual(config.__aspireAppHostSelectionOrigin, 'user-selection');
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

    test('clears a cancelled lifecycle waiter when the active operation settles', async () => {
        const appHostPath = '/repo/AppHost/AppHost.csproj';
        let releaseActive: (() => void) | undefined;
        let signalActiveStarted: (() => void) | undefined;
        const activeStarted = new Promise<void>(resolve => { signalActiveStarted = resolve; });
        const active = service.runWithAppHostLifecycleLock(
            appHostPath,
            new vscode.CancellationTokenSource().token,
            () => new Promise<void>(resolve => {
                releaseActive = resolve;
                signalActiveStarted?.();
            }));
        await activeStarted;
        const queuedTokenSource = new vscode.CancellationTokenSource();
        const queued = service.runWithAppHostLifecycleLock(appHostPath, queuedTokenSource.token, async () => undefined);
        queuedTokenSource.cancel();
        await assert.rejects(queued, vscode.CancellationError);

        releaseActive?.();
        await active;

        const reservationId = service.tryReserveExternalOperation(appHostPath, 'deploy', true);
        assert.strictEqual(typeof reservationId, 'string');
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

    test('launch aborts after waiting when another launch claimed the AppHost', async () => {
        let releaseActive: (() => void) | undefined;
        let signalActiveStarted: (() => void) | undefined;
        const activeStarted = new Promise<void>(resolve => { signalActiveStarted = resolve; });
        try {
            assert.strictEqual(typeof service.tryReserveExternalLaunch('/repo/AppHost/AppHost.csproj'), 'string');
            const active = service.runWithAppHostLifecycleLock(
                '/repo/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token,
                () => new Promise<void>(resolve => {
                    releaseActive = resolve;
                    signalActiveStarted?.();
                }));
            await activeStarted;

            const blockedLaunch = service.launch('/repo/AppHost/AppHost.csproj', 'run', true);

            releaseActive?.();
            await assert.rejects(blockedLaunch, vscode.CancellationError);
            await active;

            assert.strictEqual(startDebuggingStub.called, false);
        }
        finally {
            releaseActive?.();
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

    test('queues behind every active lock a directory mutation merges into one identity', async () => {
        // `Second.csproj` makes `First.csproj` and `Program.cs` ambiguous, so operations for them
        // are independent identities and hold separate locks. Removing it merges the two into a
        // single identity whose path keys span both active locks. Queueing behind only one of them
        // would run the merged operation beside the other, which is the exclusivity this lock exists
        // to provide.
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj', 'Program.cs');
        const started: string[] = [];
        let releaseProject!: () => void;
        let releaseSource!: () => void;
        let signalProjectStarted!: () => void;
        let signalSourceStarted!: () => void;
        const projectStarted = new Promise<void>(resolve => { signalProjectStarted = resolve; });
        const sourceStarted = new Promise<void>(resolve => { signalSourceStarted = resolve; });

        const projectOperation = service.runWithAppHostLifecycleLock(
            path.join(directory, 'First.csproj'),
            new vscode.CancellationTokenSource().token,
            async () => {
                started.push('project');
                signalProjectStarted();
                await new Promise<void>(resolve => { releaseProject = resolve; });
                return 'project';
            });
        const sourceOperation = service.runWithAppHostLifecycleLock(
            path.join(directory, 'Program.cs'),
            new vscode.CancellationTokenSource().token,
            async () => {
                started.push('source');
                signalSourceStarted();
                await new Promise<void>(resolve => { releaseSource = resolve; });
                return 'source';
            });
        await Promise.all([projectStarted, sourceStarted]);

        fs.rmSync(path.join(directory, 'Second.csproj'));

        const merged = service.runWithAppHostLifecycleLock(
            path.join(directory, 'First.csproj'),
            new vscode.CancellationTokenSource().token,
            async () => {
                started.push('merged');
                return 'merged';
            });

        await new Promise<void>(resolve => setTimeout(resolve, 0));
        assert.deepStrictEqual(started, ['project', 'source']);

        releaseProject();
        await new Promise<void>(resolve => setTimeout(resolve, 0));
        assert.deepStrictEqual(started, ['project', 'source'], 'The merged operation must still wait for the other active lock');

        releaseSource();
        assert.deepStrictEqual(await Promise.all([projectOperation, sourceOperation, merged]), ['project', 'source', 'merged']);
        assert.deepStrictEqual(started, ['project', 'source', 'merged']);
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
            configuration: { noDebug: true },
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

    test('treats differently-cased paths to one existing AppHost as the same identity', function () {
        const directory = createAppHostDirectory('AppHost.csproj');
        const appHostPath = path.join(directory, 'AppHost.csproj');
        const differentlyCasedPath = path.join(directory, 'apphost.CSPROJ');
        if (!fs.existsSync(differentlyCasedPath)) {
            this.skip();
            return;
        }

        assert.strictEqual(service.compareAppHostIdentity(differentlyCasedPath, appHostPath), 'same');
        assert.strictEqual(getAppHostIdentityKey(differentlyCasedPath), getAppHostIdentityKey(appHostPath));
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

    test('uses an AppHost child identity while preserving its parent as the stop owner', async () => {
        const directory = createAppHostDirectory('AppHost.csproj');
        const appHostPath = path.join(directory, 'AppHost.csproj');
        let parentStopCount = 0;
        const parentSession = {
            appHostPath,
            resolvedAppHostPath: undefined,
            operationKind: 'run' as const,
            startupCompleted: true,
            configuration: { noDebug: true },
            stopDebugging: async () => { parentStopCount++; },
        };
        let editorSessions = [parentSession];
        let childStopCount = 0;
        const childDebugSession = {
            id: 'apphost-child',
            session: { id: 'apphost-child', configuration: { noDebug: false } } as unknown as vscode.DebugSession,
            stopSession: async () => { childStopCount++; },
        } satisfies AspireResourceDebugSession;
        service.setEditorSessionProvider(() => editorSessions);
        service.trackAppHostDebugSession(parentSession, appHostPath, childDebugSession);
        assert.deepStrictEqual(service.getEditorRunSessions(appHostPath), { sessions: [parentSession], ambiguous: false });

        editorSessions = [];
        const orphaned = service.getEditorRunSessions(appHostPath);

        assert.strictEqual(orphaned.sessions.length, 1);
        assert.strictEqual(orphaned.sessions[0].configuration.noDebug, true);
        await orphaned.sessions[0].stopDebugging();
        assert.strictEqual(parentStopCount, 1);
        assert.strictEqual(childStopCount, 0);
        assert.strictEqual(stopDebuggingStub.called, false);

        onDidTerminateDebugSessionCallback?.(childDebugSession.session);
        assert.deepStrictEqual(service.getEditorRunSessions(appHostPath), { sessions: [], ambiguous: false });
    });

    test('uses a concrete tracked child identity when its live parent has no resolved AppHost', async () => {
        const directory = createAppHostDirectory('AppHost.csproj');
        const appHostPath = path.join(directory, 'AppHost.csproj');
        let parentStopCount = 0;
        let childStopCount = 0;
        const parentSession: AppHostLaunchSession = {
            appHostPath: directory,
            resolvedAppHostPath: undefined,
            operationKind: 'run',
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { parentStopCount++; },
        };
        const childDebugSession = {
            id: 'concrete-apphost-child',
            session: { id: 'concrete-apphost-child', configuration: { noDebug: false } } as unknown as vscode.DebugSession,
            stopSession: async () => { childStopCount++; },
        } satisfies AspireResourceDebugSession;
        service.setEditorSessionProvider(() => [parentSession]);
        service.trackAppHostDebugSession(parentSession, appHostPath, childDebugSession);

        const result = await service.stopAppHost(appHostPath, new vscode.CancellationTokenSource().token);

        assert.deepStrictEqual(result, { outcome: 'stopped', controller: 'editor', noDebug: false });
        assert.strictEqual(parentStopCount, 1);
        assert.strictEqual(childStopCount, 0);
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

    test('stops an externally running AppHost through the shared lifecycle service', async () => {
        const appHostPath = path.resolve('/repo/AppHost/AppHost.csproj');
        const stopRequests: string[] = [];
        service.setRunningAppHostProvider(async () => [{ appHostPath }]);
        service.setExternalAppHostStopper(async requestedPath => { stopRequests.push(requestedPath); });

        const result = await service.stopAppHost(appHostPath, new vscode.CancellationTokenSource().token);

        assert.deepStrictEqual(result, { outcome: 'stopped', controller: 'external' });
        assert.deepStrictEqual(stopRequests, [appHostPath]);
    });

    test('refuses to stop an external AppHost whose identity is ambiguous', async () => {
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj', 'Program.cs');
        const requestedPath = path.join(directory, 'First.csproj');
        const stopRequests: string[] = [];
        service.setRunningAppHostProvider(async () => [{ appHostPath: path.join(directory, 'Program.cs') }]);
        service.setExternalAppHostStopper(async requestedPath => { stopRequests.push(requestedPath); });

        const result = await service.stopAppHost(requestedPath, new vscode.CancellationTokenSource().token);

        assert.deepStrictEqual(result, { outcome: 'ambiguousAppHost', controller: 'external' });
        assert.deepStrictEqual(stopRequests, []);
    });

    test('uses an editor session that appears while external running state is queried', async () => {
        const appHostPath = path.resolve('/repo/AppHost/AppHost.csproj');
        let editorSessions: AppHostLaunchSession[] = [];
        let editorStopCount = 0;
        const stopRequests: string[] = [];
        const session: AppHostLaunchSession = {
            appHostPath,
            resolvedAppHostPath: appHostPath,
            operationKind: 'run',
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { editorStopCount++; },
        };
        service.setEditorSessionProvider(() => editorSessions);
        service.setRunningAppHostProvider(async () => {
            editorSessions = [session];
            return [{ appHostPath }];
        });
        service.setExternalAppHostStopper(async requestedPath => { stopRequests.push(requestedPath); });

        const result = await service.stopAppHost(appHostPath, new vscode.CancellationTokenSource().token);

        assert.deepStrictEqual(result, { outcome: 'stopped', controller: 'editor', noDebug: false });
        assert.strictEqual(editorStopCount, 1);
        assert.deepStrictEqual(stopRequests, []);
    });

    test('preserves the editor controller and mode when stopping is cancelled', async () => {
        const appHostPath = path.resolve('/repo/AppHost/AppHost.csproj');
        const session: AppHostLaunchSession = {
            appHostPath,
            resolvedAppHostPath: appHostPath,
            operationKind: 'run',
            startupCompleted: true,
            configuration: { noDebug: false },
            stopDebugging: async () => { throw new vscode.CancellationError(); },
        };
        service.setEditorSessionProvider(() => [session]);

        await assert.rejects(
            service.stopAppHost(appHostPath, new vscode.CancellationTokenSource().token),
            error => error instanceof AppHostStopCancellationError &&
                error instanceof vscode.CancellationError &&
                error.controller === 'editor' &&
                error.noDebug === false);
    });

    test('service disposal cancels an external stop that supplied its own token', async () => {
        const appHostPath = path.resolve('/repo/AppHost/AppHost.csproj');
        let signalStopStarted: (() => void) | undefined;
        const stopStarted = new Promise<void>(resolve => { signalStopStarted = resolve; });
        service.setRunningAppHostProvider(async () => [{ appHostPath }]);
        service.setExternalAppHostStopper(async (_requestedPath, token) => {
            signalStopStarted?.();
            await new Promise<void>((_resolve, reject) => {
                token.onCancellationRequested(() => reject(new vscode.CancellationError()));
            });
        });

        const stopping = service.stopAppHost(appHostPath, new vscode.CancellationTokenSource().token);
        await stopStarted;
        service.dispose();

        await assert.rejects(stopping, error => error instanceof vscode.CancellationError);
    });

    test('rechecks external running state after a concurrent stop releases the lifecycle lock', async () => {
        const appHostPath = path.resolve('/repo/AppHost/AppHost.csproj');
        let running = true;
        let resolveFirstStop: (() => void) | undefined;
        const stopRequests: string[] = [];
        service.setRunningAppHostProvider(async () => running ? [{ appHostPath }] : []);
        service.setExternalAppHostStopper(async requestedPath => {
            stopRequests.push(requestedPath);
            if (stopRequests.length === 1) {
                await new Promise<void>(resolve => { resolveFirstStop = resolve; });
                running = false;
            }
        });
        const token = new vscode.CancellationTokenSource().token;

        const firstStop = service.stopAppHost(appHostPath, token);
        const secondStop = service.stopAppHost(appHostPath, token);
        await new Promise(resolve => setImmediate(resolve));
        resolveFirstStop?.();

        assert.deepStrictEqual(await firstStop, { outcome: 'stopped', controller: 'external' });
        assert.deepStrictEqual(await secondStop, { outcome: 'notRunning', controller: 'none' });
        assert.deepStrictEqual(stopRequests, [appHostPath]);
    });

    test('case-distinct Windows AppHosts have independent launching state', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').callsFake((filePath: fs.PathLike) => ({
            dev: 1n,
            ino: path.basename(path.dirname(String(filePath))) === 'AppHost' ? 100n : 101n,
        }) as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/apphost.mts';
        const lowerCasePath = '/workspace/apphost/apphost.mts';

        try {
            await service.launch(upperCasePath, 'run', true);

            assert.strictEqual(service.isLaunching(upperCasePath), true);
            assert.strictEqual(service.isLaunching(lowerCasePath), false);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('stale termination does not clear a newer equivalent launch', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';

        try {
            await service.launch(upperCasePath, 'run', true);
            const staleConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
            service.clearLaunching(upperCasePath);

            await service.launch(lowerCasePath, 'run', true);
            const currentConfiguration = startDebuggingStub.secondCall.args[1] as AspireExtendedDebugConfiguration;

            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback({
                configuration: staleConfiguration,
            } as unknown as vscode.DebugSession);
            assert.deepStrictEqual(service.launchingPaths, [path.resolve(lowerCasePath)]);

            onDidTerminateDebugSessionCallback({
                configuration: currentConfiguration,
            } as unknown as vscode.DebugSession);
            assert.deepStrictEqual(service.launchingPaths, []);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('untokened termination does not clear service-owned launching state', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'run', true);

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: appHostPath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession);
        assert.deepStrictEqual(service.launchingPaths, [path.resolve(appHostPath)]);
        assert.deepStrictEqual(service.launchingPaths, [path.resolve(appHostPath)]);
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
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean } | undefined;
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
            shouldMarkAppHostStopping: true,
        });
    });

    test('terminated F5 sessions clear the reservation for their resolved AppHost', () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');
        const reservationId = service.tryReserveExternalLaunch(projectPath);
        assert.strictEqual(typeof reservationId, 'string');

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: directory,
                command: 'run',
                [appHostTelemetryTargetPathConfigKey]: projectPath,
                [appHostLaunchReservationIdConfigKey]: reservationId,
            },
        } as unknown as vscode.DebugSession);

        assert.strictEqual(service.isLaunching(projectPath), false);
    });

    test('a stale terminated F5 session does not clear a newer reservation', () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');
        let terminationEvent: { shouldRequestStopRefresh: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });
        const staleReservationId = service.tryReserveExternalLaunch(projectPath);
        assert.strictEqual(typeof staleReservationId, 'string');
        service.clearMatchingLaunching(projectPath, staleReservationId || undefined);
        const currentReservationId = service.tryReserveExternalLaunch(projectPath);
        assert.strictEqual(typeof currentReservationId, 'string');

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: directory,
                command: 'run',
                [appHostTelemetryTargetPathConfigKey]: projectPath,
                [appHostLaunchReservationIdConfigKey]: staleReservationId,
            },
        } as unknown as vscode.DebugSession);

        assert.strictEqual(service.isLaunching(projectPath), true);
        assert.strictEqual(terminationEvent?.shouldRequestStopRefresh, false);
    });

    test('a stale project-path termination does not refresh stop state for a source-path replacement', () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');
        const sourcePath = path.join(directory, 'Program.cs');
        let terminationEvent: { shouldRequestStopRefresh: boolean } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });
        const staleReservationId = service.tryReserveExternalLaunch(projectPath);
        assert.strictEqual(typeof staleReservationId, 'string');
        service.clearMatchingLaunching(projectPath, staleReservationId || undefined);
        const currentReservationId = service.tryReserveExternalLaunch(sourcePath);
        assert.strictEqual(typeof currentReservationId, 'string');

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            configuration: {
                type: 'aspire',
                program: projectPath,
                command: 'run',
                [appHostLaunchReservationIdConfigKey]: staleReservationId,
            },
        } as unknown as vscode.DebugSession);

        assert.strictEqual(service.isLaunching(sourcePath), true);
        assert.strictEqual(terminationEvent?.shouldRequestStopRefresh, false);
    });

    test('toolbar restart does not mark the replacement AppHost as stopping', () => {
        let terminationEvent: {
            appHostPath: string;
            command?: string;
            shouldRequestStopRefresh: boolean;
            shouldMarkAppHostStopping: boolean;
        } | undefined;
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvent = event;
        });
        const sessionId = 'aspire-session';

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback({
            id: sessionId,
            configuration: {
                type: 'aspire',
                program: '/repo/AppHost.csproj',
                command: 'run',
                __aspireAppHostRestartSourceSessionId: sessionId,
            },
        } as unknown as vscode.DebugSession);

        assert.deepStrictEqual(terminationEvent, {
            appHostPath: '/repo/AppHost.csproj',
            command: 'run',
            shouldRequestStopRefresh: true,
            shouldMarkAppHostStopping: false,
        });
    });

    test('terminating one of multiple equivalent run sessions does not mark the survivor as stopping', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').callsFake((filePath: fs.PathLike) => ({
            dev: 1n,
            ino: path.extname(String(filePath)) === '.csproj' ? 100n : 50n,
        }) as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';
        const terminationEvents: Array<{
            appHostPath: string;
            command?: string;
            shouldRequestStopRefresh: boolean;
            shouldMarkAppHostStopping: boolean;
        }> = [];
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvents.push(event);
        });
        const workspaceRootSession = {
            id: 'upper',
            configuration: {
                type: 'aspire',
                program: '/workspace',
                command: 'run',
                [appHostTelemetryTargetPathConfigKey]: upperCasePath,
            },
        } as unknown as vscode.DebugSession;
        const lowerCaseSession = {
            id: 'lower',
            configuration: {
                type: 'aspire',
                program: lowerCasePath,
                command: 'run',
            },
        } as unknown as vscode.DebugSession;

        try {
            assert.ok(onDidStartDebugSessionCallback);
            onDidStartDebugSessionCallback(workspaceRootSession);
            onDidStartDebugSessionCallback(lowerCaseSession);

            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback(workspaceRootSession);
            onDidTerminateDebugSessionCallback(lowerCaseSession);

            assert.deepStrictEqual(terminationEvents, [
                {
                    appHostPath: upperCasePath,
                    command: 'run',
                    shouldRequestStopRefresh: true,
                    shouldMarkAppHostStopping: false,
                },
                {
                    appHostPath: lowerCasePath,
                    command: 'run',
                    shouldRequestStopRefresh: true,
                    shouldMarkAppHostStopping: true,
                },
            ]);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('pending run is tracked before telemetry classification completes', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';
        const terminationEvents: Array<{
            appHostPath: string;
            command?: string;
            shouldRequestStopRefresh: boolean;
            shouldMarkAppHostStopping: boolean;
        }> = [];
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvents.push(event);
        });

        try {
            await service.launch(upperCasePath, 'run', true);
            const upperCaseConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
            const upperCaseSession = {
                id: 'upper',
                configuration: upperCaseConfiguration,
            } as unknown as vscode.DebugSession;
            assert.ok(onDidStartDebugSessionCallback);
            onDidStartDebugSessionCallback(upperCaseSession);
            service.clearLaunching(upperCasePath);

            const lowerCaseLaunchTask = service.launch(lowerCasePath, 'run', true);

            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback(upperCaseSession);
            await lowerCaseLaunchTask;

            assert.deepStrictEqual(terminationEvents, [{
                appHostPath: upperCasePath,
                command: 'run',
                shouldRequestStopRefresh: true,
                shouldMarkAppHostStopping: false,
            }]);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('pending equivalent run prevents a terminated session from marking it as stopping', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const statStub = sinon.stub(fs, 'statSync').returns({
            dev: 1n,
            ino: 100n,
        } as fs.BigIntStats);
        const upperCasePath = '/workspace/AppHost/AppHost.csproj';
        const lowerCasePath = '/workspace/apphost/apphost.csproj';
        const terminationEvents: Array<{
            appHostPath: string;
            command?: string;
            shouldRequestStopRefresh: boolean;
            shouldMarkAppHostStopping: boolean;
        }> = [];
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvents.push(event);
        });

        try {
            await service.launch(upperCasePath, 'run', true);
            const upperCaseConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
            const upperCaseSession = {
                id: 'upper',
                configuration: upperCaseConfiguration,
            } as unknown as vscode.DebugSession;
            assert.ok(onDidStartDebugSessionCallback);
            onDidStartDebugSessionCallback(upperCaseSession);
            service.clearLaunching(upperCasePath);

            await service.launch(lowerCasePath, 'run', true);
            const lowerCaseConfiguration = startDebuggingStub.secondCall.args[1] as AspireExtendedDebugConfiguration;
            const lowerCaseSession = {
                id: 'lower',
                configuration: lowerCaseConfiguration,
            } as unknown as vscode.DebugSession;
            service.clearLaunching(lowerCasePath);

            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback(upperCaseSession);
            onDidStartDebugSessionCallback(lowerCaseSession);
            onDidTerminateDebugSessionCallback(lowerCaseSession);

            assert.deepStrictEqual(terminationEvents, [
                {
                    appHostPath: upperCasePath,
                    command: 'run',
                    shouldRequestStopRefresh: false,
                    shouldMarkAppHostStopping: false,
                },
                {
                    appHostPath: lowerCasePath,
                    command: 'run',
                    shouldRequestStopRefresh: true,
                    shouldMarkAppHostStopping: true,
                },
            ]);
        } finally {
            statStub.restore();
            platformStub.restore();
        }
    });

    test('terminated non-run sessions do not request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean } | undefined;
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
            shouldMarkAppHostStopping: false,
        });
    });

    test('terminated Aspire sessions default missing command to run and request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean } | undefined;
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
            shouldMarkAppHostStopping: true,
        });
    });

    test('terminated Aspire sessions drop invalid command values and do not request stop refresh', () => {
        let terminationEvent: { appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean } | undefined;
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
            shouldMarkAppHostStopping: false,
        });
    });

    test('terminated Aspire sessions ignore non-string AppHost paths', () => {
        let terminationEventRaised = false;
        service.onDidTerminateAppHostDebugSession(() => {
            terminationEventRaised = true;
        });

        assert.ok(onDidTerminateDebugSessionCallback);
        assert.doesNotThrow(() => {
            onDidTerminateDebugSessionCallback?.({
                configuration: {
                    type: 'aspire',
                    program: { path: '/repo/AppHost.csproj' },
                    command: 'run',
                },
            } as unknown as vscode.DebugSession);
        });

        assert.strictEqual(terminationEventRaised, false);
    });

    test('a Run termination still requests stop refresh while a later Publish stays active', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        const terminationEvents: Array<{ command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean }> = [];
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvents.push(event);
        });

        await service.launch(appHostPath, 'run', true);
        const runConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        const runSession = { id: 'run', configuration: runConfiguration } as unknown as vscode.DebugSession;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback(runSession);
        service.clearLaunching(appHostPath);

        await service.launch(appHostPath, 'publish', true);
        const publishConfiguration = startDebuggingStub.secondCall.args[1] as AspireExtendedDebugConfiguration;
        const publishSession = { id: 'publish', configuration: publishConfiguration } as unknown as vscode.DebugSession;
        onDidStartDebugSessionCallback(publishSession);

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback(runSession);

        assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'publish');
        assert.deepStrictEqual(terminationEvents, [{
            appHostPath,
            command: 'run',
            shouldRequestStopRefresh: true,
            shouldMarkAppHostStopping: true,
        }]);
    });

    test('a non-Run launch reports a pending then active operation and clears on termination', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        let changeCount = 0;
        let signalCliResolutionStarted: (() => void) | undefined;
        let releaseCliResolution: (() => void) | undefined;
        const cliResolutionStarted = new Promise<void>(resolve => { signalCliResolutionStarted = resolve; });
        const cliResolutionRelease = new Promise<void>(resolve => { releaseCliResolution = resolve; });
        resolveCliPathStub.callsFake(async () => {
            signalCliResolutionStarted?.();
            await cliResolutionRelease;
            return { cliPath: '/path/bin/aspire', available: true, source: 'path' };
        });
        service.onDidChangeOperationState(() => { changeCount++; });

        const launch = service.launch(appHostPath, 'publish', true);
        await cliResolutionStarted;

        assert.deepStrictEqual(service.getActiveOperation(appHostPath), {
            appHostPath,
            command: 'publish',
            noDebug: true,
            doStep: undefined,
        });
        assert.strictEqual(changeCount, 1);
        assert.strictEqual(startDebuggingStub.called, false);

        releaseCliResolution?.();
        await launch;

        const publishConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        const publishSession = { id: 'publish', configuration: publishConfiguration } as unknown as vscode.DebugSession;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback(publishSession);

        assert.deepStrictEqual(service.getActiveOperation(appHostPath), {
            appHostPath,
            command: 'publish',
            noDebug: true,
            doStep: undefined,
        });
        assert.strictEqual(changeCount, 1, 'transferring a pending operation to its session is not observable');

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback(publishSession);

        assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
        assert.strictEqual(changeCount, 2);
    });

    test('an external non-Run reservation blocks duplicate operations but not Run', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        const reservationId = service.tryReserveExternalOperation(appHostPath, 'deploy', true, 'infra');
        assert.strictEqual(typeof reservationId, 'string');
        assert.deepStrictEqual(service.getActiveOperation(appHostPath), {
            appHostPath,
            command: 'deploy',
            noDebug: true,
            doStep: 'infra',
        });

        await assert.rejects(
            service.launch(appHostPath, 'publish', true),
            (error: unknown) => error instanceof vscode.CancellationError);
        await service.launch(appHostPath, 'run', true);

        const externalConfiguration: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            name: 'Deploy AppHost',
            request: 'launch',
            program: appHostPath,
            command: 'deploy',
            noDebug: true,
            step: 'infra',
            [appHostLaunchReservationIdConfigKey]: reservationId,
        };
        const externalSession = {
            id: 'external-deploy',
            configuration: externalConfiguration,
        } as unknown as vscode.DebugSession;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback(externalSession);

        assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'deploy');
        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback(externalSession);
        assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
    });

    test('an external non-Run reservation is rejected while a Run launch is pending', () => {
        const appHostPath = '/repo/AppHost.csproj';
        assert.strictEqual(service.tryReserveLaunch(appHostPath), true);

        assert.strictEqual(
            service.tryReserveExternalOperation(appHostPath, 'deploy', true, 'infra'),
            false);
        assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
    });

    test('a directory-scoped external non-Run reservation is rejected while a child AppHost launch is pending', () => {
        const directory = createAppHostDirectory('AppHost.csproj');
        const appHostPath = path.join(directory, 'AppHost.csproj');
        assert.strictEqual(service.tryReserveLaunch(appHostPath), true);

        assert.strictEqual(
            service.tryReserveExternalOperation(directory, 'deploy', true, undefined, true),
            false);
        assert.strictEqual(service.getActiveOperation(directory), undefined);
    });

    test('an external non-Run reservation is rejected while a lifecycle operation owns the AppHost', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        let releaseOperation: (() => void) | undefined;
        let signalOperationStarted: (() => void) | undefined;
        const operationStarted = new Promise<void>(resolve => { signalOperationStarted = resolve; });
        const operation = service.runWithAppHostLifecycleLock(
            appHostPath,
            new vscode.CancellationTokenSource().token,
            () => new Promise<void>(resolve => {
                releaseOperation = resolve;
                signalOperationStarted?.();
            }));
        await operationStarted;

        assert.strictEqual(
            service.tryReserveExternalOperation(appHostPath, 'publish', true),
            false);
        assert.strictEqual(service.getActiveOperation(appHostPath), undefined);

        releaseOperation?.();
        await operation;
        const reservationId = service.tryReserveExternalOperation(appHostPath, 'publish', true);
        assert.strictEqual(typeof reservationId, 'string');
        service.releaseExternalOperationReservation(appHostPath, reservationId || '');
    });

    test('an external non-Run reservation is allowed beside a stable Run', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'run', true);
        const configuration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback({
            id: 'stable-run',
            configuration,
        } as unknown as vscode.DebugSession);
        service.clearLaunching(appHostPath);

        const reservationId = service.tryReserveExternalOperation(appHostPath, 'deploy', true);

        assert.strictEqual(typeof reservationId, 'string');
        assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'deploy');
    });

    test('a directory-scoped external non-Run reservation blocks a child AppHost operation', async () => {
        const directory = createAppHostDirectory('AppHost.csproj');
        const appHostPath = path.join(directory, 'AppHost.csproj');
        const reservationId = service.tryReserveExternalOperation(directory, 'deploy', true, undefined, true);
        assert.strictEqual(typeof reservationId, 'string');
        assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'deploy');

        await assert.rejects(
            service.launch(appHostPath, 'publish', true),
            (error: unknown) => error instanceof vscode.CancellationError);
        assert.strictEqual(startDebuggingStub.called, false);
    });

    test('an abandoned external non-Run reservation expires', () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            const appHostPath = '/repo/AppHost.csproj';
            let changeCount = 0;
            service.onDidChangeOperationState(() => { changeCount++; });

            assert.strictEqual(
                typeof service.tryReserveExternalOperation(appHostPath, 'publish', true),
                'string');
            assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'publish');

            clock.tick(externalLaunchReservationTimeoutMs + 1);

            assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
            assert.strictEqual(changeCount, 2);
        }
        finally {
            clock.restore();
        }
    });

    test('a toolbar restart preserves a durable operation until the replacement terminates', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'do', false, 'test');
        const configuration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        const firstSession = {
            id: 'first-do',
            configuration,
        } as unknown as vscode.DebugSession;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback(firstSession);

        configuration[appHostRestartSourceSessionIdConfigKey] = firstSession.id;
        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback(firstSession);
        assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'do');

        const replacementSession = {
            id: 'replacement-do',
            configuration,
        } as unknown as vscode.DebugSession;
        onDidStartDebugSessionCallback(replacementSession);
        assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'do');

        delete configuration[appHostRestartSourceSessionIdConfigKey];
        onDidTerminateDebugSessionCallback(replacementSession);
        assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
    });

    test('a durable operation expires when its toolbar restart never starts a replacement', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'do', false, 'test');
        const configuration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        const session = {
            id: 'do',
            configuration,
        } as unknown as vscode.DebugSession;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback(session);

        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        try {
            configuration[appHostRestartSourceSessionIdConfigKey] = session.id;
            assert.ok(onDidTerminateDebugSessionCallback);
            onDidTerminateDebugSessionCallback(session);
            assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'do');

            clock.tick(externalLaunchReservationTimeoutMs + 1);

            assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
        }
        finally {
            clock.restore();
        }
    });

    test('an active operation matches its AppHost by identity, not just its raw path', async () => {
        const directory = createAppHostDirectory('AppHost.csproj', 'Program.cs');
        const projectPath = path.join(directory, 'AppHost.csproj');
        const sourcePath = path.join(directory, 'Program.cs');

        await service.launch(projectPath, 'deploy', false, 'infra');

        assert.deepStrictEqual(service.getActiveOperation(sourcePath), {
            appHostPath: projectPath,
            command: 'deploy',
            noDebug: false,
            doStep: 'infra',
        });
    });

    test('an ambiguous AppHost path does not claim either active operation but still blocks duplicates', async () => {
        const directory = createAppHostDirectory('First.csproj', 'Second.csproj', 'Program.cs');
        const firstProjectPath = path.join(directory, 'First.csproj');
        const secondProjectPath = path.join(directory, 'Second.csproj');
        const ambiguousSourcePath = path.join(directory, 'Program.cs');

        await service.launch(firstProjectPath, 'publish', true);
        const firstConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback({ id: 'first', configuration: firstConfiguration } as unknown as vscode.DebugSession);

        await service.launch(secondProjectPath, 'deploy', false, 'infra');
        const secondConfiguration = startDebuggingStub.secondCall.args[1] as AspireExtendedDebugConfiguration;
        onDidStartDebugSessionCallback({ id: 'second', configuration: secondConfiguration } as unknown as vscode.DebugSession);

        assert.strictEqual(service.getActiveOperation(firstProjectPath)?.command, 'publish');
        assert.strictEqual(service.getActiveOperation(secondProjectPath)?.command, 'deploy');
        assert.strictEqual(service.getActiveOperation(ambiguousSourcePath), undefined);
        await assert.rejects(
            service.launch(ambiguousSourcePath, 'publish', true),
            (error: unknown) => error instanceof vscode.CancellationError);
        assert.strictEqual(startDebuggingStub.calledTwice, true);
    });

    test('a duplicate non-Run operation is rejected while one is pending', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'publish', true);

        await assert.rejects(
            service.launch(appHostPath, 'deploy', true),
            (error: unknown) => error instanceof vscode.CancellationError);

        assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'publish');
        assert.strictEqual(startDebuggingStub.calledOnce, true);
    });

    test('a duplicate non-Run operation is rejected while one is active', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'publish', true);
        const publishConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback({ id: 'publish', configuration: publishConfiguration } as unknown as vscode.DebugSession);
        service.clearLaunching(appHostPath);

        await assert.rejects(
            service.launch(appHostPath, 'publish', true),
            (error: unknown) => error instanceof vscode.CancellationError);

        assert.strictEqual(startDebuggingStub.calledOnce, true);
    });

    test('a Run is not rejected while a non-Run operation is active', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'publish', true);
        const publishConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback({ id: 'publish', configuration: publishConfiguration } as unknown as vscode.DebugSession);

        await service.launch(appHostPath, 'run', true);

        assert.strictEqual(startDebuggingStub.calledTwice, true);
        assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'publish');
    });

    test('an external F5 launch is not rejected while a non-Run operation is active', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'publish', true);
        const publishConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback({ id: 'publish', configuration: publishConfiguration } as unknown as vscode.DebugSession);

        assert.strictEqual(typeof service.tryReserveExternalLaunch(appHostPath), 'string');
        assert.strictEqual(service.getActiveOperation(appHostPath)?.command, 'publish');
    });

    test('a declined non-Run launch clears its pending operation', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        startDebuggingStub.resolves(false);

        await assert.rejects(service.launch(appHostPath, 'publish', true));

        assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
    });

    test('a cancelled non-Run launch clears its pending operation', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        startDebuggingStub.rejects(new vscode.CancellationError());

        await assert.rejects(service.launch(appHostPath, 'publish', true));

        assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
    });

    test('a suppressed non-Run launch clears its pending operation', async () => {
        const environmentVariables = [
            'ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE',
            'ASPIRE_EXTENSION_E2E_STATE_FILE',
            'ASPIRE_EXTENSION_E2E_CONTROL_FILE',
            'ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH',
        ] as const;
        const originalValues = new Map(environmentVariables.map(name => [name, process.env[name]]));
        const appHostPath = '/repo/AppHost.csproj';

        try {
            process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE = 'true';
            process.env.ASPIRE_EXTENSION_E2E_STATE_FILE = 'state.json';
            process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE = 'control.json';
            process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_DEBUG_LAUNCH = 'true';

            await service.launch(appHostPath, 'publish', true);

            assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
            assert.strictEqual(startDebuggingStub.called, false);
        }
        finally {
            for (const [name, value] of originalValues) {
                if (value === undefined) {
                    delete process.env[name];
                }
                else {
                    process.env[name] = value;
                }
            }
        }
    });

    test('disposal clears active operation state', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        await service.launch(appHostPath, 'publish', true);
        assert.ok(service.getActiveOperation(appHostPath));

        service.dispose();

        assert.strictEqual(service.getActiveOperation(appHostPath), undefined);
    });

    test('a Publish launched while a Run is active does not advance the Run generation', async () => {
        const appHostPath = '/repo/AppHost.csproj';
        const terminationEvents: Array<{ appHostPath: string; command?: string; shouldRequestStopRefresh: boolean; shouldMarkAppHostStopping: boolean }> = [];
        service.onDidTerminateAppHostDebugSession(event => {
            terminationEvents.push(event);
        });

        await service.launch(appHostPath, 'run', true);
        const runConfiguration = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        const runSession = { id: 'run', configuration: runConfiguration } as unknown as vscode.DebugSession;
        assert.ok(onDidStartDebugSessionCallback);
        onDidStartDebugSessionCallback(runSession);
        service.clearLaunching(appHostPath);

        await service.launch(appHostPath, 'publish', true);
        const publishConfiguration = startDebuggingStub.secondCall.args[1] as AspireExtendedDebugConfiguration;
        const publishSession = { id: 'publish', configuration: publishConfiguration } as unknown as vscode.DebugSession;
        onDidStartDebugSessionCallback(publishSession);
        service.clearLaunching(appHostPath);

        // The distinct reservation IDs prove the Publish took its own launching slot rather
        // than reusing the Run's; the assertions below prove it did not steal the Run's
        // generation while doing so.
        assert.notStrictEqual(
            publishConfiguration[appHostLaunchReservationIdConfigKey],
            runConfiguration[appHostLaunchReservationIdConfigKey]);

        assert.ok(onDidTerminateDebugSessionCallback);
        onDidTerminateDebugSessionCallback(publishSession);
        onDidTerminateDebugSessionCallback(runSession);

        assert.deepStrictEqual(terminationEvents, [
            { appHostPath, command: 'publish', shouldRequestStopRefresh: false, shouldMarkAppHostStopping: false },
            { appHostPath, command: 'run', shouldRequestStopRefresh: true, shouldMarkAppHostStopping: true },
        ]);
    });
});
