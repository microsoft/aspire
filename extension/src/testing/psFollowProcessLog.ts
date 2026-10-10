import * as fs from 'fs';

export interface PsFollowProcessEvent {
    readonly runId: string;
    readonly id: string;
    readonly pid: number;
    readonly state: 'started' | 'exited';
}

export interface PsFollowProcessRecord {
    readonly id: string;
    readonly pid: number;
    readonly exited: boolean;
}

export function getPsFollowProcessLogPath(stateFile: string): string {
    return `${stateFile}.ps-follow.jsonl`;
}

export function readPsFollowProcesses(processLogPath: string, runId: string): PsFollowProcessRecord[] {
    if (!fs.existsSync(processLogPath)) {
        return [];
    }

    const processes = new Map<string, PsFollowProcessRecord>();
    // The activation-time observer appends one event per line, including native followers:
    //   {"runId":"run","id":"child","pid":123,"state":"started"}\n
    //   {"runId":"run","id":"child","pid":123,"state":"exited"}\n
    // Ignore an unfinished final line. Handle IDs distinguish reused PIDs across reloads.
    for (const line of fs.readFileSync(processLogPath, 'utf8').split(/\r?\n/).slice(0, -1)) {
        const event: unknown = JSON.parse(line);
        if (event === null || typeof event !== 'object'
            || !('runId' in event) || typeof event.runId !== 'string' || event.runId.length === 0
            || !('id' in event) || typeof event.id !== 'string' || event.id.length === 0
            || !('pid' in event) || typeof event.pid !== 'number'
            || !Number.isSafeInteger(event.pid) || event.pid <= 0
            || !('state' in event) || (event.state !== 'started' && event.state !== 'exited')) {
            throw new Error(`Invalid ps follow process event in ${processLogPath}: ${line}`);
        }
        if (event.runId !== runId) {
            continue;
        }

        const previous = processes.get(event.id);
        if (event.state === 'started') {
            if (previous) {
                throw new Error(`Duplicate ps follow process start in ${processLogPath}: ${line}`);
            }
            processes.set(event.id, { id: event.id, pid: event.pid, exited: false });
        } else {
            if (!previous || previous.pid !== event.pid || previous.exited) {
                throw new Error(`Unmatched ps follow process exit in ${processLogPath}: ${line}`);
            }
            processes.set(event.id, { ...previous, exited: true });
        }
    }

    return Array.from(processes.values());
}
