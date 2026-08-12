import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createDebugAdapterTracker } from '../debugger/adapterTracker';
import AspireDcpServer from '../dcp/AspireDcpServer';
import { AspireResourceExtendedDebugConfiguration, ServiceLogsNotification, SessionTerminatedNotification } from '../dcp/types';

suite('Debug Adapter Tracker Tests', () => {
    let dcpServer: sinon.SinonStubbedInstance<AspireDcpServer>;
    let debugSession: vscode.DebugSession;
    let registerFactoryStub: sinon.SinonStub;

    setup(() => {
        dcpServer = sinon.createStubInstance(AspireDcpServer);

        // Create a mock debug session with AspireResourceExtendedDebugConfiguration
        debugSession = {
            id: 'test-session-1',
            type: 'coreclr',
            name: 'Test Session',
            workspaceFolder: undefined,
            configuration: {
                runId: 'run-123',
                debugSessionId: 'debug-456',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            } as AspireResourceExtendedDebugConfiguration,
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub()
        };

        // Stub registerDebugAdapterTrackerFactory to record calls and allow .lastCall
        registerFactoryStub = sinon.stub(vscode.debug, 'registerDebugAdapterTrackerFactory').callsFake((_type, factory) => {
            // Return a disposable, as expected by the code under test
            return { dispose: () => {} };
        });
    });

    teardown(() => {
        sinon.restore();
    });

    test('exit code 0 is sent as 0', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        // Call onExit with code 0
        tracker.onExit(0);

        // Verify notification was sent with exit code 0
        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
        assert.strictEqual(notification.notification_type, 'sessionTerminated');
        assert.strictEqual(notification.session_id, 'run-123');
        assert.strictEqual(notification.dcp_id, 'debug-456');
        assert.strictEqual(notification.exit_code, 0);

        disposable.dispose();
    });

    test('exit code 1 is sent as 1', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        // Call onExit with code 1
        tracker.onExit(1);

        // Verify notification was sent with exit code 1
        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
        assert.strictEqual(notification.exit_code, 1);

        disposable.dispose();
    });

    test('exit code 143 on macOS is converted to 0', async () => {
        // Mock process.platform to return 'darwin'
        const originalPlatform = process.platform;
        Object.defineProperty(process, 'platform', {
            value: 'darwin',
            configurable: true
        });

        try {
            const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
            const factory = registerFactoryStub.lastCall.args[1];
            const tracker = factory.createDebugAdapterTracker(debugSession);

            // Call onExit with code 143 (SIGTERM)
            tracker.onExit(143);

            // Verify notification was sent with exit code 0 (converted from 143)
            assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
            const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
            assert.strictEqual(notification.notification_type, 'sessionTerminated');
            assert.strictEqual(notification.session_id, 'run-123');
            assert.strictEqual(notification.dcp_id, 'debug-456');
            assert.strictEqual(notification.exit_code, 0, 'Exit code 143 should be converted to 0 on macOS');

            disposable.dispose();
        } finally {
            // Restore original platform
            Object.defineProperty(process, 'platform', {
                value: originalPlatform,
                configurable: true
            });
        }
    });

    test('exit code 143 on Linux is converted to 0', async () => {
        // Mock process.platform to return 'linux'
        const originalPlatform = process.platform;
        Object.defineProperty(process, 'platform', {
            value: 'linux',
            configurable: true
        });

        try {
            const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
            const factory = registerFactoryStub.lastCall.args[1];
            const tracker = factory.createDebugAdapterTracker(debugSession);

            // Call onExit with code 143 (SIGTERM)
            tracker.onExit(143);

            // Verify notification was sent with exit code 0 (converted from 143)
            assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
            const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
            assert.strictEqual(notification.exit_code, 0, 'Exit code 143 should be converted to 0 on Linux');

            disposable.dispose();
        } finally {
            // Restore original platform
            Object.defineProperty(process, 'platform', {
                value: originalPlatform,
                configurable: true
            });
        }
    });

    test('exit code 143 on Windows is NOT converted', async () => {
        // Mock process.platform to return 'win32'
        const originalPlatform = process.platform;
        Object.defineProperty(process, 'platform', {
            value: 'win32',
            configurable: true
        });

        try {
            const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
            const factory = registerFactoryStub.lastCall.args[1];
            const tracker = factory.createDebugAdapterTracker(debugSession);

            // Call onExit with code 143
            tracker.onExit(143);

            // Verify notification was sent with exit code 143 (NOT converted on Windows)
            assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
            const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
            assert.strictEqual(notification.exit_code, 143, 'Exit code 143 should NOT be converted to 0 on Windows');

            disposable.dispose();
        } finally {
            // Restore original platform
            Object.defineProperty(process, 'platform', {
                value: originalPlatform,
                configurable: true
            });
        }
    });

    test('undefined exit code is sent as 0', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        // Call onExit with undefined
        tracker.onExit(undefined);

        // Verify notification was sent with exit code 0
        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
        assert.strictEqual(notification.exit_code, 0);

        disposable.dispose();
    });

    test('exited event exit code takes precedence over adapter onExit code', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        // Debuggee reports non-zero exit via the DAP `exited` event...
        tracker.onDidSendMessage({
            type: 'event',
            event: 'exited',
            body: { exitCode: 1 }
        });

        // ...but the debug adapter itself exits cleanly with 0.
        tracker.onExit(0);

        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
        assert.strictEqual(notification.notification_type, 'sessionTerminated');
        assert.strictEqual(notification.exit_code, 1, 'The debuggee exit code from the exited event should be reported');

        disposable.dispose();
    });

    test('falls back to adapter onExit code when no exited event is observed', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        // No `exited` event; only the adapter exit code is available.
        tracker.onExit(3);

        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
        assert.strictEqual(notification.exit_code, 3);

        disposable.dispose();
    });

    test('falls back to adapter onExit code when exited event has a non-number exit code', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        // Malformed `exited` event without a numeric exitCode; the guard should
        // ignore it and leave the adapter exit code to be used.
        tracker.onDidSendMessage({
            type: 'event',
            event: 'exited',
            body: {}
        });
        tracker.onExit(2);

        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
        assert.strictEqual(notification.exit_code, 2);

        disposable.dispose();
    });

    test('exited event exit code of 0 is used even when adapter exit code differs', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        tracker.onDidSendMessage({
            type: 'event',
            event: 'exited',
            body: { exitCode: 0 }
        });
        tracker.onExit(1);

        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
        const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
        assert.strictEqual(notification.exit_code, 0, 'A zero exited-event code must not be overridden by the adapter code');

        disposable.dispose();
    });

    test('process event resets a captured exit code so a clean restart reports 0', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        // A prior run exited non-zero, then the debuggee restarts (process event with a
        // valid PID) and exits cleanly. The stale 1 must not leak into the new run.
        tracker.onDidSendMessage({
            type: 'event',
            event: 'exited',
            body: { exitCode: 1 }
        });
        tracker.onDidSendMessage({
            type: 'event',
            event: 'process',
            body: { systemProcessId: 4242 }
        });
        tracker.onExit(0);

        const terminated = findSessionTerminated(dcpServer);
        assert.strictEqual(terminated.exit_code, 0, 'The process event should clear the captured exit code from the prior run');

        disposable.dispose();
    });

    test('process event without a system process ID still resets a captured exit code', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        // `systemProcessId` is optional in DAP. Even when the restart is reported without
        // it, the captured exit code must still be cleared for the new run.
        tracker.onDidSendMessage({
            type: 'event',
            event: 'exited',
            body: { exitCode: 1 }
        });
        tracker.onDidSendMessage({
            type: 'event',
            event: 'process',
            body: {}
        });
        tracker.onExit(0);

        const terminated = findSessionTerminated(dcpServer);
        assert.strictEqual(terminated.exit_code, 0, 'A PID-less process event must still clear the captured exit code');

        disposable.dispose();
    });

    test('exited event exit code 143 on Linux is converted to 0', async () => {
        const originalPlatform = process.platform;
        Object.defineProperty(process, 'platform', {
            value: 'linux',
            configurable: true
        });

        try {
            const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
            const factory = registerFactoryStub.lastCall.args[1];
            const tracker = factory.createDebugAdapterTracker(debugSession);

            // SIGTERM-terminated debuggee reports 143 via the exited event.
            // The adapter code is a distinct sentinel so the asserted 0 can only
            // come from converting the exited-event 143, not the adapter code.
            tracker.onDidSendMessage({
                type: 'event',
                event: 'exited',
                body: { exitCode: 143 }
            });
            tracker.onExit(7);

            assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
            const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
            assert.strictEqual(notification.exit_code, 0, 'Exit code 143 from the exited event should be converted to 0 on Linux');

            disposable.dispose();
        } finally {
            Object.defineProperty(process, 'platform', {
                value: originalPlatform,
                configurable: true
            });
        }
    });

    test('exited event exit code 143 on Windows is NOT converted', async () => {
        const originalPlatform = process.platform;
        Object.defineProperty(process, 'platform', {
            value: 'win32',
            configurable: true
        });

        try {
            const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
            const factory = registerFactoryStub.lastCall.args[1];
            const tracker = factory.createDebugAdapterTracker(debugSession);

            // Windows never converts 143, so the exited-event code must survive as-is.
            // The adapter code is a distinct sentinel so the asserted 143 can only
            // come from the exited event, not the adapter code.
            tracker.onDidSendMessage({
                type: 'event',
                event: 'exited',
                body: { exitCode: 143 }
            });
            tracker.onExit(7);

            assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
            const notification = dcpServer.sendNotification.firstCall.args[0] as SessionTerminatedNotification;
            assert.strictEqual(notification.exit_code, 143, 'Exit code 143 from the exited event should NOT be converted to 0 on Windows');

            disposable.dispose();
        } finally {
            Object.defineProperty(process, 'platform', {
                value: originalPlatform,
                configurable: true
            });
        }
    });

    test('non-telemetry output events are sent as service logs', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'node');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        const testCases: { category?: string, output: string, expectedIsStdErr: boolean, expectedLogMessage: string }[] = [
            { category: 'stdout', output: 'stdout line\n', expectedIsStdErr: false, expectedLogMessage: 'stdout line' },
            { category: 'stderr', output: 'stderr line\n', expectedIsStdErr: true, expectedLogMessage: 'stderr line' },
            { category: 'console', output: 'VITE ready\n', expectedIsStdErr: false, expectedLogMessage: 'VITE ready' },
            { category: 'important', output: 'important line\n', expectedIsStdErr: false, expectedLogMessage: 'important line' },
            { category: 'debug', output: 'debug line\n', expectedIsStdErr: false, expectedLogMessage: 'debug line' },
            { output: 'default console line\n', expectedIsStdErr: false, expectedLogMessage: 'default console line' }
        ];

        for (const testCase of testCases) {
            dcpServer.sendNotification.resetHistory();

            tracker.onDidSendMessage({
                type: 'event',
                event: 'output',
                body: {
                    category: testCase.category,
                    output: testCase.output
                }
            });

            assert.strictEqual(dcpServer.sendNotification.calledOnce, true);
            const notification = dcpServer.sendNotification.firstCall.args[0] as ServiceLogsNotification;
            assert.strictEqual(notification.notification_type, 'serviceLogs');
            assert.strictEqual(notification.session_id, 'run-123');
            assert.strictEqual(notification.dcp_id, 'debug-456');
            assert.strictEqual(notification.is_std_err, testCase.expectedIsStdErr);
            assert.strictEqual(notification.log_message, testCase.expectedLogMessage);
        }

        disposable.dispose();
    });

    test('telemetry output event is not sent as service log', async () => {
        const disposable = createDebugAdapterTracker(dcpServer as any, 'node');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        tracker.onDidSendMessage({
            type: 'event',
            event: 'output',
            body: {
                category: 'telemetry',
                output: '{"eventName":"nodeTelemetry"}'
            }
        });

        assert.strictEqual(dcpServer.sendNotification.called, false);

        disposable.dispose();
    });

    test('apphost output events are mirrored to output callback without service log notification', async () => {
        const outputCallback = sinon.stub();
        const disposable = createDebugAdapterTracker(dcpServer as any, 'pwa-node', undefined, outputCallback);
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker({
            ...debugSession,
            configuration: {
                ...debugSession.configuration,
                isApphost: true
            }
        });

        tracker.onDidSendMessage({
            type: 'event',
            event: 'output',
            body: {
                category: 'stderr',
                output: 'tsx compile error\n'
            }
        });

        assert.strictEqual(outputCallback.calledOnceWith('tsx compile error\n', 'stderr'), true);
        assert.strictEqual(dcpServer.sendNotification.called, false);

        disposable.dispose();
    });

    test('resource output events are not mirrored to output callback', async () => {
        const outputCallback = sinon.stub();
        const disposable = createDebugAdapterTracker(dcpServer as any, 'pwa-node', undefined, outputCallback);
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(debugSession);

        tracker.onDidSendMessage({
            type: 'event',
            event: 'output',
            body: {
                category: 'stderr',
                output: 'resource error\n'
            }
        });

        assert.strictEqual(outputCallback.called, false);
        assert.strictEqual(dcpServer.sendNotification.calledOnce, true);

        disposable.dispose();
    });

    test('does not send notification if debug session lacks runId', async () => {
        // Create session without proper configuration
        const invalidSession = {
            ...debugSession,
            configuration: {
                type: 'coreclr',
                name: 'Test',
                request: 'launch'
            }
        };

        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(invalidSession);

        assert.strictEqual(tracker, undefined);

        // Verify notification was NOT sent
        assert.strictEqual(dcpServer.sendNotification.called, false);

        disposable.dispose();
    });

    test('does not track leased parent debug session without runId', async () => {
        const leasedParentSession = {
            ...debugSession,
            configuration: {
                type: 'coreclr',
                name: 'Aspire test run',
                request: 'launch',
                env: {
                    DCP_INSTANCE_ID_PREFIX: 'aspire-extension-test-run-123-'
                }
            }
        };

        const disposable = createDebugAdapterTracker(dcpServer as any, 'coreclr');
        const factory = registerFactoryStub.lastCall.args[1];
        const tracker = factory.createDebugAdapterTracker(leasedParentSession);

        assert.strictEqual(tracker, undefined);
        assert.strictEqual(dcpServer.sendNotification.called, false);

        disposable.dispose();
    });
});

// Returns the single sessionTerminated notification sent during a test. A restart
// sequence also emits a processRestarted notification, so we can't rely on firstCall.
function findSessionTerminated(dcpServer: sinon.SinonStubbedInstance<AspireDcpServer>): SessionTerminatedNotification {
    const terminated = dcpServer.sendNotification.getCalls()
        .map(call => call.args[0])
        .find((notification): notification is SessionTerminatedNotification => notification.notification_type === 'sessionTerminated');

    assert.ok(terminated, 'Expected a sessionTerminated notification to be sent');
    return terminated;
}
