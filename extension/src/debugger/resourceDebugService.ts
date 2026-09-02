import * as vscode from 'vscode';
import type { AppHostDisplayInfo, ResourceJson } from '../data/AppHostDataRepository';
import type { AspireExtendedDebugConfiguration } from '../dcp/types';
import { compareAppHostIdentity, type AppHostIdentityRelation } from '../utils/appHostIdentity';
import { extensionLogOutputChannel } from '../utils/logging';
import { isCommandCancellation } from '../utils/telemetry';
import {
    ResourceAttachConfigurationError,
    type ResourceAttachProvider,
    type ResourceDebugAppHostTarget,
    type ResourceDebugExtensionRequirement,
    type ResourceDebugger,
    type ResourceDebugRequest,
    type ResourceDebugResult,
    type ResourceDebugStrategy,
} from './resourceDebugContracts';
import { ResourceAttachProviderRegistry } from './resourceAttachProviders';
import { ResourceDebugSessionRegistry } from './resourceDebugSessionRegistry';
import {
    ExtensionResourceDebugTelemetry,
    type ResourceDebugAttachSessionMetadata,
    type ResourceDebugClock,
    type ResourceDebugDebuggerRequirement,
    type ResourceDebugResourceState,
    type ResourceDebugResourceType,
    type ResourceDebugRequestedStrategyTelemetryBucket,
    type ResourceDebugResultTelemetryMeasurements,
    type ResourceDebugTelemetry,
    monotonicResourceDebugClock,
} from './resourceDebugTelemetry';

const safeAttachDebuggerOverrideProperties = {
    dotnet: [
        'name',
        'justMyCode',
        'requireExactSource',
        'suppressJITOptimizations',
        'enableStepFiltering',
        'sourceFileMap',
        'sourceLinkOptions',
        'symbolOptions',
        'logging',
        'stopAtEntry',
    ],
    go: [
        'name',
        'stopOnEntry',
        'substitutePath',
        'showRegisters',
        'showGlobalVariables',
        'showLog',
        'logOutput',
        'hideSystemGoroutines',
        'stackTraceDepth',
        'showPprofLabels',
        'trace',
        'cwd',
    ],
} as const;

export interface ResourceDebugAppHostRepository {
    fetchRunningAppHostsOnce(cancellationToken?: vscode.CancellationToken): Promise<readonly AppHostDisplayInfo[]>;
    fetchAppHostResourcesOnce(appHostPath: string, cancellationToken?: vscode.CancellationToken, appHostPid?: number): Promise<readonly ResourceJson[]>;
}

export type ResourceDebugAppHostIdentityComparer =
    (left: string | undefined, right: string | undefined) => AppHostIdentityRelation;

export type ResourceDebugStartDebugging =
    (workspaceFolder: vscode.WorkspaceFolder | undefined, configuration: vscode.DebugConfiguration) => Thenable<boolean>;

export interface ResourceDebugServiceDependencies {
    readonly appHostRepository: ResourceDebugAppHostRepository;
    readonly attachProviders: ResourceAttachProviderRegistry;
    readonly sessionRegistry: ResourceDebugSessionRegistry;
    readonly startDebugging: ResourceDebugStartDebugging;
    readonly compareAppHostIdentity?: ResourceDebugAppHostIdentityComparer;
    readonly isProcessAlreadyDebugged?: (processId: number) => boolean;
    readonly getDebugSessionConfiguration?: (appHost: ResourceDebugAppHostTarget) => AspireExtendedDebugConfiguration | undefined;
    readonly telemetry?: ResourceDebugTelemetry;
    readonly clock?: ResourceDebugClock;
}

/**
 * Resolves and attaches to a resource using a fresh CLI snapshot. It deliberately returns only
 * bounded, presentation-safe outcomes; tree and language-model callers own their own UX.
 */
export class ResourceDebugService implements vscode.Disposable, ResourceDebugger {
    private readonly _compareAppHostIdentity: ResourceDebugAppHostIdentityComparer;
    private readonly _telemetry: ResourceDebugTelemetry;
    private readonly _clock: ResourceDebugClock;
    readonly onDidChangeDebugSessions: vscode.Event<void>;

