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

// ============================================================================
// DTO Interfaces
// ============================================================================

/** Options for creating a distributed application builder from polyglot apphosts. */
export interface CreateBuilderOptions {
    /** The command line arguments. */
    args?: string[];
    /** The directory containing the AppHost project file. */
    projectDirectory?: string | null;
    /** The full path to the AppHost file (e.g., apphost.ts, apphost.py). Used for consistent socket path computation across CLI and AppHost. */
    appHostFilePath?: string | null;
    /** When containers are used, use this value to override the container registry. */
    containerRegistryOverride?: string | null;
    /** Determines whether the dashboard is disabled. */
    disableDashboard?: boolean;
    /** The application name to display in the dashboard. */
    dashboardApplicationName?: string | null;
    /** Allows the use of HTTP urls for the AppHost resource endpoint. */
    allowUnsecuredTransport?: boolean;
    /** Enables resource logging. */
    enableResourceLogging?: boolean;
    /** Whether compatible resources are watched by default. An explicit value overrides AppHost configuration; individual resources can override the default. AppHost edits and IDE ownership are configured separately. */
    defaultResourceWatchEnabled?: boolean | null;
    /** When false, pre-flush rejected promises are not re-thrown by build(). Default: true. */
    throwOnPendingRejections?: boolean;
}

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

