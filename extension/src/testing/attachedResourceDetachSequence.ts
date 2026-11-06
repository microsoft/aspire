export interface AttachedResourceDetachSteps {
  removeBreakpoint(): void;
  waitForBreakpointRemoval(): Promise<void>;
  continueExecution(): Promise<void>;
  waitForResourceResponse(phase: 'attached' | 'detached'): Promise<string>;
  detach(): Promise<void>;
}

export interface ResourceAttachReadinessSteps<T> {
  waitForResourceResponse(phase: 'beforeAttach'): Promise<string>;
  attach(): Promise<T>;
}

// A resource reports Running as soon as its launcher process starts, which for `go run` is before the
// compiled application exists. The attach provider resolves that application among the launcher's
// children within a bounded budget, so attaching on Running alone fails whenever the build outlasts the
// budget. The application serving its expected response is the observable signal that it is up.
export async function attachWhenResourceResponds<T>(steps: ResourceAttachReadinessSteps<T>): Promise<T> {
  await steps.waitForResourceResponse('beforeAttach');
  return await steps.attach();
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
