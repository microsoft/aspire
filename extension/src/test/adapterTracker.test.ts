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

        // Call onExit
        tracker.onExit(0);

        // Verify notification was NOT sent
        assert.strictEqual(dcpServer.sendNotification.called, false);

        disposable.dispose();
    });
});
