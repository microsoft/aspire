import * as assert from 'assert';
import { readReportedPidFromDebugOutput } from '../testing/resourceDebugOutput';

suite('Resource debug output pid markers', () => {
    test('falls back to all output events when stopped-session output lacks the requested pid marker', () => {
        const pid = readReportedPidFromDebugOutput({
            resourceDebugSession: { id: 'resource-session' },
            outputHead: [
                { sessionId: 'resource-session', sessionType: 'pwa-node', output: 'unrelated stopped-session output\n' },
                { sessionId: 'apphost-session', sessionType: 'aspire', output: 'ASPIRE_E2E_NODE_CHILD_PID=54321\n' },
            ],
            outputSample: [],
        }, 'ASPIRE_E2E_NODE_CHILD_PID');

        assert.strictEqual(pid, 54321);
    });

    test('prefers the stopped-session pid marker when it is present', () => {
        const pid = readReportedPidFromDebugOutput({
            resourceDebugSession: { id: 'resource-session' },
            outputHead: [
                { sessionId: 'apphost-session', sessionType: 'aspire', output: 'ASPIRE_E2E_NODE_PID=11111\n' },
                { sessionId: 'resource-session', sessionType: 'pwa-node', output: 'ASPIRE_E2E_NODE_PID=22222\n' },
            ],
            outputSample: [],
        }, 'ASPIRE_E2E_NODE_PID');

        assert.strictEqual(pid, 22222);
    });

    test('does not combine partial pid markers from different debug sessions', () => {
        assert.throws(
            () => readReportedPidFromDebugOutput({
                outputHead: [
                    { sessionId: 'apphost-session', sessionType: 'aspire', output: 'ASPIRE_E2E_NODE_PID=' },
                    { sessionId: 'resource-session', sessionType: 'pwa-node', output: '12345\n' },
                ],
                outputSample: [],
            }, 'ASPIRE_E2E_NODE_PID'),
            /Expected the debuggee to print ASPIRE_E2E_NODE_PID/);
    });

    test('does not combine partial pid markers across noncontiguous output captures', () => {
        assert.throws(
            () => readReportedPidFromDebugOutput({
                resourceDebugSession: { id: 'resource-session' },
                outputHead: [
                    { sessionId: 'resource-session', sessionType: 'pwa-node', output: 'ASPIRE_E2E_NODE_PID=' },
                ],
                outputSample: [
                    { sessionId: 'resource-session', sessionType: 'pwa-node', output: '12345\n' },
                ],
            }, 'ASPIRE_E2E_NODE_PID'),
            /Expected the debuggee to print ASPIRE_E2E_NODE_PID/);
    });
});