    constructor(private readonly _dependencies: ResourceDebugServiceDependencies) {
        this._compareAppHostIdentity = _dependencies.compareAppHostIdentity ?? compareAppHostIdentity;
        this._telemetry = _dependencies.telemetry ?? new ExtensionResourceDebugTelemetry();
        this._clock = _dependencies.clock ?? monotonicResourceDebugClock;
        this.onDidChangeDebugSessions = _dependencies.sessionRegistry.onDidChangeSessions;
    }

    dispose(): void {
        this._dependencies.sessionRegistry.dispose();
    }

    canAttachToResource(resource: ResourceJson): boolean {
        try {
            const provider = this._dependencies.attachProviders.getRecognizedProviderForResource(resource);
            return provider !== undefined
                && provider.canAttachToResource(resource);
        }
        catch (error) {
            this._logFailure('checking whether a resource can be attached', error);
            return false;
        }
    }

    async debug(request: ResourceDebugRequest): Promise<ResourceDebugResult> {
        const requestedStrategy = getRequestedStrategy(request.strategy);
        const effectiveStrategy = selectEffectiveStrategy(requestedStrategy);
        const telemetry = new ResourceDebugOperationTelemetry(
            this._telemetry,
            this._clock,
            request.source,
            requestedStrategy ?? 'invalid');
        telemetry.recordStart();
        let result: ResourceDebugResult = { outcome: 'error', errorKind: 'unexpected' };

        try {
            if (requestedStrategy === undefined || effectiveStrategy === undefined) {
                result = { outcome: 'error', errorKind: 'unexpected' };
                return result;
            }

            if (request.cancellationToken?.isCancellationRequested) {
                result = { outcome: 'cancelled' };
                return result;
            }

            const resolvedAppHost = await this._resolveAppHost(request);
            if ('outcome' in resolvedAppHost) {
                result = resolvedAppHost;
                return result;
            }

            const resolvedTarget: ResourceDebugAppHostTarget = {
                absolutePath: resolvedAppHost.appHostPath,
                displayPath: request.appHost.displayPath,
                appHostPid: resolvedAppHost.appHostPid,
                cliPid: resolvedAppHost.cliPid ?? undefined,
            };
            result = await this._dependencies.sessionRegistry.runSerialized(
                resolvedTarget,
                request.resourceName,
                request.cancellationToken,
                async () => await this._debugSerialized(request, resolvedTarget, telemetry, requestedStrategy, effectiveStrategy),
                () => ({ outcome: 'cancelled' }));
            return result;
        }
        catch (error) {
            if (isCommandCancellation(error) || request.cancellationToken?.isCancellationRequested) {
                result = { outcome: 'cancelled' };
                return result;
            }

            this._logFailure('debugging the resource', error);
            result = { outcome: 'error', errorKind: 'unexpected' };
            return result;
        }
        finally {
            telemetry.recordResult(result);
        }
    }

    private async _resolveAppHost(request: ResourceDebugRequest): Promise<AppHostDisplayInfo | ResourceDebugResult> {
        let appHosts: readonly AppHostDisplayInfo[];
        try {
            appHosts = await this._dependencies.appHostRepository.fetchRunningAppHostsOnce(request.cancellationToken);
        }
        catch (error) {
            if (isCommandCancellation(error) || request.cancellationToken?.isCancellationRequested) {
                return { outcome: 'cancelled' };
            }

            this._logFailure('resolving the running AppHost', error);
            return { outcome: 'error', errorKind: 'resourceSnapshotFailed' };
        }

        if (request.cancellationToken?.isCancellationRequested) {
            return { outcome: 'cancelled' };
        }

        const appHostMatches = appHosts.map(appHost => ({
            appHost,
            relation: this._compareAppHostIdentity(request.appHost.absolutePath, appHost.appHostPath),
        }));
        if (appHostMatches.some(match => match.relation === 'ambiguous')) {
            return { outcome: 'appHostNotFound' };
        }

        const matchingAppHosts = appHostMatches
            .filter(match => match.relation === 'same')
            .map(match => match.appHost)
            .filter(appHost => request.appHost.appHostPid === undefined
                || appHost.appHostPid === request.appHost.appHostPid);
        if (matchingAppHosts.length !== 1) {
            return { outcome: 'appHostNotFound' };
        }

        return matchingAppHosts[0];
    }

