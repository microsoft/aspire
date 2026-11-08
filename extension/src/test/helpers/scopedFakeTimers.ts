import { AsyncLocalStorage } from 'node:async_hooks';
import * as sinon from 'sinon';

type ScopedTimerMethod = 'setTimeout' | 'clearTimeout' | 'setInterval' | 'clearInterval' | 'setImmediate' | 'clearImmediate' | 'queueMicrotask';

const scopedTimerMethods: readonly ScopedTimerMethod[] = [
    'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'setImmediate', 'clearImmediate', 'queueMicrotask',
];

const clearMethods: ReadonlySet<ScopedTimerMethod> = new Set(['clearTimeout', 'clearInterval', 'clearImmediate']);

const fakeTimerScope = new AsyncLocalStorage<object>();

/**
 * Fakes timers, `Date`, `process.nextTick` and `queueMicrotask` only for the calling test's async context.
 *
 * `sinon.useFakeTimers()` replaces those globals for the whole process. Inside the VS Code extension host
 * that process also runs host and other-extension callbacks, and `clock.tickAsync()` yields to them through
 * real `setImmediate`. When one of them schedules a timer or microtask during that yield it lands on the
 * fake clock, so a test asserting `clock.countTimers() === 0` fails intermittently for work it never
 * started (seen on the Windows unit-test job, see https://github.com/microsoft/aspire/issues/21886).
 *
 * This installs sinon's clock on a private object and routes each global to it only when the caller runs in
 * the async context that created the clock - the test body and everything it awaits or schedules. Callbacks
 * from async contexts that existed before the clock keep the real implementations, so `countTimers()` only
 * sees the test's own work. Clearing is routed by handle, so either kind of timer can be cleared anywhere.
 */
export function useScopedFakeTimers(): sinon.SinonFakeTimers {
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

    const scope = {};
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
        construct: (_target, args) => Reflect.construct(inScope() ? fakeGlobal.Date : realDate, args),
        get: (target, property) => {
            if (property === 'now') {
                return () => (inScope() ? fakeGlobal.Date : realDate).now();
            }
            return Reflect.get(target, property);
        },
    });

    realProcess.nextTick = ((...args: Parameters<typeof process.nextTick>) =>
        (inScope() ? fakeGlobal.process.nextTick : realNextTick).apply(realProcess, args)) as typeof process.nextTick;

    fakeTimerScope.enterWith(scope);

    const uninstall = clock.restore.bind(clock);
    const restore = () => {
        for (const name of scopedTimerMethods) {
            (globalThis as unknown as Record<string, unknown>)[name] = realGlobals[name];
        }
        globalThis.Date = realDate;
        realProcess.nextTick = realNextTick;
        uninstall();
        if (inScope()) {
            fakeTimerScope.enterWith(undefined as unknown as object);
        }
    };
    clock.restore = restore;
    return clock;
}
