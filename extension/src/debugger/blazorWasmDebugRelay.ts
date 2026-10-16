import { randomUUID } from 'crypto';
import * as http from 'http';
import * as net from 'net';
import * as vscode from 'vscode';
import WebSocket, { WebSocketServer, type RawData } from 'ws';
import { AspireResourceExtendedDebugConfiguration } from '../dcp/types';
import { blazorWasmDebugRelayFailed } from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';
import { registerRunCleanup } from './runCleanupRegistry';

const inspectPathPlaceholder = '{browserInspectUriPath}';
const maxPayload = 256 * 1024 * 1024;
const browserTypes = new Set(['chrome', 'msedge', 'pwa-chrome', 'pwa-msedge']);

export function registerBlazorWasmDebugRelay(owner: AspireResourceExtendedDebugConfiguration): vscode.Disposable {
    let disposed = false;
    // Resolutions have independent cancellation lifetimes, even for the same bridge.
    const relays = new Set<Promise<BlazorWasmDebugRelay>>();
    const relayUris = new Set<string>();
    const provider = vscode.debug.registerDebugConfigurationProvider('*', {
        async resolveDebugConfigurationWithSubstitutedVariables(_folder, configuration, token) {
            if (configuration.runId !== owner.runId
                || configuration.debugSessionId !== owner.debugSessionId
                || configuration.resourceType !== 'browser'
                || !browserTypes.has(configuration.type)
                || typeof configuration.inspectUri !== 'string'
                || relayUris.has(configuration.inspectUri)) {
                return configuration;
            }

            // The native C# bridge emits ws://localhost:<port>{browserInspectUriPath}.
            // The legacy WASM backend uses /_framework/debug/ws-proxy?browser=... instead
            // and must keep its existing transport.
            const upstream = getNativeBridgeUrl(configuration.inspectUri);
            if (!upstream) {
                return configuration;
            }

            if (disposed || token?.isCancellationRequested) {
                return undefined;
            }

            try {
                const pending = createBlazorWasmDebugRelay(upstream);
                relays.add(pending);
                const relay = await pending;
                if (disposed || token?.isCancellationRequested) {
                    relays.delete(pending);
                    relay.dispose();
                    return undefined;
                }

                relayUris.add(relay.inspectUri);
                configuration.inspectUri = relay.inspectUri;
                return configuration;
            }
            catch (error) {
                extensionLogOutputChannel.error(`Failed to prepare the Blazor WASM debug relay: ${error instanceof Error ? error.message : String(error)}`);
                throw new Error(blazorWasmDebugRelayFailed);
            }
        },
    });
    const registration = {
        dispose() {
            if (disposed) {
                return;
            }
            disposed = true;
            provider.dispose();
            for (const pending of relays) {
                void pending.then(relay => relay.dispose(), error => {
                    extensionLogOutputChannel.debug(`Blazor WASM debug relay initialization failed before cleanup: ${error instanceof Error ? error.message : String(error)}`);
                });
            }
            relays.clear();
            relayUris.clear();
        },
    };
    registerRunCleanup(owner.runId, () => registration.dispose());
    return registration;
}

function getNativeBridgeUrl(inspectUri: string): string | undefined {
    if (!inspectUri.endsWith(inspectPathPlaceholder)) {
        return undefined;
    }
    const address = inspectUri.slice(0, -inspectPathPlaceholder.length);
    if (!URL.canParse(address)) {
        return undefined;
    }
    const url = new URL(address);
    if (url.protocol !== 'ws:'
        || !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)
        || !url.port || url.username || url.password
        || url.pathname !== '/' || url.search || url.hash) {
        return undefined;
    }
    return url.origin;
}

export interface BlazorWasmDebugRelay extends vscode.Disposable {
    readonly inspectUri: string;
}

