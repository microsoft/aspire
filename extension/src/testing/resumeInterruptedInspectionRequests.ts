// Requests VS Code's Variables, Watch and Call Stack views send to inspect a stopped thread.
const stoppedStateInspectionCommands: ReadonlySet<string> = new Set(['stackTrace', 'scopes', 'variables', 'evaluate']);

// Why a failed inspection response was attributed to the proof's resume, so a run that relied on the
// exemption shows which case matched and which `continue` it was attributed to.
export interface ResumeInterruptionClassification {
  reason: 'pendingAtProofContinue' | 'sentAfterProofContinue';
  proofContinueSeq: number;
}

interface SessionInspectionState {
  proofResumeExpected: boolean;
  proofContinueSeq?: number;
  pendingInspectionRequests: Set<number>;
  interruptedInspectionRequests: Map<number, ResumeInterruptionClassification>;
}

// When an attach proof stops at its breakpoint, VS Code's views inspect the stopped frame while the proof
// resumes the debuggee with its own `continue`. That request bypasses VS Code's thread model, so VS Code can
// still be waiting for, or about to send, inspection requests for a frame that no longer exists. vsdbg then
// fails them; the observed failure (#21777) is a `variables` request still pending at the proof's `continue`
// that fails with 'Evaluation aborted.'. Requests sent after that `continue` and before the next stop are
// covered defensively for the same reason; no CI run has shown one yet. This tracker identifies exactly those
// responses so every other failed response, including inspection failures while the thread is still stopped,
// is kept.
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
      state.proofContinueSeq = message.seq;
      for (const sequence of state.pendingInspectionRequests) {
        state.interruptedInspectionRequests.set(sequence, { reason: 'pendingAtProofContinue', proofContinueSeq: message.seq });
      }

      state.pendingInspectionRequests.clear();
      return;
    }

    if (stoppedStateInspectionCommands.has(message.command)) {
      if (state.proofContinueSeq !== undefined) {
        state.interruptedInspectionRequests.set(message.seq, { reason: 'sentAfterProofContinue', proofContinueSeq: state.proofContinueSeq });
      }
      else {
        state.pendingInspectionRequests.add(message.seq);
      }
    }
  }

  onEvent(sessionId: string, message: { type?: unknown; event?: unknown } | undefined): void {
    if (message?.type === 'event' && message.event === 'stopped') {
      this.getState(sessionId).proofContinueSeq = undefined;
    }
  }

  // Returns the classification when the response is a failed inspection request interrupted by the proof's
  // resume, otherwise undefined.
  onResponse(sessionId: string, message: { type?: unknown; request_seq?: unknown; success?: unknown } | undefined): ResumeInterruptionClassification | undefined {
    if (message?.type !== 'response' || typeof message.request_seq !== 'number') {
      return undefined;
    }

    const state = this.getState(sessionId);
    state.pendingInspectionRequests.delete(message.request_seq);
    const classification = state.interruptedInspectionRequests.get(message.request_seq);
    state.interruptedInspectionRequests.delete(message.request_seq);
    return message.success === false ? classification : undefined;
  }

  private getState(sessionId: string): SessionInspectionState {
    let state = this.sessions.get(sessionId);
    if (!state) {
      state = { proofResumeExpected: false, pendingInspectionRequests: new Set(), interruptedInspectionRequests: new Map() };
      this.sessions.set(sessionId, state);
    }

    return state;
  }
}
