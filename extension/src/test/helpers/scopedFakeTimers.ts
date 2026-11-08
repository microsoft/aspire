import { AsyncLocalStorage } from 'node:async_hooks';
import * as sinon from 'sinon';

type ScopedTimerMethod = 'setTimeout' | 'clearTimeout' | 'setInterval' | 'clearInterval' | 'setImmediate' | 'clearImmediate' | 'queueMicrotask';

const scopedTimerMethods: readonly ScopedTimerMethod[] = [
    'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'setImmediate', 'clearImmediate', 'queueMicrotask',
];

const clearMethods: ReadonlySet<ScopedTimerMethod> = new Set(['clearTimeout', 'clearInterval', 'clearImmediate']);

const fakeTimerScope = new AsyncLocalStorage<object>();

/**
 * Runs `body` with timers, `Date`, `process.nextTick` and `queueMicrotask` faked only for its async context.
 *
 * `sinon.useFakeTimers()` replaces those globals for the whole process. Inside the VS Code extension host
 * that process also runs host and other-extension callbacks, and `clock.tickAsync()` yields to them through
 * real `setImmediate`. When one of them schedules a timer or microtask during that yield it lands on the
 * fake clock, so a test asserting `clock.countTimers() === 0` fails intermittently for work it never
 * started (seen on the Windows unit-test job, see https://github.com/microsoft/aspire/issues/21886).
 *
 * This installs sinon's clock on a private object and routes each global to it only when the caller runs in
 * `body`'s async context - the body and everything it awaits or schedules. Callbacks from any other async
 * context, including the test runner that called us, keep the real implementations, so `countTimers()` only
 * sees the body's own work. Clearing is routed by handle, so either kind of timer can be cleared anywhere.
 * The clock is restored when `body` settles.
 */
export async function withScopedFakeTimers<T>(body: (clock: sinon.SinonFakeTimers) => T | Promise<T>): Promise<T> {
    const scope = {};
    const clock = installScopedFakeTimers(scope);
    try {
        return await fakeTimerScope.run(scope, () => body(clock));
    }
    finally {
        clock.restore();
    }
}

function installScopedFakeTimers(scope: object): sinon.SinonFakeTimers {
    const realProcess = process;
    const realGlobals = Object.fromEntries(scopedTimerMethods.map(name => [name, globalThis[name]])) as Record<ScopedTimerMethod, (...args: unknown[]) => unknown>;
    const realDate = globalThis.Date;
    const realNextTick = realProcess.nextTick;

    // Inherit everything else from the real global so fake-timers sees a complete environment, but give it
    // its own `process` so faking nextTick never touches the real one.
    const fakeGlobal = Object.create(globalThis, {
        process: { value: Object.create(realProcess), writable: true, configurable: true },
    }) as typeof globalThis;
    for (const name of scopedTimerMethods) {
        Object.defineProperty(fakeGlobal, name, { value: realGlobals[name], writable: true, configurable: true });
    }
    Object.defineProperty(fakeGlobal, 'Date', { value: realDate, writable: true, configurable: true });

    // sinon forwards `global` to fake-timers' withGlobal, but its typings omit it.
    const clock = sinon.useFakeTimers({
        global: fakeGlobal,
        toFake: [...scopedTimerMethods, 'Date', 'nextTick'],
        shouldClearNativeTimers: true,
    } as Parameters<typeof sinon.useFakeTimers>[0]);

    const inScope = () => fakeTimerScope.getStore() === scope;
    const fakeMethods = fakeGlobal as unknown as Record<ScopedTimerMethod, (...args: unknown[]) => unknown>;

    for (const name of scopedTimerMethods) {
        const router = clearMethods.has(name)
            // The fake clear functions forward native handles to the real ones (shouldClearNativeTimers).
            ? (...args: unknown[]) => fakeMethods[name](...args)
            : (...args: unknown[]) => (inScope() ? fakeMethods[name] : realGlobals[name])(...args);
        Object.defineProperty(router, 'name', { value: name });
        (globalThis as unknown as Record<string, unknown>)[name] = router;
    }

    globalThis.Date = new Proxy(realDate, {
        apply: (_target, thisArg, args) => Reflect.apply(inScope() ? fakeGlobal.Date : realDate, thisArg, args),
        construct: (_target, args, newTarget) => Reflect.construct(inScope() ? fakeGlobal.Date : realDate, args, newTarget),
        get: (target, property) => {
            if (property === 'now') {
                return () => (inScope() ? fakeGlobal.Date : realDate).now();
            }
            return Reflect.get(target, property);
        },
    });

    realProcess.nextTick = ((...args: Parameters<typeof process.nextTick>) =>
        (inScope() ? fakeGlobal.process.nextTick : realNextTick).apply(realProcess, args)) as typeof process.nextTick;

    const uninstall = clock.restore.bind(clock);
    const restore = () => {
        for (const name of scopedTimerMethods) {
            (globalThis as unknown as Record<string, unknown>)[name] = realGlobals[name];
        }
        globalThis.Date = realDate;
        realProcess.nextTick = realNextTick;
        uninstall();
    };
    clock.restore = restore;
    return clock;
}