export async function createBlazorWasmDebugRelay(upstream: string): Promise<BlazorWasmDebugRelay> {
    const prefix = `/${randomUUID()}`;
    const sockets = new Set<net.Socket>();
    const upstreams = new Set<WebSocket>();
    const server = http.createServer((_request, response) => response.writeHead(404).end());
    const webSockets = new WebSocketServer({ noServer: true, perMessageDeflate: false, maxPayload });
    let disposed = false;
    const dispose = () => {
        if (disposed) {
            return;
        }
        disposed = true;
        for (const socket of webSockets.clients) {
            socket.terminate();
        }
        for (const socket of upstreams) {
            socket.terminate();
        }
        for (const socket of sockets) {
            socket.destroy();
        }
        webSockets.close();
        server.close();
    };
    server.on('connection', socket => {
        sockets.add(socket);
        socket.on('close', () => sockets.delete(socket));
    });
    server.on('upgrade', (request, socket, head) => {
        const requestPath = request.url;
        if (disposed || request.headers.origin !== undefined
            || !requestPath
            || requestPath.includes('#')
            || (!requestPath.startsWith(`${prefix}/devtools/browser/`)
                && !requestPath.startsWith(`${prefix}/devtools/page/`))) {
            socket.end('HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n');
            return;
        }

        webSockets.handleUpgrade(request, socket, head, client => {
            let target: WebSocket | undefined;
            let pendingBytes = 0;
            const pending: Array<{ data: RawData; binary: boolean }> = [];
            const fail = (error: Error) => {
                if (!disposed && client.readyState !== WebSocket.CLOSED) {
                    extensionLogOutputChannel.warn(`Blazor WASM debug relay connection failed: ${error.message}`);
                }
                client.terminate();
                target?.terminate();
            };
            const forward = (source: WebSocket, destination: WebSocket, data: RawData, binary: boolean) => {
                source.pause();
                destination.send(data, { binary }, error => {
                    if (error) {
                        fail(error);
                    }
                    else {
                        source.resume();
                    }
                });
            };
            client.on('error', fail);
            client.on('close', () => {
                pending.length = 0;
                target?.terminate();
            });
            client.on('message', (data, binary) => {
                if (target?.readyState === WebSocket.OPEN) {
                    forward(client, target, data, binary);
                    return;
                }
                if (target && target.readyState !== WebSocket.CONNECTING) {
                    client.terminate();
                    return;
                }

                pendingBytes += Array.isArray(data)
                    ? data.reduce((length, buffer) => length + buffer.length, 0)
                    : data.byteLength;
                if (pendingBytes > maxPayload) {
                    fail(new Error('The pending Blazor WASM debugger data exceeds the relay buffer limit.'));
                    return;
                }
                pending.push({ data, binary });
                if (target) {
                    return;
                }

                // js-debug abandons unopened WebSockets after two seconds. Acknowledge locally
                // before touching the bridge, then wait for a CDP command: an abandoned opening
                // cannot send one and must never acquire the native debugger's first connection.
                // https://github.com/dotnet/vscode-csharp/issues/9890
                target = new WebSocket(`${upstream}${requestPath.slice(prefix.length)}`, {
                    perMessageDeflate: false,
                    maxPayload,
                    headers: { host: 'localhost' },
                });
                upstreams.add(target);
                target.on('error', fail);
                target.on('close', () => {
                    upstreams.delete(target!);
                    client.terminate();
                });
                target.on('open', () => {
                    for (const message of pending.splice(0)) {
                        forward(client, target!, message.data, message.binary);
                    }
                    pendingBytes = 0;
                });
                target.on('message', (message, isBinary) => {
                    if (client.readyState === WebSocket.OPEN) {
                        forward(target!, client, message, isBinary);
                    }
                });
            });
        });
    });
    const port = await new Promise<number>((resolve, reject) => {
        server.once('error', reject);
        server.listen(0, '127.0.0.1', () => {
            server.removeListener('error', reject);
            const address = server.address();
            if (!address || typeof address === 'string') {
                dispose();
                reject(new Error(blazorWasmDebugRelayFailed));
                return;
            }
            resolve(address.port);
        });
    });
    server.on('error', error => {
        extensionLogOutputChannel.error(`Blazor WASM debug relay server failed: ${error.message}`);
        dispose();
    });
    server.unref();
    return { inspectUri: `ws://127.0.0.1:${port}${prefix}${inspectPathPlaceholder}`, dispose };
}
