import * as assert from 'assert';
import { createDebugAdapterOutputCapture, findBreakpointRemovalRequest, getResourceDebugProofRequest, isBreakpointRemovalAcknowledged, resourceDebugProofPhaseBudgetMs, type DebugAdapterMessageSummary } from '../testing/e2eStateFileBridge';
import type { AspireExtensionE2EControlCommand } from '../types/extensionApi';

suite('Resource debug proof request', () => {
    const appHostPath = '/workspace/AspireE2E.AppHost/AspireE2E.AppHost.csproj';
    const sourcePath = '/workspace/AspireE2E.NodeApp/app.js';

    function languageNeutralCommand(overrides: Partial<Extract<AspireExtensionE2EControlCommand, { name: 'proveResourceDebugging' }>> = {}): Extract<AspireExtensionE2EControlCommand, { name: 'proveResourceDebugging' }> {
        return {
            name: 'proveResourceDebugging',
            appHostPath,
            resourceName: 'e2e-node',
            sourcePath,
            breakpointLine: 12,
            ...overrides,
        };
    }

    test('normalizes the language-neutral command with a language-neutral proof name', () => {
        const request = getResourceDebugProofRequest(languageNeutralCommand());

        assert.strictEqual(request.proof, 'aspire-resource-debug-breakpoint-hit');
        assert.strictEqual(request.appHostPath, appHostPath);
        assert.strictEqual(request.sourcePath, sourcePath);
        assert.strictEqual(request.resourceName, 'e2e-node');
        assert.strictEqual(request.breakpointLine, 12);
        assert.strictEqual(request.timeoutMs, 300000);
        assert.strictEqual(request.pauseOnBreakpointMs, 0);
        assert.strictEqual(request.expectedResourceDebugSessionType, undefined);
        assert.strictEqual(request.stopDebuggingOnCompletion, true);
    });

    test('keeps the language-neutral proof name stable for callers that grep the payload', () => {
        // Callers assert on this exact string (see resourceDebugger.e2e.test.ts), so the constant
        // is pinned here to catch an accidental rename before the E2E shard flakes on it.
        const request = getResourceDebugProofRequest(languageNeutralCommand({
            resourceName: 'e2e-node',
            sourcePath: '/workspace/AspireE2E.NodeApp/app.js',
            breakpointLine: 4,
            timeoutMs: 120000,
            pauseOnBreakpointMs: 1500,
        }));

        assert.strictEqual(request.proof, 'aspire-resource-debug-breakpoint-hit');
        assert.strictEqual(request.resourceName, 'e2e-node');
        assert.strictEqual(request.breakpointLine, 4);
        assert.strictEqual(request.timeoutMs, 120000);
        assert.strictEqual(request.pauseOnBreakpointMs, 1500);
        assert.strictEqual(request.stopDebuggingOnCompletion, true);
    });

    test('carries the expected resource debug session type and teardown opt-out', () => {
        const request = getResourceDebugProofRequest(languageNeutralCommand({
            expectedResourceDebugSessionType: 'pwa-node',
            stopDebuggingOnCompletion: false,
        }));

        assert.strictEqual(request.expectedResourceDebugSessionType, 'pwa-node');
        assert.strictEqual(request.stopDebuggingOnCompletion, false);
    });

    test('clamps the per-phase timeout budgets to the requested timeout', () => {
        const request = getResourceDebugProofRequest(languageNeutralCommand({ timeoutMs: 60000 }));

        assert.strictEqual(request.appHostStartupTimeoutMs, 60000);
        assert.strictEqual(request.resourceStartTimeoutMs, 60000);
        assert.strictEqual(request.breakpointTimeoutMs, 60000);
    });

    test('caps each phase independently when the requested timeout is large', () => {
        const request = getResourceDebugProofRequest(languageNeutralCommand({ timeoutMs: 600000 }));

        assert.strictEqual(request.appHostStartupTimeoutMs, 180000);
        assert.strictEqual(request.resourceStartTimeoutMs, 180000);
        assert.strictEqual(request.breakpointTimeoutMs, 240000);
    });

    test('rejects a missing resource name', () => {
        assert.throws(
            () => getResourceDebugProofRequest(languageNeutralCommand({ resourceName: '' })),
            /requires resourceName/);
    });

    test('rejects a negative breakpoint line', () => {
        assert.throws(
            () => getResourceDebugProofRequest(languageNeutralCommand({ breakpointLine: -1 })),
            /zero-based non-negative integer line/);
    });

    test('rejects a non-integer timeout', () => {
        assert.throws(
            () => getResourceDebugProofRequest(languageNeutralCommand({ timeoutMs: 1.5 })),
            /timeoutMs must be a non-negative integer/);
    });

    test('rejects an unknown proof command name', () => {
        assert.throws(
            () => getResourceDebugProofRequest({ name: 'proveRustResourceDebugging' } as unknown as Extract<AspireExtensionE2EControlCommand, { name: 'proveResourceDebugging' }>),
            /Unsupported Aspire resource debug proof command/);
    });
});

