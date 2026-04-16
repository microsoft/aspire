import * as net from 'node:net';
import {
    RequestType2,
    createMessageConnection,
    StreamMessageReader,
    StreamMessageWriter,
    type MessageConnection,
} from 'vscode-jsonrpc/node.js';
// Side-effect import: loading this module triggers its top-level
// registerHandleWrapper(...) calls, populating the transport.ts handle-wrapper
// registry with factories for every type in the generated SDK. Without this
// import `wrapIfHandle` would return bare Handle instances with no methods.
import '../.modules/aspire.js';
import type { DistributedApplicationBuilder } from '../.modules/aspire.js';
import {
    getAspireExport,
    type AspireExportedFunction,
    type AspireExportMetadata,
    type AspireIntegrationDefinition,
} from '../.modules/base.js';
import { wrapIfHandle, type AspireClientRpc } from '../.modules/transport.js';

export type JsonObject = Record<string, unknown>;

/**
 * A handle to a server-owned object, as it arrives over the wire from the AppHost server.
 * The host runtime wraps these into the generated Impl classes before dispatching to user code.
 */
export interface RemoteHandle
{
    $handle: string;
    $type: string;
}

/**
 * The set of integrations a host process loads. Each integration's exported
 * capabilities are AspireExport-wrapped functions rolled up via the
 * `defineIntegration` helper (both emitted into the generated `.modules/base.js`).
 */
export interface IntegrationHostDefinition
{
    packageName: string;
    integrations: readonly AspireIntegrationDefinition[];
}

// ============================================================================
// Integration host framework: socket, auth, registration, dispatch.
// ============================================================================

