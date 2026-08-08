/**
 * Small process-inspection helpers the E2E harness uses when a process it expected to be dead is
 * still alive.
 *
 * These live outside `src/test-e2e/**` on purpose. `compile-e2e` wipes `out/test-e2e/` and rewrites
 * it under a nested layout, so a unit test that requires `../test-e2e/helpers/...` sees a missing
 * module whenever the E2E build ran last. Keeping the pure parse/format logic here lets both the
 * E2E fixture layer and the unit test import it from a stable path.
 */

export interface ProcessSnapshot {
    pid: number;
    parentPid: number;
    status?: string;
    commandLine?: string;
}

/**
 * Parses a single-line `ps` snapshot into a structured record.
 *
 * The E2E fixture calls `ps -p <pid> -o pid=,ppid=,stat=,command=` which prints, e.g.:
 *    4711     1 S+   node /workspace/AspireE2E.NodeApp/app.js
 * `command` is the last column so it can contain spaces, meaning only the first three
 * whitespace-delimited fields (pid, ppid, stat) are split off; the remainder is treated as the
 * command line verbatim. The pid on the line is cross-checked against the requested pid so a
 * garbled snapshot (empty output, mismatched header) resolves to `undefined` rather than a wrong
 * process.
 */
export function parsePosixProcessSnapshot(output: string, pid: number): ProcessSnapshot | undefined {
    const match = /^\s*(\d+)\s+(\d+)\s+(\S+)(?:\s+(.*?))?\s*$/.exec(output);
    if (!match || Number(match[1]) !== pid) {
        return undefined;
    }

    return {
        pid,
        parentPid: Number(match[2]),
        status: match[3],
        commandLine: match[4] || undefined,
    };
}

/**
 * Formats a snapshot for a timeout error message.
 *
 * The pid is always included even when the snapshot is missing so the diagnostic still identifies
 * which process the wait was blocked on.
 */
export function formatProcessSnapshot(snapshot: ProcessSnapshot | undefined, pid: number): string {
    if (!snapshot) {
        return `pid=${pid}, process details unavailable`;
    }

    return `pid=${snapshot.pid}, parentPid=${snapshot.parentPid}, status=${snapshot.status ?? '<unknown>'}, command=${snapshot.commandLine ?? '<unknown>'}`;
}
