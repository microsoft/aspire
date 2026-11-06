const preparedActionLifetimeMs = 5 * 60 * 1000;

export class PreparedToolActions<T> {
    private _actions: Array<{ key: string; value: T; expiresAt: number }> = [];
    private readonly _activeActionCounts = new Map<string, number>();

    constructor(
        private readonly _limit: number,
        private readonly _sameAction: (first: T, second: T) => boolean,
    ) {
    }

    clear(): void {
        this._actions.length = 0;
        this._activeActionCounts.clear();
    }

    remember(key: string, value: T): void {
        this._prune();
        if (this._actions.length >= this._limit) {
            this._actions.shift();
        }
        this._actions.push({ key, value, expiresAt: performance.now() + preparedActionLifetimeMs });
    }

    consume(key: string): T | undefined {
        this._prune();
        const matching = this._actions.filter(action => action.key === key);
        const first = matching[0];
        // VS Code supplies no preparation token to correlate identical inputs. Conflicting
        // confirmations therefore cannot prove which action the user approved.
        if (first && matching.some(action => !this._sameAction(action.value, first.value))) {
            this.remove(key);
            return undefined;
        }
        const action = matching.at(-1);
        if (action) {
            this._actions.splice(this._actions.indexOf(action), 1);
        }
        return action?.value;
    }

    async run<TResult>(key: string, operation: () => Promise<TResult>): Promise<TResult> {
        this._activeActionCounts.set(key, (this._activeActionCounts.get(key) ?? 0) + 1);
        try {
            return await operation();
        }
        finally {
            const count = this._activeActionCounts.get(key) ?? 0;
            if (count > 1) {
                this._activeActionCounts.set(key, count - 1);
            } else {
                this._activeActionCounts.delete(key);
                // Concurrent confirmations can each consume a record while an action is active.
                // Drop abandoned duplicates after the last invocation to prevent later replay.
                this.remove(key);
            }
        }
    }

    remove(key: string): void {
        this._actions = this._actions.filter(action => action.key !== key);
    }

    private _prune(): void {
        // Use a monotonic deadline: changing wall time must not extend or shorten consent.
        const now = performance.now();
        this._actions = this._actions.filter(action => action.expiresAt > now);
    }
}
