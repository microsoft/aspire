// aspire.mts - Capability-based Aspire SDK
// This SDK uses the ATS (Aspire Type System) capability API.
// Capabilities are endpoints like 'Aspire.Hosting/createBuilder'.
//
// GENERATED CODE - DO NOT EDIT

import {
    AspireClient,
    Handle,
    MarshalledHandle,
    AppHostUsageError,
    CancellationToken,
    CapabilityError,
    registerCallback,
    wrapIfHandle,
    registerHandleWrapper,
    isPromiseLike
} from './transport.mjs';
import type { AspireClientRpc } from './transport.mjs';

import type { HandleReference } from './base.mjs';

import {
    ResourceBuilderBase,
    ReferenceExpression,
    refExpr,
    AspireDict,
    AspireList,
    createFluentPromiseClass as $aspireCreateFluentPromiseClass,
    InteractionInputCollectionPromiseImpl
} from './base.mjs';

export {
    InputType,
    InteractionInputCollection
} from './base.mjs';

export type {
    InteractionInput,
    InteractionInputOption,
    InteractionInputCollectionPromise
} from './base.mjs';

import type {
    Awaitable,
    FluentPromiseTransitions as $aspireFluentPromiseTransitions,
    InteractionInput,
    InteractionInputCollection,
    InteractionInputCollectionPromise,
    InputType
} from './base.mjs';

// ============================================================================
// Handle Type Aliases (Internal - not exported to users)
// ============================================================================

/** Represents an arbitrary Kubernetes manifest that is emitted with a Kubernetes service. */
type KubernetesManifestResourceHandle = Handle<'Aspire.Hosting.Kubernetes/Aspire.Hosting.Kubernetes.KubernetesManifestResource'>;

// ============================================================================
// KubernetesManifestResource
// ============================================================================

/** Represents an arbitrary Kubernetes manifest that is emitted with a Kubernetes service. */
export interface KubernetesManifestResource {
    toJSON(): MarshalledHandle;
    /**
     * Adds or updates a manifest field using a dot-separated path.
     *
     * Values can be JSON-compatible primitives, objects, or arrays. To preserve keys containing dots, such as
     * `app.kubernetes.io/component`, pass an object containing those keys at the parent field path.
     * Setting a field replaces any existing value at that path.
     * @param path The dot-separated manifest field path, for example `spec.replicas` or `data.username`.
     * @param value The JSON-compatible field value.
     * @returns This manifest resource.
     */
    withField(path: string, value: any): KubernetesManifestResourcePromise;
}

export interface KubernetesManifestResourcePromise extends PromiseLike<KubernetesManifestResource> {
    /**
     * Adds or updates a manifest field using a dot-separated path.
     *
     * Values can be JSON-compatible primitives, objects, or arrays. To preserve keys containing dots, such as
     * `app.kubernetes.io/component`, pass an object containing those keys at the parent field path.
     * Setting a field replaces any existing value at that path.
     * @param path The dot-separated manifest field path, for example `spec.replicas` or `data.username`.
     * @param value The JSON-compatible field value.
     * @returns This manifest resource.
     */
    withField(path: string, value: any): KubernetesManifestResourcePromise;
}

// ============================================================================
// KubernetesManifestResourceImpl
// ============================================================================

/** Represents an arbitrary Kubernetes manifest that is emitted with a Kubernetes service. */
class KubernetesManifestResourceImpl implements KubernetesManifestResource {
    constructor(private _handle: KubernetesManifestResourceHandle, private _client: AspireClientRpc) {}

    /** Serialize for JSON-RPC transport */
    toJSON(): MarshalledHandle { return this._handle.toJSON(); }

    /** @internal */
    async _withFieldInternal(path: string, value: any): Promise<KubernetesManifestResource> {
        const rpcArgs: Record<string, unknown> = { context: this._handle, path, value };
        const result = await this._client.invokeCapability<KubernetesManifestResourceHandle>(
            'Aspire.Hosting.Kubernetes/withField',
            rpcArgs
        );
        return new KubernetesManifestResourceImpl(result, this._client);
    }

    /**
     * Adds or updates a manifest field using a dot-separated path.
     *
     * Values can be JSON-compatible primitives, objects, or arrays. To preserve keys containing dots, such as
     * `app.kubernetes.io/component`, pass an object containing those keys at the parent field path.
     * Setting a field replaces any existing value at that path.
     * @param path The dot-separated manifest field path, for example `spec.replicas` or `data.username`.
     * @param value The JSON-compatible field value.
     * @returns This manifest resource.
     */
    withField(path: string, value: any): KubernetesManifestResourcePromise {
        return new KubernetesManifestResourcePromiseImpl(this._withFieldInternal(path, value), this._client);
    }

}

/** @internal */
const KubernetesManifestResourcePromiseImpl = $aspireCreateFluentPromiseClass<KubernetesManifestResource, KubernetesManifestResourcePromise>((): $aspireFluentPromiseTransitions => ({
    ["withField"]: () => KubernetesManifestResourcePromiseImpl,
}));

