import { AsyncLocalStorage } from 'node:async_hooks';
import * as sinon from 'sinon';

type ScopedTimerMethod = 'setTimeout' | 'clearTimeout' | 'setInterval' | 'clearInterval' | 'setImmediate' | 'clearImmediate' | 'queueMicrotask';
type TimerFunction = (...args: unknown[]) => unknown;

const scopedTimerMethods: readonly ScopedTimerMethod[] = [
    'setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'setImmediate', 'clearImmediate', 'queueMicrotask',
];

const clearMethods: ReadonlySet<ScopedTimerMethod> = new Set(['clearTimeout', 'clearInterval', 'clearImmediate']);

const fakeTimerScope = new AsyncLocalStorage<object>();

/** The fake globals of every scope whose body has not settled yet, keyed by scope. */
const activeScopes = new Map<object, typeof globalThis>();

/** The scope whose clock created each fake timer handle, so it can be cleared from any context. */
const handleOwners = new WeakMap<object, typeof globalThis>();

/** Maps each router this module installs to the global it replaced, so a later install never wraps a router. */
const routedGlobals = new WeakMap<object, unknown>();

interface InstalledRouters {
    readonly realGlobals: Record<ScopedTimerMethod, TimerFunction>;
    readonly realDate: DateConstructor;
    readonly realNextTick: typeof process.nextTick;
    readonly routers: Record<ScopedTimerMethod, TimerFunction>;
    readonly dateRouter: DateConstructor;
    readonly nextTickRouter: typeof process.nextTick;
}

let installedRouters: InstalledRouters | undefined;

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
 *
 * Scopes do not have to be nested. When mocha times out a test its body keeps running while the next test
 * starts its own scope, so the two can settle in either order. The routers are installed once for all
 * active scopes and look up the caller's scope on each call; settling a scope only retires that scope, and
 * the real globals come back when the last active scope settles.
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
    const routers = installedRouters ?? installRouters();

    // Inherit everything else from the real global so fake-timers sees a complete environment, but give it
    // its own `process` so faking nextTick never touches the real one.
    const fakeGlobal = Object.create(globalThis, {
        process: { value: Object.create(process), writable: true, configurable: true },
    }) as typeof globalThis;
    for (const name of scopedTimerMethods) {
        Object.defineProperty(fakeGlobal, name, { value: routers.realGlobals[name], writable: true, configurable: true });
    }
    Object.defineProperty(fakeGlobal, 'Date', { value: routers.realDate, writable: true, configurable: true });
    fakeGlobal.process.nextTick = routers.realNextTick;

    // Matches the methods plain `sinon.useFakeTimers()` fakes that the code under test uses; sinon forwards
    // `global` to fake-timers' withGlobal, but its typings omit it.
    const clock = sinon.useFakeTimers({
        global: fakeGlobal,
        toFake: [...scopedTimerMethods, 'Date', 'nextTick'],
        shouldClearNativeTimers: true,
    } as Parameters<typeof sinon.useFakeTimers>[0]);
    activeScopes.set(scope, fakeGlobal);

    const uninstall = clock.restore.bind(clock);
    let restored = false;
    clock.restore = () => {
        if (restored) {
            return;
        }
        restored = true;
        activeScopes.delete(scope);
        uninstall();
        if (activeScopes.size === 0) {
            uninstallRouters();
        }
    };
    return clock;
}

function activeFakeGlobal(): typeof globalThis | undefined {
    const scope = fakeTimerScope.getStore();
    return scope === undefined ? undefined : activeScopes.get(scope);
}

function installRouters(): InstalledRouters {
    const globals = globalThis as unknown as Record<string, unknown>;
    const unwrap = <V>(value: V): V => (routedGlobals.has(value as object) ? routedGlobals.get(value as object) as V : value);

    const realGlobals = Object.fromEntries(scopedTimerMethods.map(name => [name, unwrap(globals[name])])) as Record<ScopedTimerMethod, TimerFunction>;
    const realDate = unwrap(globalThis.Date);
    const realNextTick = unwrap(process.nextTick);

    const routers = {} as Record<ScopedTimerMethod, TimerFunction>;
    for (const name of scopedTimerMethods) {
        const router: TimerFunction = clearMethods.has(name)
            // The fake clear functions forward native handles to the real ones (shouldClearNativeTimers).
            ? (...args) => {
                const handle = args[0];
                const owner = typeof handle === 'object' && handle !== null ? handleOwners.get(handle) : undefined;
                return ((owner ?? activeFakeGlobal() ?? realGlobals) as unknown as Record<ScopedTimerMethod, TimerFunction>)[name](...args);
            }
            : (...args) => {
                const fake = activeFakeGlobal();
                if (fake === undefined) {
                    return realGlobals[name](...args);
                }
                const handle = (fake as unknown as Record<ScopedTimerMethod, TimerFunction>)[name](...args);
                if (typeof handle === 'object' && handle !== null) {
                    handleOwners.set(handle, fake);
                }
                return handle;
            };
        Object.defineProperty(router, 'name', { value: name });
        routers[name] = router;
    }

    const dateRouter = new Proxy(realDate, {
        apply: (_target, thisArg, args) => Reflect.apply(activeFakeGlobal()?.Date ?? realDate, thisArg, args),
        construct: (_target, args, newTarget) => Reflect.construct(activeFakeGlobal()?.Date ?? realDate, args, newTarget),
        get: (target, property) => {
            if (property === 'now') {
                return () => (activeFakeGlobal()?.Date ?? realDate).now();
            }
            return Reflect.get(target, property);
        },
    });

    const nextTickRouter = ((...args: Parameters<typeof process.nextTick>) =>
        (activeFakeGlobal()?.process.nextTick ?? realNextTick).apply(process, args)) as typeof process.nextTick;

    for (const name of scopedTimerMethods) {
        routedGlobals.set(routers[name], realGlobals[name]);
        globals[name] = routers[name];
    }
    routedGlobals.set(dateRouter, realDate);
    globalThis.Date = dateRouter;
    routedGlobals.set(nextTickRouter, realNextTick);
    process.nextTick = nextTickRouter;

    installedRouters = { realGlobals, realDate, realNextTick, routers, dateRouter, nextTickRouter };
    return installedRouters;
}

function uninstallRouters(): void {
    const installed = installedRouters;
    if (installed === undefined) {
        return;
    }
    installedRouters = undefined;

    // Only put back globals that still hold our routers. If something else replaced one on top of us, it
    // keeps its own value; the router it may later restore routes to the real implementation because no
    // scope is active.
    const globals = globalThis as unknown as Record<string, unknown>;
    for (const name of scopedTimerMethods) {
        if (globals[name] === installed.routers[name]) {
            globals[name] = installed.realGlobals[name];
        }
    }
    if (globalThis.Date === installed.dateRouter) {
        globalThis.Date = installed.realDate;
    }
    if (process.nextTick === installed.nextTickRouter) {
        process.nextTick = installed.realNextTick;
    }
}
