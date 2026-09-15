import { extensionLogOutputChannel } from "../../utils/logging";

/**
 * Renders a stop failure for an aggregate message. A rejection reason is `unknown`: adapters reject
 * with plain strings and DAP error objects as readily as with Errors.
 */
export function describeStopFailure(reason: unknown): string {
  return reason instanceof Error ? reason.message : String(reason);
}

/**
 * Starts a session stop and always returns a promise.
 *
 * `stopSession()` is contributed by resource debugger extensions and is only typed as returning a
 * `Thenable<void>` - nothing forces the implementation to be `async`. A synchronous throw from one
 * of them would escape the surrounding `.map(...)` callback before `Promise.allSettled` ever saw
 * the array, aborting the whole shutdown and leaving every not-yet-visited resource, the AppHost,
 * and the Aspire parent running. `Promise.allSettled` only absorbs rejected promises, not throws
 * raised while the promise array is being built, so the conversion has to happen here.
 *
 * The call itself stays synchronous (rather than being deferred with `Promise.resolve().then(...)`)
 * so all resource stops are still started eagerly and run concurrently.
 */
export function startStop<T>(operation: () => Thenable<T>): Promise<T> {
  try {
    return Promise.resolve(operation());
  }
  catch (err) {
    return Promise.reject(err);
  }
}

/**
 * Asks a session to stop without waiting for it, for the paths that cannot await: the late-start
 * handlers, which stop a session that arrived after the shutdown snapshot, and dispose(), whose
 * `Disposable.dispose()` contract returns void.
 *
 * The stop is still a `Thenable` and can reject - `vscode.debug.stopDebugging()` rejects for a
 * session VS Code no longer knows about - and dropping it produced an unhandled promise rejection
 * in the extension host with no indication of which session failed.
 */
export function stopSessionInBackground(operation: () => Thenable<unknown>, description: string): void {
  startStop(operation).catch(err => {
    extensionLogOutputChannel.warn(`Failed to stop ${description}: ${describeStopFailure(err)}`);
  });
}