// ============================================================================
// Connection Helper
// ============================================================================

/**
 * Creates and connects to the Aspire AppHost.
 * Reads connection info from environment variables set by `aspire run`.
 */
export async function connect(): Promise<AspireClientRpc> {
    const socketPath = process.env.REMOTE_APP_HOST_SOCKET_PATH;
    if (!socketPath) {
        throw new Error(
            'REMOTE_APP_HOST_SOCKET_PATH environment variable not set. ' +
            'Run this application using `aspire run`.'
        );
    }

    const client = new AspireClient(socketPath);
    await client.connect();

    // Exit the process if the server connection is lost
    client.onDisconnect(() => {
        console.error('Connection to AppHost lost. Exiting...');
        process.exit(1);
    });

    return client;
}

/**
 * Creates a new distributed application builder.
 * This is the entry point for building Aspire applications.
 *
 * @param options - Optional configuration options for the builder
 * @returns A DistributedApplicationBuilder instance
 *
 * @example
 * const builder = await createBuilder();
 * await builder.addRedis("cache");
 * await builder.addContainer("api", "mcr.microsoft.com/dotnet/samples:aspnetapp");
 * const app = await builder.build();
 * await app.run();
 */
export async function createBuilder(options?: CreateBuilderOptions): Promise<DistributedApplicationBuilder> {
    const client = await connect();

    // Apply client-side options before any tracking begins
    if (options?.throwOnPendingRejections === false) {
        client.throwOnPendingRejections = false;
    }

    // Default args, projectDirectory, and appHostFilePath if not provided
    // ASPIRE_APPHOST_FILEPATH is set by the CLI for consistent socket hash computation
    const effectiveOptions: CreateBuilderOptions = {
        ...options,
        args: options?.args ?? process.argv.slice(2),
        projectDirectory: options?.projectDirectory ?? process.env.ASPIRE_PROJECT_DIRECTORY ?? process.cwd(),
        appHostFilePath: options?.appHostFilePath ?? process.env.ASPIRE_APPHOST_FILEPATH
    };

    // Strip client-only options before sending to the host
    delete effectiveOptions.throwOnPendingRejections;

    const handle = await client.invokeCapability<IDistributedApplicationBuilderHandle>(
        'Aspire.Hosting/createBuilder',
        { argsOrOptions: effectiveOptions }
    );
    return new DistributedApplicationBuilderImpl(handle, client);
}

// Re-export commonly used types
export { Handle, AppHostUsageError, CancellationToken, CapabilityError, registerCallback } from './transport.mjs';
export { refExpr, ReferenceExpression } from './base.mjs';
export type { HandleReference, Awaitable } from './base.mjs';

// ============================================================================
// Global Error Handling
// ============================================================================

/**
 * Set up global error handlers to ensure the process exits properly on errors.
 * Node.js doesn't exit on unhandled rejections by default, so we need to handle them.
 */
process.on('unhandledRejection', (reason: unknown) => {
    const error = reason instanceof Error ? reason : new Error(String(reason));

    if (reason instanceof AppHostUsageError) {
        console.error(`\n❌ AppHost Error: ${error.message}`);
    } else if (reason instanceof CapabilityError) {
        console.error(`\n❌ Capability Error: ${error.message}`);
        console.error(`   Code: ${(reason as CapabilityError).code}`);
        if ((reason as CapabilityError).capability) {
            console.error(`   Capability: ${(reason as CapabilityError).capability}`);
        }
    } else {
        console.error(`\n❌ Unhandled Error: ${error.message}`);
        if (error.stack) {
            console.error(error.stack);
        }
    }

    process.exit(1);
});

process.on('uncaughtException', (error: Error) => {
    if (error instanceof AppHostUsageError) {
        console.error(`\n❌ AppHost Error: ${error.message}`);
    } else if (error instanceof CapabilityError) {
        console.error(`\n❌ Capability Error: ${error.message}`);
        console.error(`   Code: ${error.code}`);
        if (error.capability) {
            console.error(`   Capability: ${error.capability}`);
        }
    } else {
        console.error(`\n❌ Uncaught Exception: ${error.message}`);
    }
    // Suppress stack traces for structured errors (AppHostUsageError, CapabilityError)
    // to keep polyglot output clean. Use --verbose for full diagnostics.
    if (!(error instanceof AppHostUsageError) && !(error instanceof CapabilityError) && error.stack) {
        console.error(error.stack);
    }
    process.exit(1);
});

// ============================================================================
// Handle Wrapper Registrations
// ============================================================================

// Register wrapper factories for typed handle wrapping in callbacks
registerHandleWrapper('Aspire.Hosting.Kubernetes/Aspire.Hosting.Kubernetes.KubernetesManifestResource', (handle, client) => new KubernetesManifestResourceImpl(handle as KubernetesManifestResourceHandle, client));