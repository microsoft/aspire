import * as assert from 'assert';

import {
    attachWhenResourceResponds,
    runAttachedResourceDetachSequence,
    type AttachedResourceDetachSteps,
    type ResourceAttachReadinessSteps,
} from '../testing/attachedResourceDetachSequence';

suite('Attached resource detach sequence', () => {
    // Models the debuggee observed in the Windows resource-debug-tools flake (#21411): requests queued by
    // the traffic driver are still waiting at the breakpoint, the adapter applies breakpoint removal
    // asynchronously, and detaching from a debuggee that is stopped at a breakpoint leaves it suspended.
    function createFakeAttachedDebuggee() {
        const state = {
            breakpointBound: true,
            stoppedAtBreakpoint: true,
            queuedRequests: 2,
            detached: false,
            suspendedAfterDetach: false,
            pendingRemoval: undefined as Promise<void> | undefined,
            log: [] as string[],
        };

        const serve = (phase: 'attached' | 'detached'): string | undefined => {
            if ((phase === 'detached') !== state.detached) {
                throw new Error(`Unexpected ${phase} request.`);
            }

            return state.stoppedAtBreakpoint || state.suspendedAfterDetach ? undefined : 'ok';
        };

        const steps: AttachedResourceDetachSteps = {
            removeBreakpoint: () => {
                state.log.push('removeBreakpoint');
                // vscode.debug.removeBreakpoints returns before the adapter receives setBreakpoints.
                state.pendingRemoval = new Promise<void>(resolve => setTimeout(() => {
                    state.breakpointBound = false;
                    state.log.push('setBreakpoints acknowledged');
                    resolve();
                }, 10));
            },
            waitForBreakpointRemoval: async () => {
                await state.pendingRemoval;
            },
            continueExecution: async () => {
                state.log.push('continue');
                state.stoppedAtBreakpoint = false;
                if (state.queuedRequests > 0) {
                    state.queuedRequests--;
                    // A queued request immediately reaches the breakpoint line again.
                    state.stoppedAtBreakpoint = state.breakpointBound;
                }
                if (!state.stoppedAtBreakpoint) {
                    state.queuedRequests = 0;
                }
            },
            waitForResourceResponse: async phase => {
                const body = serve(phase);
                if (body === undefined) {
                    throw new Error(`Timed out waiting for resource response while ${phase}.`);
                }

                return body;
            },
            detach: async () => {
                state.log.push('detach');
                state.detached = true;
                state.suspendedAfterDetach = state.stoppedAtBreakpoint;
            },
        };

        return { state, steps };
    }

    test('waits for breakpoint removal and an attached response before detaching', async () => {
        const { state, steps } = createFakeAttachedDebuggee();

        const body = await runAttachedResourceDetachSequence(steps);

        assert.strictEqual(body, 'ok');
        assert.strictEqual(state.suspendedAfterDetach, false);
        assert.deepStrictEqual(state.log, ['removeBreakpoint', 'setBreakpoints acknowledged', 'continue', 'detach']);
    });

    test('does not detach when the debuggee is not serving requests while attached', async () => {
        const { state, steps } = createFakeAttachedDebuggee();
        steps.continueExecution = async () => {
            state.log.push('continue');
        };

        await assert.rejects(
            () => runAttachedResourceDetachSequence(steps),
            /Timed out waiting for resource response while attached\./);
        assert.ok(!state.log.includes('detach'), `Expected no detach, got ${state.log.join(', ')}`);
    });
});

suite('Resource attach readiness', () => {
    // Models the Windows resource-debug-tools Go flake: the resource reports Running as soon as `go run`
    // starts, but the compiled application only appears once the build finishes. The attach provider
    // looks for that application among the launcher's children and reports configurationFailed when it
    // is not there yet.
    function createFakeGoRunResource(buildPolls: number) {
        const state = {
            remainingBuildPolls: buildPolls,
            log: [] as string[],
        };

        const applicationStarted = () => state.remainingBuildPolls === 0;
        const steps: ResourceAttachReadinessSteps<string> = {
            waitForResourceResponse: async phase => {
                while (!applicationStarted()) {
                    state.remainingBuildPolls--;
                    await Promise.resolve();
                }

                state.log.push(`${phase} response`);
                return 'go-ok';
            },
            attach: async () => {
                state.log.push('attach');
                if (!applicationStarted()) {
                    throw new Error('aspire_resource_debug returned configurationFailed.');
                }

                return 'started';
            },
        };

        return { state, steps };
    }

    test('attaching as soon as the launcher runs fails while the application is still building', async () => {
        const { steps } = createFakeGoRunResource(3);

        await assert.rejects(() => steps.attach(), /configurationFailed/);
    });

    test('attaches only after the application serves its expected response', async () => {
        const { state, steps } = createFakeGoRunResource(3);

        const outcome = await attachWhenResourceResponds(steps);

        assert.strictEqual(outcome, 'started');
        assert.deepStrictEqual(state.log, ['beforeAttach response', 'attach']);
    });

    test('does not attach when the application never responds', async () => {
        const { state, steps } = createFakeGoRunResource(3);
        steps.waitForResourceResponse = async () => {
            throw new Error('Timed out waiting for resource response before attaching the debugger.');
        };

        await assert.rejects(() => attachWhenResourceResponds(steps), /before attaching the debugger/);
        assert.ok(!state.log.includes('attach'), `Expected no attach, got ${state.log.join(', ')}`);
    });
});