    private async _debugSerialized(
        request: ResourceDebugRequest,
        resolvedTarget: ResourceDebugAppHostTarget,
        telemetry: ResourceDebugOperationTelemetry,
        requestedStrategy: ResourceDebugStrategy,
        effectiveStrategy: 'attach',
    ): Promise<ResourceDebugResult> {
        if (request.cancellationToken?.isCancellationRequested) {
            return { outcome: 'cancelled' };
        }

        let resources: readonly ResourceJson[];
        try {
            resources = await this._dependencies.appHostRepository.fetchAppHostResourcesOnce(
                resolvedTarget.absolutePath,
                request.cancellationToken,
                resolvedTarget.appHostPid);
        }
        catch (error) {
            if (isCommandCancellation(error) || request.cancellationToken?.isCancellationRequested) {
                return { outcome: 'cancelled' };
            }

            this._logFailure('fetching the selected AppHost resource snapshot', error);
            return { outcome: 'error', errorKind: 'resourceSnapshotFailed' };
        }

        if (request.cancellationToken?.isCancellationRequested) {
            return { outcome: 'cancelled' };
        }

        const matchingResources = resources.filter(resource => resource.name === request.resourceName);
        if (matchingResources.length !== 1) {
            return { outcome: 'resourceNotFound' };
        }

        const resource = matchingResources[0];
        telemetry.recordResource(resource);
        let provider: ResourceAttachProvider | undefined;
        try {
            provider = this._dependencies.attachProviders.getRecognizedProviderForResource(resource);
        }
        catch (error) {
            if (isCommandCancellation(error) || request.cancellationToken?.isCancellationRequested) {
                return { outcome: 'cancelled' };
            }

            this._logFailure('resolving the resource attach provider', error);
            return { outcome: 'error', errorKind: 'providerResolutionFailed' };
        }

        if (!provider) {
            return { outcome: 'unsupportedResource' };
        }

        telemetry.recordProvider(provider);
        if (resource.state !== 'Running') {
            return { outcome: 'resourceNotRunning' };
        }

        return await this._attach(request, resolvedTarget, resource, provider, telemetry, requestedStrategy, effectiveStrategy);
    }

