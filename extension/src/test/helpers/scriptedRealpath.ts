import * as fs from 'fs';
import * as path from 'path';
import * as sinon from 'sinon';

/**
 * Results a scripted path returns, in call order.
 *
 * `results` is consumed one entry per call; once it is exhausted every further call returns
 * `thereafter`, or the last scripted result when `thereafter` is omitted.
 */
export interface ScriptedRealpathPlan {
    readonly results: readonly string[];
    readonly thereafter?: string;
}

/**
 * Replaces `fs.realpathSync.native` with a per-path script so a symlink retarget can be placed
 * between two specific canonicalization samples.
 *
 * A real symlink can only be repointed between two `await`s, which makes the interesting races -
 * a target that moves *within* one synchronous binding, or between a containment check and the
 * binding that follows it - impossible to reproduce with the filesystem alone. Scripting the
 * canonicalization call itself is what makes those windows deterministic: the script decides
 * exactly which sample sees which file, on every platform, without timers or symlink support.
 *
 * Paths that are not scripted fall through to the real implementation, so a test only has to
 * describe the entry it is moving.
 */
export class ScriptedRealpath {
    private readonly _plans = new Map<string, { remaining: string[]; thereafter: string }>();
    private readonly _callsByPath = new Map<string, number>();
    private readonly _stub: sinon.SinonStub;

    constructor(sandbox: sinon.SinonSandbox = sinon) {
        const original = fs.realpathSync.native;
        this._stub = sandbox.stub(fs.realpathSync, 'native').callsFake((value: fs.PathLike, ...rest: unknown[]) => {
            const key = toKey(value);
            this._callsByPath.set(key, (this._callsByPath.get(key) ?? 0) + 1);
            const plan = this._plans.get(key);
            if (!plan) {
                return (original as (value: fs.PathLike, ...rest: unknown[]) => string)(value, ...rest);
            }

            return plan.remaining.length > 0 ? plan.remaining.shift()! : plan.thereafter;
        });
    }

    /** Scripts the results successive canonicalizations of `appHostPath` return. */
    script(appHostPath: string, plan: ScriptedRealpathPlan): void {
        if (plan.results.length === 0 && plan.thereafter === undefined) {
            throw new Error('A scripted realpath plan needs at least one result.');
        }

        this._plans.set(toKey(appHostPath), {
            remaining: [...plan.results],
            thereafter: plan.thereafter ?? plan.results[plan.results.length - 1],
        });
    }

    /** How many times `appHostPath` has been canonicalized since the script was installed. */
    callCount(appHostPath: string): number {
        return this._callsByPath.get(toKey(appHostPath)) ?? 0;
    }

    restore(): void {
        this._stub.restore();
    }
}

function toKey(value: fs.PathLike): string {
    return path.resolve(typeof value === 'string' ? value : value.toString());
}
