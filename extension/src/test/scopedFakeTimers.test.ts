import * as assert from 'assert';
import { AsyncResource } from 'node:async_hooks';
import { withScopedFakeTimers } from './helpers/scopedFakeTimers';

suite('withScopedFakeTimers', () => {
    // Long enough that the timer can only fire if a test advances the fake clock to it.
    const unreachableDelayMs = 60_000;

    test('ignores timers and microtasks scheduled by callbacks that started outside the body', async () => {
        let foreignTimer: ReturnType<typeof setTimeout> | undefined;
        // Stands in for an extension-host callback that is already queued when the test starts and runs
        // during one of tickAsync's real setImmediate yields.
        const foreignCallback = new Promise<void>(resolve => setImmediate(() => {
            foreignTimer = setTimeout(() => { }, unreachableDelayMs);
            process.nextTick(() => { });
            queueMicrotask(() => { });
            resolve();
        }));

        try {
            await withScopedFakeTimers(async clock => {
                await clock.tickAsync(0);
                await foreignCallback;

                assert.strictEqual(clock.countTimers(), 0);
            });
        }
        finally {
            clearTimeout(foreignTimer);
        }
    });

    test('ignores work the caller queues after the body yields', async () => {
        let callerTimer: ReturnType<typeof setTimeout> | undefined;
        let callerWorkQueued = () => { };
        const callerWork = new Promise<void>(resolve => { callerWorkQueued = resolve; });

        const run = withScopedFakeTimers(async clock => {
            await callerWork;

            assert.strictEqual(clock.countTimers(), 0);
        });
        // Mirrors the test runner continuing after it has invoked an async test body.
        void Promise.resolve().then(() => {
            callerTimer = setTimeout(() => { }, unreachableDelayMs);
            callerWorkQueued();
        });

        try {
            await run;
        }
        finally {
            clearTimeout(callerTimer);
        }
    });

    test('fakes timers, Date and microtasks for the body and the work it starts', async () => {
        const foreignContext = new AsyncResource('foreign-context');
        let foreignTimer: ReturnType<typeof setTimeout> | undefined;

        try {
            await withScopedFakeTimers(async clock => {
                let fired = false;
                await Promise.resolve();
                setTimeout(() => { fired = true; }, 1000);
                process.nextTick(() => { });
                queueMicrotask(() => { });
                foreignContext.runInAsyncScope(() => {
                    foreignTimer = setTimeout(() => { }, unreachableDelayMs);
                    assert.notStrictEqual(Date.now(), 0, 'Code outside the body must keep real time.');
                });

                assert.strictEqual(Date.now(), 0);
                assert.strictEqual(new Date().getTime(), 0);
                assert.strictEqual(clock.countTimers(), 3);

                await clock.tickAsync(1000);

                assert.strictEqual(fired, true);
                assert.strictEqual(Date.now(), 1000);
                assert.strictEqual(clock.countTimers(), 0);
            });
        }
        finally {
            clearTimeout(foreignTimer);
            foreignContext.emitDestroy();
        }
    });

    test('restores the real globals when the body resolves, rejects or throws', async () => {
        const captureGlobals = () => ({
            setTimeout: globalThis.setTimeout,
            clearTimeout: globalThis.clearTimeout,
            setInterval: globalThis.setInterval,
            clearInterval: globalThis.clearInterval,
            setImmediate: globalThis.setImmediate,
            clearImmediate: globalThis.clearImmediate,
            queueMicrotask: globalThis.queueMicrotask,
            Date: globalThis.Date,
            dateNow: globalThis.Date.now,
            nextTick: process.nextTick,
        });
        const originals = captureGlobals();
        const failure = new Error('body failed');

        const bodies: [string, () => unknown][] = [
            ['resolves', async () => { }],
            ['rejects', async () => { await Promise.resolve(); throw failure; }],
            ['throws synchronously', () => { throw failure; }],
        ];
        for (const [outcome, body] of bodies) {
            const run = withScopedFakeTimers(clock => {
                assert.notStrictEqual(globalThis.setTimeout, originals.setTimeout, 'The helper should be installed while the body runs.');
                assert.ok(clock);
                return body();
            });
            if (outcome === 'resolves') {
                await run;
            }
            else {
                await assert.rejects(run, failure);
            }

            assert.deepStrictEqual(captureGlobals(), originals, `Globals were not restored after the body ${outcome}.`);
        }
    });

    test('preserves Date subclasses inside and outside the body', async () => {
        const foreignContext = new AsyncResource('foreign-context');

        try {
            await withScopedFakeTimers(() => {
                // Declared here so it extends the routed Date rather than the real one.
                class DerivedDate extends Date { }

                const inside = new DerivedDate();
                assert.ok(inside instanceof DerivedDate);
                assert.strictEqual(inside.getTime(), 0);

                foreignContext.runInAsyncScope(() => {
                    const outside = new DerivedDate();
                    assert.ok(outside instanceof DerivedDate);
                    assert.notStrictEqual(outside.getTime(), 0);
                });
            });
        }
        finally {
            foreignContext.emitDestroy();
        }
    });
});
