/** Per-run cleanup handlers registered by resource-type extensions. */
const runCleanupHandlers = new Map<string, Array<() => void>>();

/**
 * Registers a cleanup handler to be invoked when the run session for the given
 * runId is stopped or encounters an error. Multiple handlers can be registered
 * for the same runId; they are invoked in registration order.
 */
export function registerRunCleanup(runId: string, cleanup: () => void): void {
    const handlers = runCleanupHandlers.get(runId) ?? [];
    handlers.push(cleanup);
    runCleanupHandlers.set(runId, handlers);
}

/**
 * Invokes all registered cleanup handlers for the given runId and removes them.
 * Safe to call even if no handlers are registered (no-op in that case).
 */
export function cleanupRun(runId: string): void {
    const handlers = runCleanupHandlers.get(runId);
    if (handlers) {
        for (const handler of handlers) {
            handler();
        }
        runCleanupHandlers.delete(runId);
    }
}
