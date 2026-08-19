import * as assert from 'assert';
import type { DebugAdapterOutputEvent } from './e2eStateFileBridge';

export interface ResourceDebugOutputProof {
    resourceDebugSession?: { id: string };
    outputHead: readonly DebugAdapterOutputEvent[];
    outputSample: readonly DebugAdapterOutputEvent[];
}

/**
 * Reads a pid the debuggee printed, for example:
 *   ASPIRE_E2E_NODE_CHILD_PID=54321
 * The value arrives as DAP `output` events, which can split or batch lines, so the whole captured
 * output is searched rather than a single event. The head and sample captures are searched
 * independently because a ring-buffer wrap can leave an unknown gap between them.
 */
export function readReportedPidFromDebugOutput(proof: ResourceDebugOutputProof, marker: string): number {
    const captures = [proof.outputHead, proof.outputSample];
    const stoppedSessionMatches = captures.flatMap(events =>
        findPidMatches(events.filter(event => event.sessionId === proof.resourceDebugSession?.id), marker));
    const matches = stoppedSessionMatches.length > 0
        ? stoppedSessionMatches
        : captures.flatMap(events => findPidMatches(events, marker));
    const events = captures.flat();

    assert.ok(matches.length > 0, `Expected the debuggee to print ${marker} in its debug output: ${JSON.stringify(events)}`);

    return Number(matches[matches.length - 1][1]);
}

function findPidMatches(events: readonly DebugAdapterOutputEvent[], marker: string): RegExpMatchArray[] {
    // Aspire, AppHost, and resource adapters can emit output concurrently. Reassemble fragments
    // per session so one adapter's marker cannot be completed with digits from another adapter.
    const outputBySessionId = new Map<string, string>();
    for (const event of events) {
        outputBySessionId.set(event.sessionId, `${outputBySessionId.get(event.sessionId) ?? ''}${event.output}`);
    }

    return [...outputBySessionId.values()]
        .flatMap(output => [...output.matchAll(new RegExp(`${marker}=(\\d+)`, 'g'))]);
}
