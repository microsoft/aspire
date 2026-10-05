import { registerRunCleanup } from "./runCleanupRegistry";

type RunStartWrapper = <T>(operation: () => Promise<T>) => Promise<T>;

const runStartWrappers = new Map<string, RunStartWrapper[]>();
let runStartQueue = Promise.resolve();

export function registerRunStartWrapper(runId: string, wrapper: RunStartWrapper): void {
    let wrappers = runStartWrappers.get(runId);
    if (!wrappers) {
        wrappers = [];
        runStartWrappers.set(runId, wrappers);
    }

    wrappers.push(wrapper);
    registerRunCleanup(runId, () => {
        const currentWrappers = runStartWrappers.get(runId);
        if (!currentWrappers) {
            return;
        }

        const index = currentWrappers.indexOf(wrapper);
        if (index !== -1) {
            currentWrappers.splice(index, 1);
        }

        if (currentWrappers.length === 0) {
            runStartWrappers.delete(runId);
        }
    });
}

export async function runWithRunStartWrappers<T>(runId: string, operation: () => Promise<T>): Promise<T> {
    const wrappers = [...runStartWrappers.get(runId) ?? []];
    if (wrappers.length === 0) {
        return await operation();
    }

    let wrappedOperation = operation;
    for (const wrapper of wrappers.reverse()) {
        const nextOperation = wrappedOperation;
        wrappedOperation = () => wrapper(nextOperation);
    }

    return await runSerialized(wrappedOperation);
}

export async function waitForRunStartIdle(): Promise<void> {
    await runStartQueue.catch(() => undefined);
}

async function runSerialized<T>(operation: () => Promise<T>): Promise<T> {
    let releaseQueuedStart = (): void => { };
    const previousStart = runStartQueue;
    runStartQueue = new Promise(resolve => {
        releaseQueuedStart = resolve;
    });

    await previousStart.catch(() => undefined);

    try {
        return await operation();
    } finally {
        releaseQueuedStart();
    }
}
