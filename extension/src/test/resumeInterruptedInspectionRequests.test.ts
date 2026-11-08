import * as assert from 'assert';

import { ResumeInterruptedInspectionRequests } from '../testing/resumeInterruptedInspectionRequests';

type DapMessage = { type: string; seq?: number; command?: string; event?: string; request_seq?: number; success?: boolean; message?: string };

const attachedSessionId = 'attached';

// Replays messages the way the attach proof's debug adapter tracker observes them and returns the failed
// responses it would still report in debugAdapterResponses.
type TraceStep = { sessionId?: string; direction: 'toAdapter' | 'fromAdapter' | 'proofResume'; message: DapMessage };

function reportedFailures(trace: TraceStep[]): DapMessage[] {
    const tracker = new ResumeInterruptedInspectionRequests();
    const failures: DapMessage[] = [];
    for (const { sessionId = attachedSessionId, direction, message } of trace) {
        if (direction === 'proofResume') {
            tracker.expectProofResume(sessionId);
            tracker.onRequest(sessionId, message);
            continue;
        }

        if (direction === 'toAdapter') {
            tracker.onRequest(sessionId, message);
            continue;
        }

        tracker.onEvent(sessionId, message);
        const interruptedByResume = tracker.onResponse(sessionId, message);
        if (message.type === 'response' && message.success === false && !interruptedByResume) {
            failures.push(message);
        }
    }

    return failures;
}

const request = (seq: number, command: string, sessionId?: string) =>
    ({ sessionId, direction: 'toAdapter' as const, message: { type: 'request', seq, command } });
const response = (requestSeq: number, command: string, success: boolean, message?: string, sessionId?: string) =>
    ({ sessionId, direction: 'fromAdapter' as const, message: { type: 'response', request_seq: requestSeq, command, success, message } });
// The proof's own customRequest('continue') on the attached session.
const proofContinue = (seq: number, sessionId?: string) =>
    ({ sessionId, direction: 'proofResume' as const, message: { type: 'request', seq, command: 'continue' } });
const stopped = (sessionId?: string) =>
    ({ sessionId, direction: 'fromAdapter' as const, message: { type: 'event', event: 'stopped' } });

suite('Resume-interrupted inspection requests', () => {
    test('ignores a Variables view request that the proof\'s continue aborts (#21777)', () => {
        // Windows resource-debug-tools job 126943883962: vsdbg failed VS Code's variables request 13 with
        // 'Evaluation aborted.' because the proof resumed the breakpoint while it was in flight.
        const failures = reportedFailures([
            stopped(),
            request(10, 'stackTrace'),
            response(10, 'stackTrace', true),
            request(11, 'scopes'),
            response(11, 'scopes', true),
            request(13, 'variables'),
            proofContinue(14),
            response(14, 'continue', true),
            response(13, 'variables', false, 'Evaluation aborted.'),
        ]);

        assert.deepStrictEqual(failures, []);
    });

    test('ignores an inspection request VS Code sends after the proof resumes and before the next stop', () => {
        const failures = reportedFailures([
            stopped(),
            request(10, 'stackTrace'),
            response(10, 'stackTrace', true),
            proofContinue(11),
            request(12, 'scopes'),
            response(11, 'continue', true),
            response(12, 'scopes', false, 'Unable to get scopes: the target process is running.'),
        ]);

        assert.deepStrictEqual(failures, []);
    });

    test('reports an inspection failure while the thread is still stopped', () => {
        const failures = reportedFailures([
            stopped(),
            request(13, 'variables'),
            response(13, 'variables', false, 'Evaluation aborted.'),
            proofContinue(14),
            response(14, 'continue', true),
        ]);

        assert.deepStrictEqual(failures.map(failure => failure.request_seq), [13]);
    });

    test('reports inspection failures after the debuggee stops again', () => {
        const failures = reportedFailures([
            stopped(),
            proofContinue(14),
            response(14, 'continue', true),
            stopped(),
            request(15, 'variables'),
            response(15, 'variables', false, 'Evaluation aborted.'),
        ]);

        assert.deepStrictEqual(failures.map(failure => failure.request_seq), [15]);
    });

    test('reports non-inspection failures after the proof resumes', () => {
        const failures = reportedFailures([
            stopped(),
            request(13, 'setBreakpoints'),
            proofContinue(14),
            response(13, 'setBreakpoints', false, 'Failed to set breakpoints.'),
            response(14, 'continue', false, 'Failed to continue.'),
        ]);

        assert.deepStrictEqual(failures.map(failure => failure.request_seq), [13, 14]);
    });

    test('only ignores inspection requests in the session the proof resumed', () => {
        const failures = reportedFailures([
            stopped('other'),
            request(13, 'variables', 'other'),
            stopped(),
            proofContinue(14),
            response(14, 'continue', true),
            response(13, 'variables', false, 'Evaluation aborted.', 'other'),
        ]);

        assert.deepStrictEqual(failures.map(failure => failure.request_seq), [13]);
    });

    test('reports inspection failures after a continue the proof did not send', () => {
        const failures = reportedFailures([
            stopped('other'),
            request(13, 'variables', 'other'),
            request(14, 'continue', 'other'),
            request(15, 'scopes', 'other'),
            response(14, 'continue', true, undefined, 'other'),
            response(13, 'variables', false, 'Evaluation aborted.', 'other'),
            response(15, 'scopes', false, 'Unable to get scopes: the target process is running.', 'other'),
        ]);

        assert.deepStrictEqual(failures.map(failure => failure.request_seq), [13, 15]);
    });
});
