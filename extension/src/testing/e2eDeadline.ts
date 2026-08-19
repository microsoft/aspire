export function getRemainingE2eDeadlineMs(description: string, deadlineMs: number, phaseCeilingMs: number, nowMs = Date.now()): number {
    const timeoutMs = Math.min(phaseCeilingMs, deadlineMs - nowMs);
    if (timeoutMs <= 0) {
        throw new Error(`Timed out waiting for ${description}; the E2E deadline has already passed.`);
    }

    return timeoutMs;
}

export async function runWithE2eDeadline<T>(description: string, deadlineMs: number, operation: (() => Thenable<T> | Promise<T>) | Thenable<T> | Promise<T>): Promise<T> {
    const timeoutMs = deadlineMs - Date.now();
    if (timeoutMs <= 0) {
        throw new Error(`Timed out waiting for ${description}; the E2E deadline has already passed.`);
    }

    const operationPromise = typeof operation === 'function'
        ? operation()
        : operation;

    let timeout: ReturnType<typeof setTimeout> | undefined;
    try {
        return await Promise.race([
            Promise.resolve(operationPromise),
            new Promise<T>((_, reject) => {
                timeout = setTimeout(() => {
                    timeout = undefined;
                    reject(new Error(`Timed out after ${timeoutMs}ms waiting for ${description}.`));
                }, timeoutMs);
            }),
        ]);
    }
    finally {
        if (timeout !== undefined) {
            clearTimeout(timeout);
        }
    }
}