    private async _attach(
        request: ResourceDebugRequest,
        appHost: ResourceDebugAppHostTarget,
        resource: ResourceJson,
        provider: ResourceAttachProvider,
        telemetry: ResourceDebugOperationTelemetry,
        requestedStrategy: ResourceDebugStrategy,
        effectiveStrategy: 'attach',
    ): Promise<ResourceDebugResult> {
        if (request.cancellationToken?.isCancellationRequested) {
            return { outcome: 'cancelled' };
        }

        if (this._dependencies.sessionRegistry.hasActiveSession(appHost, resource.name)) {
            return { outcome: 'alreadyDebugging' };
        }

        const processId = getResourceProcessId(resource);
        if (processId !== undefined && this._dependencies.isProcessAlreadyDebugged?.(processId)) {
            return { outcome: 'alreadyDebugging' };
        }

        let missingDebuggerExtensions: readonly ResourceDebugExtensionRequirement[];
        try {
            if (!provider.canAttachToResource(resource)) {
                return { outcome: 'unsupportedResource' };
            }

            missingDebuggerExtensions = this._dependencies.attachProviders.getMissingDebuggerExtensions(provider);
        }
        catch (error) {
            if (isCommandCancellation(error) || request.cancellationToken?.isCancellationRequested) {
                return { outcome: 'cancelled' };
            }

            this._logFailure('resolving the installed resource attach provider', error);
            return { outcome: 'error', errorKind: 'providerResolutionFailed' };
        }

        if (missingDebuggerExtensions.length > 0) {
            telemetry.recordDebuggerRequirement('missing');
            return {
                outcome: 'debuggerExtensionMissing',
                debuggerExtensions: missingDebuggerExtensions.map(requirement => requirement.installMessage
                    ? { id: requirement.id, label: requirement.label, installMessage: requirement.installMessage }
                    : { id: requirement.id, label: requirement.label }),
            };
        }

        telemetry.recordDebuggerRequirement('installed');
        let configuration: vscode.DebugConfiguration;
        try {
            configuration = await provider.createDebugConfiguration(resource, request.cancellationToken);
            const launchConfigurationType = resource.properties?.['resource.launchConfigurationType'];
            if (typeof launchConfigurationType === 'string') {
                applySafeAttachDebuggerOverrides(
                    configuration,
                    this._dependencies.getDebugSessionConfiguration?.(appHost),
                    launchConfigurationType,
                    provider.id);
                configuration.noDebug = false;
            }
        }
        catch (error) {
            if (isCommandCancellation(error) || request.cancellationToken?.isCancellationRequested) {
                return { outcome: 'cancelled' };
            }

            this._logFailure(
                error instanceof ResourceAttachConfigurationError
                    ? 'creating an attach configuration for an ineligible resource'
                    : 'creating the resource attach configuration',
                error);
            return { outcome: 'error', errorKind: 'configurationFailed' };
        }

        const attachProcessId = configuration.processId;
        if (typeof attachProcessId === 'number' &&
            Number.isInteger(attachProcessId) &&
            attachProcessId > 0 &&
            this._dependencies.isProcessAlreadyDebugged?.(attachProcessId)) {
            return { outcome: 'alreadyDebugging' };
        }

        if (request.cancellationToken?.isCancellationRequested) {
            return { outcome: 'cancelled' };
        }

        const attempt = this._dependencies.sessionRegistry.createAttempt(
            appHost,
            resource.name,
            configuration,
            telemetry.createSessionMetadata(provider.id, requestedStrategy, effectiveStrategy));
        try {
            telemetry.recordDebugStart();
            const started = await this._dependencies.startDebugging(undefined, attempt.configuration);
            if (!started) {
                attempt.abandon();
                return { outcome: 'error', errorKind: 'debuggerStartDeclined' };
            }

            attempt.markStarted();
            return { outcome: 'started', providerId: provider.id };
        }
        catch (error) {
            attempt.abandon();
            if (isCommandCancellation(error) || request.cancellationToken?.isCancellationRequested) {
                return { outcome: 'cancelled' };
            }

            this._logFailure('starting the resource debugger', error);
            return { outcome: 'error', errorKind: 'debuggerStartFailed' };
        }
    }

