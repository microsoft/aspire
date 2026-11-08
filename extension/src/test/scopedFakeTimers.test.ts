import * as assert from 'assert';
import { AsyncResource } from 'node:async_hooks';
import { useScopedFakeTimers } from './helpers/scopedFakeTimers';

suite('useScopedFakeTimers', () => {
    // Long enough that the timer can only fire if a test advances the fake clock to it.
    const unreachableDelayMs = 60_000;

    test('ignores timers and microtasks scheduled by callbacks that started outside the test', async () => {
        let foreignTimer: ReturnType<typeof setTimeout> | undefined;
        // Stands in for an extension-host callback that is already queued when the test starts and runs
        // during one of tickAsync's real setImmediate yields.
        const foreignCallback = new Promise<void>(resolve => setImmediate(() => {
            foreignTimer = setTimeout(() => { }, unreachableDelayMs);
            process.nextTick(() => { });
            queueMicrotask(() => { });
            resolve();
        }));
        const clock = useScopedFakeTimers();

        try {
            await clock.tickAsync(0);
            await foreignCallback;

            assert.strictEqual(clock.countTimers(), 0);
        }
        finally {
            clearTimeout(foreignTimer);
            clock.restore();
        }
    });

    test('fakes timers, Date and microtasks for the test and the work it starts', async () => {
        const foreignContext = new AsyncResource('foreign-context');
        const clock = useScopedFakeTimers();
        let foreignTimer: ReturnType<typeof setTimeout> | undefined;

        try {
            let fired = false;
            await Promise.resolve();
            setTimeout(() => { fired = true; }, 1000);
            process.nextTick(() => { });
            queueMicrotask(() => { });
            foreignContext.runInAsyncScope(() => {
                foreignTimer = setTimeout(() => { }, unreachableDelayMs);
                assert.notStrictEqual(Date.now(), 0, 'Code outside the test must keep real time.');
            });

            assert.strictEqual(Date.now(), 0);
            assert.strictEqual(new Date().getTime(), 0);
            assert.strictEqual(clock.countTimers(), 3);

            await clock.tickAsync(1000);

            assert.strictEqual(fired, true);
            assert.strictEqual(Date.now(), 1000);
            assert.strictEqual(clock.countTimers(), 0);
        }
        finally {
            clearTimeout(foreignTimer);
            clock.restore();
            foreignContext.emitDestroy();
        }
    });
});
