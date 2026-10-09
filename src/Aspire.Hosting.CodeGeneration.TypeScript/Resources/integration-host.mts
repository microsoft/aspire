import * as net from 'node:net';
import {
    RequestType3,
    createMessageConnection,
    StreamMessageReader,
    StreamMessageWriter,
    type MessageConnection,
    type CancellationToken as RpcCancellationToken,
} from 'vscode-jsonrpc/node.js';
// Register generated factories before dispatch wraps incoming server handles.
import './aspire.mjs';
import {
    getAspireExport,
    type AspireExportedFunction,
    type AspireExportMetadata,
    type AspireIntegrationDefinition,
    type AspireCapabilityParameter,
} from './base.mjs';
import { AspireClient, CancellationToken, wrapIfHandle, type AspireClientRpc } from './transport.mjs';

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
 * `defineIntegration` helper (both emitted into the generated `.aspire/modules/base.mjs`).
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
    const registrationId = process.env.ASPIRE_INTEGRATION_HOST_REGISTRATION_ID;

    if (!socketPath || !registrationId) {
        console.error("ERROR: Integration hosts must be launched by the AppHost server.");
        process.exit(1);
    }

    const connectPath = process.platform === 'win32' && !socketPath.startsWith('\\\\.\\pipe\\')
        ? `\\\\.\\pipe\\${socketPath}`
        : socketPath;

    log(`Connecting to engine: ${connectPath}`);

    const socket = net.createConnection(connectPath);
    socket.on('close', () => {
        log("Engine disconnected, shutting down");
        process.exit(0);
    });
    await new Promise<void>((resolve, reject) => {
        socket.once('connect', resolve);
        socket.once('error', reject);
    });

    const connection: MessageConnection = createMessageConnection(
        new StreamMessageReader(socket),
        new StreamMessageWriter(socket),
        undefined,
        { cancellationStrategy: undefined });

    const shutdown = () => {
        log("Shutting down");
        connection.dispose();
        socket.destroy();
        process.exit(0);
    };
    process.on('SIGINT', shutdown);
    process.on('SIGTERM', shutdown);

    connection.onError(error => {
        console.error(`[${host.packageName}] Connection error:`, error);
    });
    connection.onClose(() => {
        log("Connection closed");
    });

    const client = AspireClient.fromConnection(connection, socket, log);

    // Callback relay — when an integration calls a guest-owned callback, we
    // route it through the AppHost server's invokeGuestCallback method.
    const invokeGuestCallback = async (callbackId: string, positionalArgs: readonly unknown[], projection: AspireCapabilityParameter): Promise<unknown> => {
        const callArgs: JsonObject = {};
        for (let i = 0; i < positionalArgs.length; i++) {
            callArgs[`p${i}`] = positionalArgs[i];
        }
        log(`Invoking guest callback ${callbackId} (${positionalArgs.length} positional arg(s))`);
        try {
            const result = await connection.sendRequest<unknown>('invokeGuestCallback', callbackId, callArgs);
            log(`Guest callback ${callbackId} completed`);
            if (projection.callbackReturnType?.typeId === 'void') {
                // Void callbacks return { p0: modifiedDto, p1: ... }. Only projected DTO
                // parameters participate in writeback; handle arguments remain server-owned.
                if (typeof result === 'object' && result !== null && !Array.isArray(result)) {
                    for (const [index, parameter] of (projection.callbackParameters ?? []).entries()) {
                        const original = positionalArgs[index];
                        const modified = (result as JsonObject)[`p${index}`];
                        if (parameter.type.category === 'Dto'
                            && typeof original === 'object' && original !== null && !Array.isArray(original)
                            && typeof modified === 'object' && modified !== null && !Array.isArray(modified)) {
                            Object.assign(original, wrapIfHandle(modified, client));
                        }
                    }
                }
                return undefined;
            }
            return wrapIfHandle(result, client);
        } catch (error) {
            log(`Guest callback ${callbackId} failed: ${error instanceof Error ? error.message : String(error)}`);
            throw error;
        }
    };

    connection.listen();

    const authToken = process.env.ASPIRE_REMOTE_APPHOST_TOKEN;
    if (authToken) {
        const authenticated = await connection.sendRequest<boolean>('authenticate', authToken);
        if (!authenticated) {
            throw new Error('Integration host authentication failed.');
        }
        log(`Authenticated: ${authenticated}`);
    }

    const ping = await connection.sendRequest<string>('ping');
    log(`Ping: ${ping}`);

    // Build a flat capability map keyed by capability id.
    const allCapabilities = host.integrations.flatMap(integration => integration.capabilities);
    const capabilityMap = new Map<string, AspireExportedFunction<JsonObject, unknown>>();
    for (const fn of allCapabilities) {
        const meta = getAspireExport(fn);
        if (!meta) {
            throw new Error('Integration exports must be AspireExport-wrapped functions.');
        }
        if (capabilityMap.has(meta.id)) {
            throw new Error(`Duplicate integration capability: ${meta.id}`);
        }
        capabilityMap.set(meta.id, fn);
    }

    connection.onRequest(
        new RequestType3<string, JsonObject | undefined, string, unknown, void>('handleExternalCapability'),
        async (capabilityId: string, args: JsonObject | undefined, invocationId: string, cancellation: RpcCancellationToken) => {
            const fn = capabilityMap.get(capabilityId);
            if (!fn) {
                throw new Error(`Unknown capability: ${capabilityId}`);
            }
            const meta = getAspireExport(fn)!;

            const wrappedArgs = wrapRemoteValue({ ...(args ?? {}) }, client) as JsonObject;

            // For any parameter the projection marks as `isCallback`, replace
            // the wire-format callback id string with a JS function that routes
            // back through invokeGuestCallback. User code can call it like any
            // normal closure.
            const params = meta.projection?.parameters ?? [];
            const controller = new AbortController();
            const cancellationRegistration = cancellation?.onCancellationRequested(() => controller.abort());
            if (cancellation?.isCancellationRequested) {
                controller.abort();
            }
            for (const param of params) {
                if (param.isCallback && typeof wrappedArgs[param.name] === 'string') {
                    const callbackId = wrappedArgs[param.name] as string;
                    wrappedArgs[param.name] = async (...callbackArgs: unknown[]) =>
                        invokeGuestCallback(callbackId, callbackArgs, param);
                }
                if (param.type?.typeId === 'cancellationToken') {
                    wrappedArgs[param.name] = CancellationToken.from(controller.signal);
                }
            }

            const started = performance.now();
            let phase = 'integration code';
            const stalled = setInterval(() =>
                log(`Still executing ${capabilityId} (invocation ${invocationId}, PID ${process.pid}, elapsed ${Math.round(performance.now() - started)}ms); awaiting ${phase}`),
                10_000);
            stalled.unref();
            log(`Dispatching ${capabilityId} (invocation ${invocationId}, PID ${process.pid})`);

            try {
                const result = await client.runWithPendingPromises(async () => {
                    try {
                        return await fn(wrappedArgs);
                    } finally {
                        phase = 'fluent RPC work';
                        log(`Integration code finished for ${capabilityId} (invocation ${invocationId}); draining fluent RPC work`);
                    }
                });
                log(`Completed ${capabilityId} (invocation ${invocationId}, elapsed ${Math.round(performance.now() - started)}ms)`);
                return result;
            } catch (error) {
                log(`Failed ${capabilityId} (invocation ${invocationId}, elapsed ${Math.round(performance.now() - started)}ms): ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
                throw error;
            } finally {
                clearInterval(stalled);
                cancellationRegistration?.dispose();
            }
        });

    connection.onRequest('getCapabilities', () => {
        log(`getCapabilities called (${allCapabilities.length} capability/capabilities)`);
        return {
            protocolVersion: 2,
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

    const registered = await connection.sendRequest<boolean>('registerAsIntegrationHost', registrationId);
    if (!registered) {
        throw new Error('Integration host registration was rejected.');
    }
    log(`Registered integrations: ${host.integrations.map(integration => integration.name).join(', ')}`);
}

function isRemoteHandle(value: unknown): value is RemoteHandle
{
    return typeof value === 'object'
        && value !== null
        && '$handle' in value
        && '$type' in value;
}

function wrapRemoteValue(value: unknown, client: AspireClientRpc): unknown
{
    if (isRemoteHandle(value)) {
        return wrapIfHandle(value, client);
    }

    if (Array.isArray(value)) {
        return value.map(item => wrapRemoteValue(item, client));
    }

    if (typeof value === 'object' && value !== null) {
        const result: JsonObject = {};
        for (const [key, nestedValue] of Object.entries(value)) {
            result[key] = wrapRemoteValue(nestedValue, client);
        }

        return result;
    }

    return value;
}