    private _logFailure(operation: string, error: unknown): void {
        extensionLogOutputChannel.error(`Resource debugger failed while ${operation}: ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
    }
}

function applySafeAttachDebuggerOverrides(
    configuration: vscode.DebugConfiguration,
    debugSessionConfiguration: AspireExtendedDebugConfiguration | undefined,
    launchConfigurationType: string,
    providerId: ResourceAttachProvider['id'],
): void {
    const overrides = debugSessionConfiguration?.debuggers?.[launchConfigurationType];
    if (!overrides) {
        return;
    }

    // A denylist would let a newly supported transport or remote-target property silently retarget
    // an operation the user confirmed as a local Aspire resource. Copy only options that affect
    // presentation, source mapping, symbol loading, logging, or debugger runtime behavior.
    for (const property of safeAttachDebuggerOverrideProperties[providerId]) {
        if (Object.prototype.hasOwnProperty.call(overrides, property)) {
            configuration[property] = overrides[property];
        }
    }
}

function getResourceProcessId(resource: ResourceJson): number | undefined {
    const value: unknown = resource.properties?.['executable.pid'];
    if (typeof value === 'number') {
        return Number.isInteger(value) && value > 0 ? value : undefined;
    }

    if (typeof value === 'string') {
        const processId = Number(value);
        return Number.isInteger(processId) && processId > 0 ? processId : undefined;
    }

    return undefined;
}

class ResourceDebugOperationTelemetry {
    private readonly _startedAt: number | undefined;
    private _resourceType: ResourceDebugResourceType | undefined;
    private _provider: ResourceAttachProvider['id'] | 'none' = 'none';
    private _state: ResourceDebugResourceState = 'unknown';
    private _debuggerRequirement: ResourceDebugDebuggerRequirement = 'none';
    private _debugStartAt: number | undefined;
    private _debugStartAttempted = false;

    constructor(
        private readonly _telemetry: ResourceDebugTelemetry,
        private readonly _clock: ResourceDebugClock,
        private readonly _source: ResourceDebugRequest['source'],
        private readonly _requestedStrategy: ResourceDebugRequestedStrategyTelemetryBucket,
    ) {
        this._startedAt = this._getTimestamp();
    }

    recordStart(): void {
        this._record(() => this._telemetry.recordStart({
            source: this._source,
            requested_strategy: this._requestedStrategy,
            controller: 'editor',
        }));
    }

    recordResource(resource: ResourceJson): void {
        this._record(() => {
            this._resourceType = getResourceTypeBucket(resource.resourceType);
            this._state = resource.state === 'Running'
                ? 'running'
                : resource.state === null
                    ? 'unknown'
                    : 'notRunning';
        });
    }

    recordProvider(provider: ResourceAttachProvider): void {
        this._record(() => {
            this._provider = provider.id;
        });
    }

    recordDebuggerRequirement(requirement: ResourceDebugDebuggerRequirement): void {
        this._record(() => {
            this._debuggerRequirement = requirement;
        });
    }

    recordDebugStart(): void {
        this._record(() => {
            this._debugStartAttempted = true;
            this._debugStartAt = this._getTimestamp();
        });
    }

    createSessionMetadata(
        provider: ResourceAttachProvider['id'],
        requestedStrategy: ResourceDebugStrategy,
        effectiveStrategy: 'attach',
    ): ResourceDebugAttachSessionMetadata {
        return {
            source: this._source,
            provider,
            resource_type: this._resourceType ?? 'other',
            requested_strategy: requestedStrategy,
            effective_strategy: effectiveStrategy,
        };
    }

    recordResult(result: ResourceDebugResult): void {
        this._record(() => this._telemetry.recordResult({
            source: this._source,
            provider: this._provider,
            ...(this._resourceType === undefined ? {} : { resource_type: this._resourceType }),
            requested_strategy: this._requestedStrategy,
            effective_strategy: result.outcome === 'started' || result.outcome === 'alreadyDebugging'
                ? 'attach'
                : 'none',
            outcome: result.outcome,
            controller: 'editor',
            state: this._state,
            debugger_requirement: this._debuggerRequirement,
            error_kind: result.outcome === 'error' ? result.errorKind : 'none',
        }, this._getMeasurements()));
    }

    private _getMeasurements(): ResourceDebugResultTelemetryMeasurements {
        const endedAt = this._getTimestamp();
        const resolutionDuration = this._getDuration(
            this._startedAt,
            this._debugStartAttempted ? this._debugStartAt : endedAt);
        const debugStartDuration = this._debugStartAttempted
            ? this._getDuration(this._debugStartAt, endedAt)
            : undefined;
        const totalDuration = this._getDuration(this._startedAt, endedAt);

        return {
            ...(resolutionDuration === undefined ? {} : { resolution_duration_ms: resolutionDuration }),
            ...(debugStartDuration === undefined ? {} : { debug_start_duration_ms: debugStartDuration }),
            ...(totalDuration === undefined ? {} : { total_duration_ms: totalDuration }),
        };
    }

    private _getTimestamp(): number | undefined {
        try {
            const timestamp = this._clock.now();
            return Number.isFinite(timestamp) ? timestamp : undefined;
        }
        catch {
            return undefined;
        }
    }

    private _getDuration(start: number | undefined, end: number | undefined): number | undefined {
        if (start === undefined || end === undefined) {
            return undefined;
        }

        const duration = end - start;
        return Number.isFinite(duration) && duration >= 0 ? duration : undefined;
    }

    private _record(record: () => void): void {
        try {
            record();
        }
        catch {
            // Telemetry is observational. A telemetry sink must not change debug behavior.
        }
    }
}

function getResourceTypeBucket(resourceType: unknown): ResourceDebugResourceType {
    switch (typeof resourceType === 'string' ? resourceType.toLowerCase() : '') {
        case 'project':
            return 'project';
        case 'executable':
            return 'executable';
        case 'container':
            return 'container';
        default:
            return 'other';
    }
}

function getRequestedStrategy(strategy: unknown): ResourceDebugStrategy | undefined {
    return strategy === 'auto' || strategy === 'attach' ? strategy : undefined;
}

function selectEffectiveStrategy(strategy: ResourceDebugStrategy | undefined): 'attach' | undefined {
    switch (strategy) {
        case 'auto':
        case 'attach':
            return 'attach';
        default:
            return undefined;
    }
}
