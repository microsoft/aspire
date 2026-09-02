import * as vscode from 'vscode';

import {
    type ResourceDebugExtensionRequirement,
    type ResourceDebugResult,
} from '../debugger/resourceDebugContracts';
import { isCommandCancellation } from '../utils/telemetry';
import {
    aspireResourceDebugToolName,
    type AspireResourceDebugStrategy,
    type AspireResourceDebugToolDependencies,
    type AspireResourceDebugToolOutcome,
    type AspireResourceDebugToolPreparation,
    type AspireResourceDebugToolResult,
} from './resourceDebugToolContracts';

const maxAppHostPathLength = 4096;
const maxResourceNameLength = 256;

// Invisible and bidi controls can make a confirmation differ from what the model sent.
// Match the AppHost lifecycle resolver's identity boundary before resource names reach
// either confirmation text or the resource-debug service.
const identityChangingCharacters = /[\u0000-\u001F\u007F-\u009F\u2028\u2029]|\p{Cf}/u;

interface ParsedInput {
    readonly appHostPath: string;
    readonly resourceName: string;
    readonly requestedStrategy: AspireResourceDebugStrategy;
}

/**
 * Owns only the language-model boundary for resource attach. AppHost discovery and debug
 * lifecycle policy remain with the shared resolver and ResourceDebugger respectively.
 */
export class AspireResourceDebugToolService implements vscode.Disposable {
    private readonly _operationCancellationSources = new Set<vscode.CancellationTokenSource>();
    private _disposed = false;

    constructor(private readonly _dependencies: AspireResourceDebugToolDependencies) {
    }

    dispose(): void {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        for (const cancellationSource of this._operationCancellationSources) {
            cancellationSource.cancel();
        }
    }

    /**
     * Validates and resolves a confirmation target without starting a debugger. Invocation
     * calls this again rather than retaining the absolute path from confirmation.
     */
    async prepare(input: unknown, token: vscode.CancellationToken): Promise<AspireResourceDebugToolPreparation> {
        return await this._runOperation(token, operationToken => this._prepare(input, operationToken));
    }

    async debug(input: unknown, token: vscode.CancellationToken): Promise<AspireResourceDebugToolResult> {
        return await this._runOperation(token, async operationToken => {
            const preparation = await this._prepare(input, operationToken);
            if (!preparation.canDebug) {
                return preparation.result;
            }

            if (operationToken.isCancellationRequested) {
                return this.createResult(
                    'cancelled',
                    preparation.target.displayPath,
                    preparation.resourceName,
                    preparation.requestedStrategy);
            }

            try {
                const result = await this._dependencies.resourceDebugger.debug({
                    source: 'languageModelTool',
                    strategy: preparation.requestedStrategy,
                    appHost: preparation.target,
                    resourceName: preparation.resourceName,
                    cancellationToken: operationToken,
                });
                return mapResourceDebugResult(result, preparation.target.displayPath, preparation.resourceName, preparation.requestedStrategy);
            }
            catch (error) {
                return this.createResult(
                    isCommandCancellation(error) || operationToken.isCancellationRequested ? 'cancelled' : 'failed',
                    preparation.target.displayPath,
                    preparation.resourceName,
                    preparation.requestedStrategy);
            }
        });
    }

    private async _prepare(input: unknown, token: vscode.CancellationToken): Promise<AspireResourceDebugToolPreparation> {
        const parsed = parseInput(input);
        if (!parsed) {
            return this.reject('invalidInput');
        }

        if (token.isCancellationRequested) {
            return this.reject('cancelled', '', parsed);
        }

        // The manifest gate is advisory: a tool can remain registered while a workspace
        // transitions into Restricted Mode, so never resolve or attach there at runtime.
        if (!vscode.workspace.isTrusted) {
            return this.reject('workspaceNotTrusted', '', parsed);
        }

        try {
            const resolution = await this._dependencies.targetResolver.resolveTarget(parsed.appHostPath, token);
            if (token.isCancellationRequested) {
                return this.reject('cancelled', '', parsed);
            }

            if (!resolution.resolved) {
                return this.reject(resolution.outcome, '', parsed);
            }

            return {
                canDebug: true,
                target: resolution.target,
                resourceName: parsed.resourceName,
                requestedStrategy: parsed.requestedStrategy,
            };
        }
        catch (error) {
            return this.reject(isCommandCancellation(error) || token.isCancellationRequested ? 'cancelled' : 'failed', '', parsed);
        }
    }

