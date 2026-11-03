import * as net from 'node:net';
import { randomBytes } from 'node:crypto';
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

const invocationSignals = new WeakMap<object, AbortSignal>();

/**
 * Gets host cancellation for the original capability argument bag, without
 * reserving an authored parameter name. Available in managed and native hosts.
 */
export function getIntegrationInvocationSignal(args: object): AbortSignal
{
    const signal = invocationSignals.get(args);
    if (!signal) {
        throw new Error('The argument bag does not belong to an active integration invocation.');
    }
    return signal;
}

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

/** Integration-owned object whose lifetime is an authoritative native declaration.
 * This convenience class projects a portable $lifetime handle, not a CLR-only resource model.
 */
export class IntegrationResource {
    constructor(readonly declaration: { toJSON(): RemoteHandle }, readonly typeId: string) {}
    /** Releases provider-local state after native declaration retirement. */
    async dispose(): Promise<void> {}
}

// ============================================================================
// Integration host framework: socket, auth, registration, dispatch.
// ============================================================================

export async function runIntegrationHost(host: IntegrationHostDefinition): Promise<void>
{
    const log = (message: string) => console.log(`[${host.packageName}] ${message}`);
    const socketPath = process.env.REMOTE_APP_HOST_SOCKET_PATH;
    const nativeEndpoint = process.env.ASPIRE_NATIVE_RPC_ENDPOINT;
    const registrationId = process.env.ASPIRE_INTEGRATION_HOST_REGISTRATION_ID;

    if ((!socketPath && !nativeEndpoint) || !registrationId) {
        console.error("ERROR: Integration hosts must be launched by the AppHost server.");
        process.exit(1);
    }

    const connectPath = socketPath && process.platform === 'win32' && !socketPath.startsWith('\\\\.\\pipe\\')
        ? `\\\\.\\pipe\\${socketPath}`
        : socketPath;

    log(`Connecting to engine: ${connectPath}`);

    // Native activation supplies a loopback endpoint such as 127.0.0.1:43210.
    // The existing managed integration-host Unix socket/named-pipe bootstrap stays intact.
    const nativeAddress = nativeEndpoint ? /^127\.0\.0\.1:(\d+)$/.exec(nativeEndpoint) : undefined;
    if (nativeEndpoint && !nativeAddress) {
        throw new Error('Native integration activation requires an IPv4 loopback endpoint.');
    }
    const socket = nativeAddress
        ? net.createConnection({ host: '127.0.0.1', port: Number(nativeAddress[1]) })
        : net.createConnection(connectPath!);
    socket.on('close', () => {
        log("Engine disconnected, shutting down");
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

    let releaseAll: () => Promise<void> = async () => {};
    let shuttingDown = false;
    const shutdown = async () => {
        if (shuttingDown) {
            return;
        }
        shuttingDown = true;
        log("Shutting down");
        // Integration-owned cleanup must not keep an orphan alive after the
        // engine disconnects or its process supervisor requests shutdown.
        const deadline = setTimeout(() => {
            console.error('Integration object cleanup timed out; forcing process exit.');
            process.exit(1);
        }, 5000);
        let exitCode = 0;
        try {
            await releaseAll();
        } catch {
            console.error('Integration object cleanup failed; forcing process exit.');
            exitCode = 1;
        } finally {
            clearTimeout(deadline);
            connection.dispose();
            socket.destroy();
            process.exit(exitCode);
        }
    };
    process.on('SIGINT', shutdown);
    process.on('SIGTERM', shutdown);

    connection.onError(error => {
        console.error(`[${host.packageName}] Connection error:`, nativeEndpoint ? 'Native RPC connection failed.' : error);
    });
    connection.onClose(() => {
        log("Connection closed");
        return shutdown();
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
            if (nativeEndpoint) {
                log(`Guest callback ${callbackId} failed.`);
                throw new Error('The native guest callback failed.');
            }
            log(`Guest callback ${callbackId} failed: ${error instanceof Error ? error.message : String(error)}`);
            throw error;
        }
    };

    connection.listen();

    const authToken = nativeEndpoint ? process.env.ASPIRE_NATIVE_RPC_TOKEN : process.env.ASPIRE_REMOTE_APPHOST_TOKEN;
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
    const resources = new Map<string, IntegrationResource>();
    const identities = new WeakMap<IntegrationResource, string>();
    const released = new WeakSet<IntegrationResource>();
    const invocations = new Map<string, { controller: AbortController; completion: Promise<void>; resources: Set<IntegrationResource> }>();
    const releaseResource = async (id: string, waitForInvocations = true): Promise<boolean> => {
        const resource = resources.get(id);
        if (!resource) {
            return true;
        }
        resources.delete(id);
        identities.delete(resource);
        released.add(resource);
        const active = [...invocations.values()].filter(invocation => invocation.resources.has(resource));
        for (const invocation of active) {
            invocation.controller.abort();
        }
        if (waitForInvocations) {
            await Promise.all(active.map(invocation => invocation.completion));
        }
        await resource.dispose();
        return true;
    };
    releaseAll = async () => {
        for (const invocation of invocations.values()) {
            invocation.controller.abort();
        }
        // On disconnect native scopes are already revoked. Clear every local
        // identity even if integration code ignores its cancellation signal.
        await Promise.all([...resources.keys()].map(id => releaseResource(id, false)));
    };
    connection.onRequest('releaseIntegrationResource', async (id: string) => {
        try {
            return await releaseResource(id);
        } catch {
            throw new Error('The native integration object release failed.');
        }
    });
    connection.onRequest('cancelProviderInvocation', async (id: string) => {
        const invocation = invocations.get(id);
        if (invocation) {
            invocation.controller.abort();
            await invocation.completion;
        }
        return true;
    });
    const encodeResult = (value: unknown): unknown => {
        if (!(value instanceof IntegrationResource)) {
            return value;
        }
        if (released.has(value)) {
            throw new Error('An integration resource cannot be republished after native retirement.');
        }
        let id = identities.get(value);
        if (!id) {
            if (resources.size >= 4096) {
                throw new Error('Integration resource capacity was reached.');
            }
            id = randomBytes(32).toString('hex');
            identities.set(value, id);
            resources.set(id, value);
        }
        return { $handle: id, $type: value.typeId, $lifetime: value.declaration.toJSON() };
    };
    const decodeArguments = (value: unknown, invocationResources: Set<IntegrationResource>): unknown => {
        if (isRemoteHandle(value) && resources.has(value.$handle)) {
            const resource = resources.get(value.$handle)!;
            if (resource.typeId !== value.$type) {
                throw new Error('Integration resource type mismatch.');
            }
            invocationResources.add(resource);
            return resource;
        }
        if (Array.isArray(value)) {
            return value.map(nested => decodeArguments(nested, invocationResources));
        }
        if (value && typeof value === 'object' && !isRemoteHandle(value)) {
            return Object.fromEntries(Object.entries(value).map(([key, nested]) => [key, decodeArguments(nested, invocationResources)]));
        }
        return wrapRemoteValue(value, client);
    };
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

            const invocationResources = new Set<IntegrationResource>();
            const wrappedArgs = decodeArguments({ ...(args ?? {}) }, invocationResources) as JsonObject;

            // For any parameter the projection marks as `isCallback`, replace
            // the wire-format callback id string with a JS function that routes
            // back through invokeGuestCallback. User code can call it like any
            // normal closure.
            const params = meta.projection?.parameters ?? [];
            const controller = new AbortController();
            let completeInvocation!: () => void;
            const completion = new Promise<void>(resolve => { completeInvocation = resolve; });
            if (invocations.has(invocationId)) {
                throw new Error('An integration invocation cannot be replayed.');
            }
            invocations.set(invocationId, { controller, completion, resources: invocationResources });
            // Keep invocation state out of the projected parameter namespace,
            // including capabilities that legitimately declare a "signal" parameter.
            invocationSignals.set(wrappedArgs, controller.signal);
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
                return encodeResult(result);
            } catch (error) {
                if (nativeEndpoint) {
                    log(`Failed ${capabilityId} (invocation ${invocationId}).`);
                    throw new Error('The native integration invocation failed.');
                }
                log(`Failed ${capabilityId} (invocation ${invocationId}, elapsed ${Math.round(performance.now() - started)}ms): ${error instanceof Error ? error.stack ?? error.message : String(error)}`);
                throw error;
            } finally {
                clearInterval(stalled);
                cancellationRegistration?.dispose();
                invocations.delete(invocationId);
                invocationSignals.delete(wrappedArgs);
                completeInvocation();
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
