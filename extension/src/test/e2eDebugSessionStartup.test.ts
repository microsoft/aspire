import * as assert from 'assert';
import fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import { waitForDebugSessionStartup } from '../test-e2e/helpers/assertions';
import type { AspireDebugSessionState, AspireExtensionE2EStateFile } from '../types/extensionApi';

suite('E2E debug AppHost startup wait', () => {
    const environmentNames = [
        'ASPIRE_EXTENSION_E2E_STATE_FILE',
        'ASPIRE_EXTENSION_E2E_PRIMARY_APPHOST',
        'ASPIRE_EXTENSION_E2E_RUN_ID',
    ] as const;
    let directory: string;
    let statePath: string;
    let previousEnvironment: Record<string, string | undefined>;
    let snapshot: AspireExtensionE2EStateFile;
    let appHostPath: string;

    setup(() => {
        previousEnvironment = Object.fromEntries(environmentNames.map(name => [name, process.env[name]]));
        directory = fs.mkdtempSync(path.join(fs.realpathSync(os.tmpdir()), 'aspire-e2e-debug-startup-'));
        statePath = path.join(directory, 'state.json');
        appHostPath = path.join(directory, 'AppHost.csproj');
        process.env.ASPIRE_EXTENSION_E2E_STATE_FILE = statePath;
        process.env.ASPIRE_EXTENSION_E2E_PRIMARY_APPHOST = appHostPath;
        process.env.ASPIRE_EXTENSION_E2E_RUN_ID = 'debug-startup';
        snapshot = {
            extensionHostSessionId: 'debug-startup',
            updatedAt: '2026-01-01T00:00:00Z',
            runId: 'debug-startup',
            state: {
                viewMode: 'workspace',
                isRepositoryLoading: false,
                isWorkspaceAppHostDiscoveryComplete: true,
                hasError: false,
                errorMessage: undefined,
                workspaceAppHost: undefined,
                workspaceAppHostName: 'AppHost',
                workspaceAppHostPath: appHostPath,
                workspaceAppHostCandidatePaths: [appHostPath],
                workspaceAppHostDescription: undefined,
                workspaceResources: [],
                appHosts: [],
                launchingPaths: [],
                stoppingPaths: [],
                debugSessions: [],
            },
            commandInvocations: [],
            terminalCommands: [],
            debugLaunches: [],
            debugConsoleOutputs: [],
            stoppingPathEvents: [],
            taskProcessEvents: [],
            browserDebugSessions: [],
        };
    });

    teardown(() => {
        for (const name of environmentNames) {
            const previousValue = previousEnvironment[name];
            if (previousValue === undefined) {
                delete process.env[name];
            }
            else {
                process.env[name] = previousValue;
            }
        }
        fs.rmSync(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    });

    test('fails fast when the observed debug session ends before startup completes', async () => {
        snapshot.debugLaunches = [launch(1)];
        snapshot.debugConsoleOutputs = [{
            sequence: 1,
            debugSessionId: 'session-1',
            appHostPath,
            category: 'stderr',
            output: 'Unhandled exception. System.IO.InvalidDataException: Service aspire-dashboard-otlp-grpc should have valid address at this point\n',
        }];
        writeState([startingSession()]);

        // The waiter reads its first snapshot synchronously before yielding to its polling delay.
        const pending = waitForDebugSessionStartup(appHostPath, 600000);
        writeState([]);

        await assert.rejects(pending, (error: Error) => {
            assert.ok(error.message.startsWith('Stopped waiting for debug AppHost startup after '), error.message);
            assert.ok(error.message.includes(`The debug session for AppHost '${appHostPath}' ended before startup completed.`), error.message);
            assert.ok(error.message.includes('[stderr] Unhandled exception. System.IO.InvalidDataException'), error.message);
            return true;
        });
    });

    test('keeps waiting when the AppHost is still launching after its session ends', async () => {
        snapshot.debugLaunches = [launch(1)];
        writeState([startingSession()]);

        const pending = waitForDebugSessionStartup(appHostPath, 1000);
        snapshot.state.launchingPaths = [appHostPath];
        writeState([]);

        await assertTimesOut(pending);
    });

    test('fails fast when the launching flag clears after the observed session ended', async () => {
        snapshot.debugLaunches = [launch(1)];
        writeState([startingSession()]);
        // Serve one snapshot per poll so the waiter deterministically sees the session, then the session gone while
        // the AppHost is still launching, then the launching flag cleared without a newer launch.
        const snapshots = [
            serialize([startingSession()], []),
            serialize([], [appHostPath]),
            serialize([], []),
        ];
        let reads = 0;
        const readFileSync = fs.readFileSync;
        const stub = sinon.stub(fs, 'readFileSync').callsFake(((file: fs.PathOrFileDescriptor, options?: unknown) =>
            file === statePath
                ? snapshots[Math.min(reads++, snapshots.length - 1)]
                : readFileSync(file, options as BufferEncoding)) as typeof fs.readFileSync);

        try {
            await assert.rejects(waitForDebugSessionStartup(appHostPath, 600000), (error: Error) => {
                assert.ok(error.message.includes(`The debug session for AppHost '${appHostPath}' ended before startup completed.`), error.message);
                return true;
            });
        }
        finally {
            stub.restore();
        }

        assert.strictEqual(reads, snapshots.length);
    });

    test('keeps waiting when a newer launch replaced the ended session', async () => {
        snapshot.debugLaunches = [launch(1)];
        writeState([startingSession()]);

        const pending = waitForDebugSessionStartup(appHostPath, 1000);
        snapshot.debugLaunches = [launch(1), launch(2)];
        writeState([]);

        await assertTimesOut(pending);
    });

    test('keeps waiting for a debug session that has not appeared yet', async () => {
        writeState([]);

        await assertTimesOut(waitForDebugSessionStartup(appHostPath, 1000));
    });

    test('resolves once the debug session completes startup', async () => {
        writeState([startingSession()]);

        const pending = waitForDebugSessionStartup(appHostPath, 5000);
        writeState([{ ...startingSession(), startupCompleted: true }]);

        const file = await pending;
        assert.strictEqual(file.state.debugSessions[0].startupCompleted, true);
    });

    function startingSession(): AspireDebugSessionState {
        return { appHostPath, dashboardUrl: undefined, startupCompleted: false };
    }

    function launch(sequence: number): AspireExtensionE2EStateFile['debugLaunches'][number] {
        return { sequence, appHostPath, command: 'run', noDebug: false, executionSuppressed: false };
    }

    function serialize(debugSessions: readonly AspireDebugSessionState[], launchingPaths: readonly string[]): string {
        return JSON.stringify({ ...snapshot, state: { ...snapshot.state, debugSessions, launchingPaths } });
    }

    function writeState(debugSessions: readonly AspireDebugSessionState[]): void {
        snapshot.state.debugSessions = debugSessions;
        fs.writeFileSync(statePath, JSON.stringify(snapshot));
    }

    async function assertTimesOut(pending: Promise<unknown>): Promise<void> {
        await assert.rejects(pending, (error: Error) => {
            assert.ok(error.message.startsWith('Timed out after 1000ms waiting for debug AppHost startup.'), error.message);
            return true;
        });
    }
});