suite('Resource debug proof phase budget', () => {
    const appHostPath = '/workspace/AspireE2E.AppHost/AspireE2E.AppHost.csproj';
    const sourcePath = '/workspace/AspireE2E.NodeApp/app.js';

    test('keeps the phases inside the requested timeout even at their ceilings', () => {
        const timeoutMs = 300000;
        const request = getResourceDebugProofRequest({
            name: 'proveResourceDebugging',
            appHostPath,
            resourceName: 'e2e-node',
            sourcePath,
            breakpointLine: 12,
            timeoutMs,
        });

        // The ceilings on their own sum past the request, which is the whole point of the deadline:
        // without it a proof asked for 300s could run for 600s.
        const ceilingTotal = request.appHostStartupTimeoutMs + request.resourceStartTimeoutMs + request.breakpointTimeoutMs;
        assert.ok(ceilingTotal > timeoutMs, `Expected the phase ceilings to exceed the request on their own, got ${ceilingTotal}.`);

        // Walk the phases with each one consuming everything it is granted, which is the worst case.
        const start = 1_000_000;
        const deadline = start + timeoutMs;
        let now = start;
        let granted = 0;
        for (const ceiling of [request.appHostStartupTimeoutMs, request.resourceStartTimeoutMs, request.breakpointTimeoutMs]) {
            const budget = resourceDebugProofPhaseBudgetMs(ceiling, deadline, now);
            granted += budget;
            now += budget;
        }

        assert.strictEqual(granted, timeoutMs, 'The phases together must not be granted more than the requested timeout.');
    });

    test('still caps a single phase at its own ceiling when budget remains', () => {
        const start = 1_000_000;
        assert.strictEqual(resourceDebugProofPhaseBudgetMs(180000, start + 300000, start), 180000);
    });

    test('grants nothing once the deadline has passed', () => {
        const start = 1_000_000;
        assert.strictEqual(resourceDebugProofPhaseBudgetMs(180000, start, start + 5000), 0);
    });
});

suite('Resource debug proof output capture', () => {
    test('keeps startup output head entries per debug session', () => {
        const capture = createDebugAdapterOutputCapture(2, 5);

        for (let i = 0; i < 20; i++) {
            capture.recordOutputEvent({
                sessionId: 'aspire-parent-session',
                sessionType: 'aspire',
                output: `aspire ${i}\n`,
            });
        }
        capture.recordOutputEvent({
            sessionId: 'node-resource-session',
            sessionType: 'pwa-node',
            output: 'ASPIRE_E2E_NODE_PID=12345\n',
        });
        capture.recordOutputEvent({
            sessionId: 'node-resource-session',
            sessionType: 'pwa-node',
            output: 'ASPIRE_E2E_NODE_CHILD_PID=12346\n',
        });
        capture.recordOutputEvent({
            sessionId: 'node-resource-session',
            sessionType: 'pwa-node',
            output: 'later node output\n',
        });

        assert.deepStrictEqual(
            capture.getOutputHeadEvents().filter(event => event.sessionId === 'aspire-parent-session').map(event => event.output),
            ['aspire 0\n', 'aspire 1\n']);
        assert.deepStrictEqual(
            capture.getOutputHeadEvents().filter(event => event.sessionId === 'node-resource-session').map(event => event.output),
            ['ASPIRE_E2E_NODE_PID=12345\n', 'ASPIRE_E2E_NODE_CHILD_PID=12346\n']);
        assert.strictEqual(capture.observedOutputEventCount, 23);
        assert.strictEqual(capture.getOutputSampleEvents().length, 5);
    });
});

