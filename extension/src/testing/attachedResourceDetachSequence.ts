export interface AttachedResourceDetachSteps {
  removeBreakpoint(): void;
  waitForBreakpointRemoval(): Promise<void>;
  continueExecution(): Promise<void>;
  waitForResourceResponse(phase: 'attached' | 'detached'): Promise<string>;
  detach(): Promise<void>;
}

// The traffic driver leaves requests queued inside the debuggee while it is stopped at the breakpoint.
// vscode.debug.removeBreakpoints only schedules a setBreakpoints request, so continuing before the
// adapter acknowledges the removal lets a queued request stop on the still-bound breakpoint again, and
// detaching from that stopped debuggee can leave it suspended. Each step therefore waits for an
// observable acknowledgement before the next one is issued.
export async function runAttachedResourceDetachSequence(steps: AttachedResourceDetachSteps): Promise<string> {
  steps.removeBreakpoint();
  await steps.waitForBreakpointRemoval();
  await steps.continueExecution();
  await steps.waitForResourceResponse('attached');
  await steps.detach();
  return await steps.waitForResourceResponse('detached');
}
