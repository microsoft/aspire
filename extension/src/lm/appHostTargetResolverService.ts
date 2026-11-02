import type * as vscode from 'vscode';

import type { AppHostTargetResolution, AppHostTargetResolver } from './appHostTargetResolverContracts';
import { SafeAppHostTargetResolver } from './safeAppHostTargetResolver';

/** Adapts shared AppHost resolution to the resource debug tool's result vocabulary. */
export class AppHostTargetResolverService implements AppHostTargetResolver {
    constructor(private readonly _targetResolver: SafeAppHostTargetResolver) {
    }

    async resolveTarget(rawAppHost: unknown, token: vscode.CancellationToken): Promise<AppHostTargetResolution> {
        const resolution = await this._targetResolver.resolveTarget(rawAppHost, token);
        if (token.isCancellationRequested) {
            return { resolved: false, outcome: 'cancelled' };
        }

        if (resolution.resolved) {
            return resolution;
        }

        return {
            resolved: false,
            outcome:
                resolution.outcome === 'appHostNotFound' ? 'unknownAppHost'
                    : resolution.outcome === 'canceled' ? 'cancelled'
                        : resolution.outcome === 'error' ? 'discoveryFailed'
                            : resolution.outcome,
            knownAppHosts: resolution.knownAppHosts,
        };
    }
}
