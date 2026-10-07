import * as sinon from 'sinon';
import type { Memento } from 'vscode';
import { UsefulnessSurveyService, type UsefulnessSurveyCampaign, type UsefulnessSurveyOutcome } from '../../services/UsefulnessSurveyService';

export class TestSurveyMemento implements Memento {
    readonly values = new Map<string, unknown>();
    readonly writes: string[] = [];
    fail = false;
    beforeWrite: ((key: string) => void) | undefined;

    keys(): readonly string[] {
        return [...this.values.keys()];
    }

    get<T>(key: string): T | undefined;
    get<T>(key: string, defaultValue: T): T;
    get<T>(key: string, defaultValue?: T): T | undefined {
        if (this.fail) {
            throw new Error('Test persistence failure.');
        }
        return this.values.has(key) ? this.values.get(key) as T : defaultValue;
    }

    async update(key: string, value: unknown): Promise<void> {
        if (this.fail) {
            throw new Error('Test persistence failure.');
        }
        this.beforeWrite?.(key);
        this.writes.push(key);
        if (value === undefined) {
            this.values.delete(key);
        }
        else {
            this.values.set(key, structuredClone(value));
        }
    }
}

export const testSurveyCampaign: UsefulnessSurveyCampaign = {
    enabled: true, id: 'test-v1', questionId: 'aspire-usefulness-v1', expiresAt: 365 * 24 * 60 * 60 * 1000,
};

export class TestUsefulnessSurvey {
    readonly clock = sinon.useFakeTimers({ now: 0 });
    readonly persistence = new TestSurveyMemento();
    service: UsefulnessSurveyService;
    allowed = true;
    focused = true;
    warnings = 0;
    shown = 0;
    response: UsefulnessSurveyOutcome = 'yes';
    holdResponse = false;
    answer: ((value: UsefulnessSurveyOutcome) => void) | undefined;
    events: { kind: string; outcome?: UsefulnessSurveyOutcome }[] = [];

    constructor() {
        this.service = this.createService(testSurveyCampaign);
    }

    createService(campaign: UsefulnessSurveyCampaign): UsefulnessSurveyService {
        return new UsefulnessSurveyService(this.persistence, {
            canCollect: () => this.allowed,
            canShow: () => this.focused,
            schedule: (callback, delay) => {
                const timer = setTimeout(callback, delay);
                return { dispose: () => clearTimeout(timer) };
            },
            show: async () => {
                this.shown++;
                if (this.holdResponse) {
                    return await new Promise(resolve => { this.answer = resolve; });
                }
                return this.response;
            },
            send: (kind, outcome) => this.events.push({ kind, outcome }),
            warn: () => { this.warnings++; },
        }, campaign);
    }

    async show(): Promise<void> {
        await this.clock.tickAsync(120_000);
    }

    dispose(): void {
        this.service.dispose();
        this.clock.restore();
    }
}
