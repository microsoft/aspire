import type { Memento } from 'vscode';
import { UsefulnessSurveyService, type UsefulnessSurveyOutcome } from '../services/UsefulnessSurveyService';
import { showUsefulnessSurvey } from '../services/initializeUsefulnessSurvey';

/** Runs only from the opt-in E2E bridge; never uses the production telemetry sender. */
export async function probeUsefulnessSurvey(globalState: Memento, reset: boolean) {
    const shownKey = 'aspire.e2e.usefulnessSurvey.shown';
    const now = Date.now();
    if (reset) {
        await globalState.update(shownKey, undefined);
    }
    const events: { kind: string; outcome?: UsefulnessSurveyOutcome }[] = [];
    let scheduled: (() => Promise<void>) | undefined;
    let shown = false;
    let finish!: () => void;
    let fail!: (error: Error) => void;
    const completed = new Promise<void>((resolve, reject) => { finish = resolve; fail = reject; });
    const service = new UsefulnessSurveyService(globalState, {
        canCollect: () => true,
        canShow: () => true,
        schedule: callback => {
            scheduled = callback;
            return { dispose: () => { scheduled = undefined; } };
        },
        show: () => { shown = true; return showUsefulnessSurvey(); },
        send: (kind, outcome) => {
            events.push({ kind, outcome });
            if (kind === 'result') {
                finish();
            }
        },
        warn: () => fail(new Error('Usefulness survey E2E probe failed.')),
    }, { enabled: true, id: 'test-v1', questionId: 'aspire-usefulness-v1', expiresAt: now + 60 * 60 * 1000 }, shownKey);
    // Observe rejection immediately even if storage fails before the notification is shown.
    const result = completed.then(() => ({ shown, events }));
    void result.catch(() => undefined);
    try {
        service.recordCommand('aspire-vscode.runAppHost');
        await scheduled?.();
        if (!shown) {
            finish();
        }
        return await result;
    }
    finally {
        service.dispose();
    }
}