suite('Resource debug proof breakpoint removal correlation', () => {
    const resourceSessionId = 'resource-session';
    const appHostSessionId = 'apphost-session';
    const sourcePath = '/workspace/AspireE2E.NodeApp/app.js';

    function setBreakpointsRequest(overrides: Partial<DebugAdapterMessageSummary> & { sessionId: string; seq?: number }): DebugAdapterMessageSummary {
        return {
            sessionType: 'pwa-node',
            sessionName: 'e2e-node',
            command: 'setBreakpoints',
            body: { source: { path: sourcePath, name: 'app.js' }, breakpoints: [] },
            ...overrides,
        };
    }

    function setBreakpointsResponse(overrides: Partial<DebugAdapterMessageSummary> & { sessionId: string; requestSeq?: number }): DebugAdapterMessageSummary {
        return {
            sessionType: 'pwa-node',
            sessionName: 'e2e-node',
            command: 'setBreakpoints',
            success: true,
            ...overrides,
        };
    }

    test('picks the removal request for the resumed session and source', () => {
        const request = findBreakpointRemovalRequest([
            setBreakpointsRequest({ sessionId: appHostSessionId, seq: 11, sessionType: 'aspire' }),
            setBreakpointsRequest({ sessionId: resourceSessionId, seq: 12, body: { source: { path: '/workspace/other.js' } } }),
            setBreakpointsRequest({ sessionId: resourceSessionId, seq: 13 }),
        ], resourceSessionId, sourcePath);

        assert.strictEqual(request?.seq, 13);
    });

    test('ignores a configurationDone request on the resumed session', () => {
        const request = findBreakpointRemovalRequest([
            setBreakpointsRequest({ sessionId: resourceSessionId, seq: 21, command: 'configurationDone' }),
        ], resourceSessionId, sourcePath);

        assert.strictEqual(request, undefined);
    });

    test('ignores a removal request that carries no sequence number to correlate on', () => {
        const request = findBreakpointRemovalRequest([
            setBreakpointsRequest({ sessionId: resourceSessionId, seq: undefined }),
        ], resourceSessionId, sourcePath);

        assert.strictEqual(request, undefined);
    });

    test('matches a source path the adapter reports in a non-normalized form', () => {
        // js-debug echoes back the path VS Code sent, but other adapters normalize it differently, so
        // the comparison goes through isSamePath rather than a string equality check.
        const request = findBreakpointRemovalRequest([
            setBreakpointsRequest({ sessionId: resourceSessionId, seq: 31, body: { source: { path: '/workspace/AspireE2E.NodeApp/./app.js' } } }),
        ], resourceSessionId, '/workspace/AspireE2E.NodeApp/app.js');

        assert.strictEqual(request?.seq, 31);
    });

    test('ignores a request whose body carries no source path', () => {
        const request = findBreakpointRemovalRequest([
            setBreakpointsRequest({ sessionId: resourceSessionId, seq: 41, body: { breakpoints: [] } }),
            setBreakpointsRequest({ sessionId: resourceSessionId, seq: 42, body: undefined }),
        ], resourceSessionId, sourcePath);

        assert.strictEqual(request, undefined);
    });

    test('accepts only the successful response to the correlated request', () => {
        const responses = [
            setBreakpointsResponse({ sessionId: appHostSessionId, requestSeq: 13, sessionType: 'aspire' }),
            setBreakpointsResponse({ sessionId: resourceSessionId, requestSeq: 12 }),
            setBreakpointsResponse({ sessionId: resourceSessionId, requestSeq: 13, success: false }),
        ];

        assert.strictEqual(isBreakpointRemovalAcknowledged(responses, resourceSessionId, 13), false);

        assert.strictEqual(
            isBreakpointRemovalAcknowledged([...responses, setBreakpointsResponse({ sessionId: resourceSessionId, requestSeq: 13 })], resourceSessionId, 13),
            true);
    });

    test('does not accept another command answered with the same sequence number', () => {
        assert.strictEqual(
            isBreakpointRemovalAcknowledged([
                setBreakpointsResponse({ sessionId: resourceSessionId, requestSeq: 13, command: 'configurationDone' }),
            ], resourceSessionId, 13),
            false);
    });
});
