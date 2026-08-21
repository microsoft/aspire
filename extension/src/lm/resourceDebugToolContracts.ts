import type {
    ResourceDebugErrorKind,
    ResourceDebugExtensionRequirement,
    ResourceDebugger,
    ResourceDebugStrategy,
} from '../debugger/resourceDebugContracts';
import type { AppHostTarget, AppHostTargetResolver } from './appHostTargetResolverContracts';
import type { PreparableLanguageModelToolRegistration } from './languageModelToolContracts';

export const aspireResourceDebugToolName = 'aspire_resource_debug';

export type AspireResourceDebugStrategy = ResourceDebugStrategy;

export interface AspireResourceDebugToolInput {
    readonly appHostPath: string;
    readonly resourceName: string;
    readonly strategy?: AspireResourceDebugStrategy;
}

export type AspireResourceDebugToolOutcome =
    | 'started'
    | 'alreadyDebugging'
    | 'appHostNotFound'
    | 'resourceNotFound'
    | 'unsupportedResource'
    | 'resourceNotRunning'
    | 'debuggerExtensionMissing'
    | 'error'
    | 'invalidInput'
    | 'unknownAppHost'
    | 'ambiguousAppHost'
    | 'discoveryFailed'
    | 'workspaceNotTrusted'
    | 'cancelled'
    | 'failed';

/**
 * The entire language-model result boundary. It contains only caller-approved resource
 * identity, resolver-produced display identity, and bounded debugger state.
 */
export interface AspireResourceDebugToolResult {
    readonly tool: typeof aspireResourceDebugToolName;
    readonly success: boolean;
    readonly outcome: AspireResourceDebugToolOutcome;
    readonly appHost: string;
    readonly resourceName: string;
    readonly requestedStrategy: AspireResourceDebugStrategy;
    readonly effectiveStrategy: 'attach' | 'none';
    readonly controller: 'editor' | 'none';
    readonly provider?: 'dotnet' | 'go';
    readonly debuggerExtensions?: readonly ResourceDebugExtensionRequirement[];
    readonly errorKind?: ResourceDebugErrorKind;
}

export interface AspireResourceDebugToolDependencies {
    readonly targetResolver: AppHostTargetResolver;
    readonly resourceDebugger: ResourceDebugger;
}

export type AspireResourceDebugToolPreparation =
    | {
        readonly canDebug: true;
        readonly target: AppHostTarget;
        readonly resourceName: string;
        readonly requestedStrategy: AspireResourceDebugStrategy;
    }
    | {
        readonly canDebug: false;
        readonly result: AspireResourceDebugToolResult;
    };

export type AspireResourceDebugToolRegistration = PreparableLanguageModelToolRegistration;

export type {
    AppHostTarget as SafeAppHostTarget,
    AppHostTargetResolution as SafeAppHostTargetResolution,
    AppHostTargetResolver as SafeAppHostTargetResolver,
} from './appHostTargetResolverContracts';