    private async _runOperation<T>(
        callerToken: vscode.CancellationToken,
        operation: (token: vscode.CancellationToken) => Promise<T>,
    ): Promise<T> {
        const cancellationSource = new vscode.CancellationTokenSource();
        this._operationCancellationSources.add(cancellationSource);
        const cancellationRegistration = callerToken.onCancellationRequested(() => cancellationSource.cancel());
        try {
            if (this._disposed || callerToken.isCancellationRequested) {
                cancellationSource.cancel();
            }

            return await operation(cancellationSource.token);
        }
        finally {
            cancellationRegistration.dispose();
            this._operationCancellationSources.delete(cancellationSource);
            cancellationSource.dispose();
        }
    }

    private reject(
        outcome: Extract<
            AspireResourceDebugToolOutcome,
            'invalidInput' | 'unknownAppHost' | 'ambiguousAppHost' | 'discoveryFailed' | 'workspaceNotTrusted' | 'cancelled' | 'failed'
        >,
        appHost = '',
        parsed?: ParsedInput,
    ): AspireResourceDebugToolPreparation {
        return {
            canDebug: false,
            result: this.createResult(
                outcome,
                appHost,
                parsed?.resourceName ?? '',
                parsed?.requestedStrategy ?? 'auto'),
        };
    }

    private createResult(
        outcome: AspireResourceDebugToolOutcome,
        appHost: string,
        resourceName: string,
        requestedStrategy: AspireResourceDebugStrategy,
    ): AspireResourceDebugToolResult {
        return {
            tool: aspireResourceDebugToolName,
            success: false,
            outcome,
            appHost,
            resourceName,
            requestedStrategy,
            effectiveStrategy: 'none',
            controller: 'none',
        };
    }
}

function parseInput(value: unknown): ParsedInput | undefined {
    try {
        if (typeof value !== 'object' || value === null || Array.isArray(value)) {
            return undefined;
        }

        const input = value as Record<PropertyKey, unknown>;
        const properties = Reflect.ownKeys(input);
        if (properties.some(property =>
            property !== 'appHostPath' &&
            property !== 'resourceName' &&
            property !== 'strategy') ||
            !Object.prototype.hasOwnProperty.call(input, 'appHostPath') ||
            !Object.prototype.hasOwnProperty.call(input, 'resourceName')) {
            return undefined;
        }

        const appHostPath = input.appHostPath;
        const resourceName = input.resourceName;
        const strategy = input.strategy;
        if (!isSafeNonBlankString(appHostPath, maxAppHostPathLength) ||
            !isSafeNonBlankString(resourceName, maxResourceNameLength) ||
            (strategy !== undefined && strategy !== 'auto' && strategy !== 'attach')) {
            return undefined;
        }

        return {
            appHostPath,
            resourceName,
            requestedStrategy: strategy ?? 'auto',
        };
    }
    catch {
        // JSON-shaped tool input normally has data properties, but malformed extension-host
        // objects can use getters or proxies. Treating a throwing getter as invalid keeps
        // its message out of both the model transcript and the extension's control flow.
        return undefined;
    }
}

function isSafeNonBlankString(value: unknown, maxLength: number): value is string {
    return typeof value === 'string' &&
        value.trim().length > 0 &&
        value.length <= maxLength &&
        !identityChangingCharacters.test(value);
}

function mapResourceDebugResult(
    result: ResourceDebugResult,
    appHost: string,
    resourceName: string,
    requestedStrategy: AspireResourceDebugStrategy,
): AspireResourceDebugToolResult {
    const base = {
        tool: aspireResourceDebugToolName,
        appHost,
        resourceName,
        requestedStrategy,
    } as const;

    switch (result.outcome) {
        case 'started':
            return {
                ...base,
                success: true,
                outcome: 'started',
                effectiveStrategy: 'attach',
                controller: 'editor',
                provider: result.providerId,
            };
        case 'alreadyDebugging':
            return {
                ...base,
                success: true,
                outcome: 'alreadyDebugging',
                effectiveStrategy: 'attach',
                controller: 'editor',
            };
        case 'debuggerExtensionMissing':
            return {
                ...base,
                success: false,
                outcome: 'debuggerExtensionMissing',
                effectiveStrategy: 'none',
                controller: 'none',
                debuggerExtensions: result.debuggerExtensions.map(toSafeDebuggerRequirement),
            };
        case 'error':
            return {
                ...base,
                success: false,
                outcome: 'error',
                effectiveStrategy: 'none',
                controller: 'none',
                errorKind: result.errorKind,
            };
        case 'appHostNotFound':
        case 'resourceNotFound':
        case 'unsupportedResource':
        case 'resourceNotRunning':
        case 'cancelled':
            return {
                ...base,
                success: false,
                outcome: result.outcome,
                effectiveStrategy: 'none',
                controller: 'none',
            };
    }
}

function toSafeDebuggerRequirement(requirement: ResourceDebugExtensionRequirement): ResourceDebugExtensionRequirement {
    return {
        id: requirement.id,
        label: requirement.label,
    };
}
