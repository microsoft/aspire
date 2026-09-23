import type * as vscode from 'vscode';

export type UsefulnessSurveyOutcome = 'yes' | 'no' | 'dismissed' | 'never_again';

export interface UsefulnessSurveyCampaign {
    readonly enabled: boolean;
    readonly id: string;
    readonly questionId: string;
    readonly expiresAt: number;
}

// Enable only after classification, production ingestion, and reporting have been verified.
// A zero expiry also fails closed until a finite campaign end date is deliberately selected.
export const usefulnessSurveyCampaign: UsefulnessSurveyCampaign = {
    enabled: false,
    id: 'usefulness-pilot-v1',
    questionId: 'aspire-usefulness-v1',
    expiresAt: 0,
};

export interface UsefulnessSurveyEnvironment {
    canCollect(): boolean;
    canShow(): boolean;
    schedule(callback: () => Promise<void>, delayMs: number): vscode.Disposable;
    show(): PromiseLike<UsefulnessSurveyOutcome>;
    send(kind: 'invitation' | 'result', outcome?: UsefulnessSurveyOutcome): void;
    warn(): void;
}

const activityCommands = new Set([
    'new', 'init', 'add', 'deploy', 'publish', 'do', 'update',
    'runAppHost', 'debugAppHost', 'runAppHostCommand', 'debugAppHostCommand',
    'runAppHostFromExplorer', 'debugAppHostFromExplorer',
    'runAppHostFromEditorCommand', 'debugAppHostFromEditorCommand',
    'openDashboard', 'openDashboardToSide', 'viewResourceLogs', 'openResourceTerminal',
    'startResource', 'stopResource', 'restartResource', 'executeResourceCommand',
    'executeResourceCommandItem', 'deployAppHost', 'publishAppHost',
    'runPipelineStepAppHost', 'debugPipelineStepAppHost',
]);

export class UsefulnessSurveyService implements vscode.Disposable {
    private _disposed = false;
    private _failed = false;
    private _generation = 0;
    private _timer: vscode.Disposable | undefined;
    private _open = false;

    constructor(
        private readonly _state: vscode.Memento,
        private readonly _environment: UsefulnessSurveyEnvironment,
        private readonly _campaign: UsefulnessSurveyCampaign,
        private readonly _shownKey = 'aspire.usefulnessSurvey.shown',
    ) { }

    recordCommand(command: string): void {
        if (!command.startsWith('aspire-vscode.') ||
            !activityCommands.has(command.slice('aspire-vscode.'.length)) ||
            !this._canCollect() || this._open) {
            return;
        }
        try {
            if (this._wasShown()) {
                return;
            }
            this._cancelTimer();
            const generation = this._generation;
            this._timer = this._environment.schedule(() => this._invite(generation), 2 * 60 * 1000);
        }
        catch {
            this._fail();
        }
    }

    permissionsChanged(): void {
        this._cancelTimer();
    }

    dispose(): void {
        this._disposed = true;
        this._cancelTimer();
    }

    private _canCollect(): boolean {
        return !this._disposed && !this._failed && this._campaign.enabled &&
            Date.now() < this._campaign.expiresAt && this._environment.canCollect();
    }

    private _cancelTimer(): void {
        this._generation++;
        this._timer?.dispose();
        this._timer = undefined;
    }

    private _wasShown(): boolean {
        const shown = this._state.get<unknown>(this._shownKey, false);
        if (typeof shown !== 'boolean') {
            throw new Error('Invalid survey suppression state.');
        }
        return shown;
    }

    private async _invite(generation: number): Promise<void> {
        this._timer = undefined;
        if (this._open || generation !== this._generation || !this._canCollect() || !this._environment.canShow()) {
            return;
        }
        try {
            if (this._wasShown()) {
                return;
            }
            this._open = true;
            // Retire before display so every outcome, including dismissal or a crash,
            // stays one-shot after reload. Cross-window deduplication is best effort.
            await this._state.update(this._shownKey, true);
            if (generation !== this._generation || !this._canCollect() || !this._environment.canShow()) {
                return;
            }
            const response = this._environment.show();
            this._environment.send('invitation');
            const outcome = await response;
            if (generation === this._generation && this._canCollect()) {
                this._environment.send('result', outcome);
            }
        }
        catch {
            this._fail();
        }
        finally {
            this._open = false;
        }
    }

    private _fail(): void {
        this._failed = true;
        this._cancelTimer();
        // Never log the answer, storage path, or raw file contents on failure.
        this._environment.warn();
    }
}