export async function runIntegrationHost(host: IntegrationHostDefinition): Promise<void>
{
    const log = (message: string) => console.log(`[${host.packageName}] ${message}`);
    const socketPath = process.env.REMOTE_APP_HOST_SOCKET_PATH;

    if (!socketPath) {
        console.error("ERROR: REMOTE_APP_HOST_SOCKET_PATH not set");
        console.error("Start the AppHost Server first, then set the env var.");
        process.exit(1);
    }

    const connectPath = process.platform === 'win32' && !socketPath.startsWith('\\\\.\\pipe\\')
        ? `\\\\.\\pipe\\${socketPath}`
        : socketPath;

    log(`Connecting to engine: ${connectPath}`);

    const socket = net.createConnection(connectPath);
    await new Promise<void>((resolve, reject) => {
        socket.once('connect', resolve);
        socket.once('error', reject);
    });

    const connection: MessageConnection = createMessageConnection(
        new StreamMessageReader(socket),
        new StreamMessageWriter(socket),
        undefined,
        { cancellationStrategy: undefined });

    connection.onError(error => {
        console.error(`[${host.packageName}] Connection error:`, error);
    });
    connection.onClose(() => {
        log("Connection closed");
    });
    connection.listen();

    const authToken = process.env.ASPIRE_REMOTE_APPHOST_TOKEN;
    if (authToken) {
        const authenticated = await connection.sendRequest<boolean>('authenticate', authToken);
        log(`Authenticated: ${authenticated}`);
    }

    const ping = await connection.sendRequest<string>('ping');
    log(`Ping: ${ping}`);

    // Raw invokeCapability — used by both the generated lib (through AspireClientRpc)
    // and the callback relay.
    const invokeCapability = async <TResult>(capabilityId: string, args: JsonObject): Promise<TResult> => {
        try {
            const result = await connection.sendRequest<TResult | { $error: { code: string; message: string } }>(
                'invokeCapability',
                capabilityId,
                args);

            if (typeof result === 'object' && result !== null && '$error' in result) {
                throw new Error(`ATS Error [${result.$error.code}]: ${result.$error.message}`);
            }

            return result as TResult;
        } catch (error) {
            log(`Engine capability ${capabilityId} failed: ${error instanceof Error ? error.message : String(error)}`);
            throw error;
        }
    };

    // Callback relay — when an integration calls a guest-owned callback, we
    // route it through the AppHost server's invokeGuestCallback method.
    const invokeGuestCallback = async <TResult>(callbackId: string, positionalArgs: readonly unknown[]): Promise<TResult> => {
        const callArgs: JsonObject = {};
        for (let i = 0; i < positionalArgs.length; i++) {
            callArgs[`p${i}`] = positionalArgs[i] as never;
        }
        log(`Invoking guest callback ${callbackId} (${positionalArgs.length} positional arg(s))`);
        try {
            const result = await connection.sendRequest<TResult>('invokeGuestCallback', callbackId, callArgs);
            log(`Guest callback ${callbackId} completed`);
            return result;
        } catch (error) {
            log(`Guest callback ${callbackId} failed: ${error instanceof Error ? error.message : String(error)}`);
            throw error;
        }
    };

    // A lightweight AspireClientRpc implementation backed by this host's JSON-RPC
    // connection. Passed to the generated `Impl` classes when we wrap incoming
    // handles before dispatching to user code.
    //
    // Responses from `invokeCapability` get routed through `wrapIfHandle` so that
    // raw {$handle, $type} JSON arriving off the wire is upgraded into proper
    // Handle instances (or typed wrappers via the registered factory) — which is
    // what the generated Impl classes expect to hold in their `_handle` fields.
    const client: AspireClientRpc = {
        connected: true,
        invokeCapability: async <T>(id: string, args?: Record<string, unknown>): Promise<T> => {
            const raw = await invokeCapability<unknown>(id, (args ?? {}) as JsonObject);
            return wrapIfHandle(raw, client) as T;
        },
        cancelToken: async () => true,
        trackPromise: () => {},
        flushPendingPromises: async () => {},
        throwOnPendingRejections: true,
    };

    // Build a flat capability map keyed by capability id.
    const allCapabilities = host.integrations.flatMap(integration => integration.capabilities);
    const capabilityMap = new Map<string, AspireExportedFunction<any, any>>();
    for (const fn of allCapabilities) {
        const meta = getAspireExport(fn);
        if (!meta) {
            log(`Warning: integration export is not an AspireExport-wrapped function, skipping`);
            continue;
        }
        capabilityMap.set(meta.id, fn);
    }

    connection.onRequest(
        new RequestType2<string, JsonObject | undefined, unknown, void>('handleExternalCapability'),
        async (capabilityId: string, args: JsonObject | undefined) => {
            const fn = capabilityMap.get(capabilityId);
            if (!fn) {
                throw new Error(`Unknown capability: ${capabilityId}`);
            }
            const meta = getAspireExport(fn)!;

            const wrappedArgs: JsonObject = { ...(args ?? {}) };

            // Upgrade any `builder`-named handle argument into a real
            // DistributedApplicationBuilder instance via the handle-wrapper
            // registry, so user code can call methods on it directly
            // (builder.addContainer(...).withImageTag(...)...). The generated
            // module already registers a factory for IDistributedApplicationBuilder,
            // so wrapIfHandle returns the right typed wrapper — we do not need
            // to import the private Impl class.
            if (isRemoteHandle(wrappedArgs.builder)) {
                wrappedArgs.builder = wrapIfHandle(wrappedArgs.builder, client) as DistributedApplicationBuilder;
            }

            // For any parameter the projection marks as `isCallback`, replace
            // the wire-format callback id string with a JS function that routes
            // back through invokeGuestCallback. User code can call it like any
            // normal closure.
            const params = meta.projection?.parameters ?? [];
            for (const param of params) {
                if (param.isCallback && typeof wrappedArgs[param.name] === 'string') {
                    const callbackId = wrappedArgs[param.name] as string;
                    wrappedArgs[param.name] = async (...callbackArgs: unknown[]) =>
                        invokeGuestCallback(callbackId, callbackArgs);
                }
            }

            log(`Dispatching ${capabilityId}`);

            try {
                const result = await fn(wrappedArgs as any);
                log(`Completed ${capabilityId}`);
                return result;
            } catch (error) {
                log(`Failed ${capabilityId}: ${error instanceof Error ? error.message : String(error)}`);
                throw error;
            }
        });

    connection.onRequest('getCapabilities', () => {
        log(`getCapabilities called (${allCapabilities.length} capability/capabilities)`);
        return {
            capabilities: allCapabilities
                .map(fn => getAspireExport(fn))
                .filter((m): m is AspireExportMetadata => m !== undefined)
                .map(meta => ({
                    id: meta.id,
                    method: meta.method,
                    description: meta.description,
                    ...meta.projection,
                })),
        };
    });

    await connection.sendRequest<boolean>('registerAsIntegrationHost');
    log(`Registered integrations: ${host.integrations.map(integration => integration.name).join(', ')}`);

    process.on('SIGINT', () => {
        log("Shutting down");
        connection.dispose();
        socket.destroy();
        process.exit(0);
    });

    socket.on('close', () => {
        log("Engine disconnected, shutting down");
        process.exit(0);
    });
}

function isRemoteHandle(value: unknown): value is RemoteHandle
{
    return typeof value === 'object'
        && value !== null
        && '$handle' in value
        && '$type' in value;
}

// Projection metadata types live in `../.modules/base.js` (AspireCapabilityProjection,
// AspireCapabilityParameter, AspireCallbackParameter, AspireTypeRef). Host runtime code
// reads them via `getAspireExport(fn).projection`. Integrations import them from the
// generated base when they need to reference a projection shape in their AspireExport
// metadata argument.
