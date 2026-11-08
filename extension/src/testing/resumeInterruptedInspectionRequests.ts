// Requests VS Code's Variables, Watch and Call Stack views send to inspect a stopped thread.
const stoppedStateInspectionCommands: ReadonlySet<string> = new Set(['stackTrace', 'scopes', 'variables', 'evaluate']);

interface SessionInspectionState {
  proofResumeExpected: boolean;
  pendingInspectionRequests: Set<number>;
  interruptedInspectionRequests: Set<number>;
  resumedSinceLastStop: boolean;
}

// When an attach proof stops at its breakpoint, VS Code's views inspect the stopped frame while the proof
// resumes the debuggee with its own `continue`. That request bypasses VS Code's thread model, so VS Code can
// still be waiting for, or about to send, inspection requests for a frame that no longer exists. Debug
// adapters then fail them (vsdbg: 'Evaluation aborted.' or '...target process is running'). Those failures
// are caused by the proof's resume, not by the adapter. This tracker identifies exactly those responses so
// every other failed response, including inspection failures while the thread is still stopped, is kept.
export class ResumeInterruptedInspectionRequests {
  private readonly sessions = new Map<string, SessionInspectionState>();

  // Call immediately before the proof sends its own `continue` to the session. Only that request starts
  // classification, so a `continue` sent by any other session or caller cannot hide failures.
  expectProofResume(sessionId: string): void {
    this.getState(sessionId).proofResumeExpected = true;
  }

  onRequest(sessionId: string, message: { type?: unknown; seq?: unknown; command?: unknown } | undefined): void {
    if (message?.type !== 'request' || typeof message.seq !== 'number' || typeof message.command !== 'string') {
      return;
    }

    const state = this.getState(sessionId);
    if (message.command === 'continue') {
      if (!state.proofResumeExpected) {
        return;
      }

      state.proofResumeExpected = false;
      for (const sequence of state.pendingInspectionRequests) {
        state.interruptedInspectionRequests.add(sequence);
      }

      state.pendingInspectionRequests.clear();
      state.resumedSinceLastStop = true;
      return;
    }

    if (stoppedStateInspectionCommands.has(message.command)) {
      if (state.resumedSinceLastStop) {
        state.interruptedInspectionRequests.add(message.seq);
      }
      else {
        state.pendingInspectionRequests.add(message.seq);
      }
    }
  }

  onEvent(sessionId: string, message: { type?: unknown; event?: unknown } | undefined): void {
    if (message?.type === 'event' && message.event === 'stopped') {
      this.getState(sessionId).resumedSinceLastStop = false;
    }
  }

  // Returns true when the response is a failed inspection request interrupted by the proof's resume.
  onResponse(sessionId: string, message: { type?: unknown; request_seq?: unknown; success?: unknown } | undefined): boolean {
    if (message?.type !== 'response' || typeof message.request_seq !== 'number') {
      return false;
    }

    const state = this.getState(sessionId);
    state.pendingInspectionRequests.delete(message.request_seq);
    const interrupted = state.interruptedInspectionRequests.delete(message.request_seq);
    return interrupted && message.success === false;
  }

  private getState(sessionId: string): SessionInspectionState {
    let state = this.sessions.get(sessionId);
    if (!state) {
      state = { proofResumeExpected: false, pendingInspectionRequests: new Set(), interruptedInspectionRequests: new Set(), resumedSinceLastStop: false };
      this.sessions.set(sessionId, state);
    }

    return state;
  }
}
