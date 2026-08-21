import type * as vscode from 'vscode';

export type ResourceDebugSource = 'tree' | 'languageModelTool';

export type ResourceAttachProviderId = 'dotnet' | 'go';

/**
 * The caller's requested behavior. `auto` is intentionally bounded to the same attach
 * action as `attach` today; the debug service owns that selection so callers cannot
 * introduce start or restart behavior by interpreting it themselves.
 */
export type ResourceDebugStrategy = 'auto' | 'attach';

/**
 * An AppHost selected by a caller. The absolute path remains internal to the editor
 * control plane; only the safe display path may be used by presentation layers. The
 * optional process ID preserves exact tree-item identity when one path has overlapping runs.
 */
export interface ResourceDebugAppHostTarget {
    readonly absolutePath: string;
    readonly displayPath: string;
    readonly appHostPid?: number;
}

export interface ResourceDebugRequest {
    readonly source: ResourceDebugSource;
    readonly strategy: ResourceDebugStrategy;
    readonly appHost: ResourceDebugAppHostTarget;
    readonly resourceName: string;
    readonly cancellationToken?: vscode.CancellationToken;
}

/**
 * The CLI resource snapshot supplied to attach providers. This is internal-only:
 * provider configuration may require process or project metadata that must never
 * cross the resource-debug result boundary.
 */
export interface ResourceDebugResourceSnapshot {
    readonly name: string;
    readonly displayName: string | null;
    readonly resourceType: string;
    readonly state: string | null;
    readonly properties: Record<string, unknown> | null;
}

export interface ResourceDebugExtensionRequirement {
    readonly id: string;
    readonly label: string;
    readonly installMessage?: string;
}

/**
 * A language-specific debugger attach provider. The resource-debug orchestrator supplies a
 * cancellation token because future providers may have cancellable configuration discovery.
 * Existing providers that delegate to debugger APIs without cancellation support can omit it.
 */
export interface ResourceAttachProvider {
    readonly id: ResourceAttachProviderId;
    readonly requiredDebuggerExtensions: readonly ResourceDebugExtensionRequirement[];
    /**
     * Identifies resources this provider supports independently of their current state. The service
     * uses this before checking whether a resource is running so stopped supported resources get a
     * bounded resourceNotRunning result instead of being reported as unsupported.
     */
    canRecognizeResource(resource: ResourceDebugResourceSnapshot): boolean;
    /**
     * Determines whether a recognized resource is ready to attach now, including runtime metadata
     * and any provider-specific attach prerequisites.
     */
    canAttachToResource(resource: ResourceDebugResourceSnapshot): boolean;
    createDebugConfiguration(resource: ResourceDebugResourceSnapshot, cancellationToken?: vscode.CancellationToken): Promise<vscode.DebugConfiguration>;
}

/**
 * The tree consumes only the extension-wide debug service. It must not create its own service
 * because that would split session tracking and allow duplicate attach commands.
 */
export interface ResourceDebugger {
    debug(request: ResourceDebugRequest): Promise<ResourceDebugResult>;
    canAttachToResource(resource: ResourceDebugResourceSnapshot): boolean;
    /**
     * Lets resource presentations refresh after attach sessions start or end without receiving
     * internal process, path, or debugger configuration details.
     */
    readonly onDidChangeDebugSessions?: vscode.Event<void>;
}

export type ResourceDebugErrorKind =
    | 'resourceSnapshotFailed'
    | 'providerResolutionFailed'
    | 'configurationFailed'
    | 'debuggerStartDeclined'
    | 'debuggerStartFailed'
    | 'unexpected';

export type ResourceDebugResult =
    | { readonly outcome: 'started'; readonly providerId: ResourceAttachProviderId }
    | { readonly outcome: 'alreadyDebugging' }
    | { readonly outcome: 'appHostNotFound' }
    | { readonly outcome: 'resourceNotFound' }
    | { readonly outcome: 'unsupportedResource' }
    | { readonly outcome: 'resourceNotRunning' }
    | { readonly outcome: 'debuggerExtensionMissing'; readonly debuggerExtensions: readonly ResourceDebugExtensionRequirement[] }
    | { readonly outcome: 'cancelled' }
    | { readonly outcome: 'error'; readonly errorKind: ResourceDebugErrorKind };

export type ResourceAttachConfigurationErrorKind = 'resourceNotAttachable';

export class ResourceAttachConfigurationError extends Error {
    constructor(public readonly errorKind: ResourceAttachConfigurationErrorKind, message: string) {
        super(message);
        this.name = 'ResourceAttachConfigurationError';
    }
}
