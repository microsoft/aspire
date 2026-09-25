import * as vscode from 'vscode';
import { createMessageConnection, ErrorCodes, MessageConnection, ResponseError } from 'vscode-jsonrpc';
import { StreamMessageReader, StreamMessageWriter } from 'vscode-jsonrpc/node';
import { invalidTokenProvided, rpcServerAddressError, rpcServerError } from '../loc/strings';
import { addInteractionServiceEndpoints, IInteractionService } from './interactionService';
import { ICliRpcClient } from './rpcClient';
import * as tls from 'tls';
import { createSelfSignedCertAsync, generateToken } from '../utils/security';
import { extensionLogOutputChannel } from '../utils/logging';
import { getSupportedCapabilities } from '../capabilities';
import { timingSafeEqual } from 'crypto';

export type RpcServerConnectionInfo = {
    address: string;
    token: string;
    cert: string;
};

export default class AspireRpcServer {
    public server: tls.Server;
    public connectionInfo: RpcServerConnectionInfo;
    public connections: ICliRpcClient[] = [];

    private readonly _ownedConnections = new Set<ICliRpcClient>();
    private _disposed = false;
    private _onNewConnection = new vscode.EventEmitter<ICliRpcClient>();
    public readonly onNewConnection = this._onNewConnection.event;

    constructor(server: tls.Server, connectionInfo: RpcServerConnectionInfo) {
        this.server = server;
        this.connectionInfo = connectionInfo;
    }

    public getConnection(debugSessionId: string): ICliRpcClient | null {
        return this.connections.find(connection => connection.debugSessionId === debugSessionId) || null;
    }

    public addConnection(connection: ICliRpcClient): boolean {
        if (this._disposed) {
            connection.dispose();
            return false;
        }

        this._ownedConnections.add(connection);
        if (this.connections.includes(connection)) {
            return true;
        }

        this.connections.push(connection);
        this._onNewConnection.fire(connection);
        return true;
    }

    public removeConnection(connection: ICliRpcClient) {
        this._ownedConnections.delete(connection);
        const index = this.connections.indexOf(connection);
        if (index !== -1) {
            this.connections.splice(index, 1);
        }

        connection.dispose();
    }

    public dispose() {
        if (this._disposed) {
            return;
        }

        this._disposed = true;
        extensionLogOutputChannel.info(`Disposing RPC server`);
        // A client is owned before its debug-session handshake starts. That ensures a stalled
        // handshake cannot outlive server teardown with its transport and UI state still active.
        for (const connection of this._ownedConnections) {
            connection.dispose();
        }

        this._ownedConnections.clear();
        this.connections.splice(0);
        this._onNewConnection.dispose();
        this.server.close();
    }

    private _ownConnection(connection: ICliRpcClient): boolean {
        if (this._disposed) {
            connection.dispose();
            return false;
        }

        this._ownedConnections.add(connection);
        return true;
    }

    static async create(rpcClientFactory: (rpcServerConnectionInfo: RpcServerConnectionInfo, connection: MessageConnection, token: string, debugSessionId: string | null) => ICliRpcClient): Promise<AspireRpcServer> {
        const token = generateToken();
        const { key, cert } = await createSelfSignedCertAsync();

        function withAuthentication(callback: (...params: any[]) => any) {
            return (...params: any[]) => {
                // timingSafeEqual is used to verify that the tokens are equivalent in a way that mitigates timing attacks
                if (!params || params.length === 0 || Buffer.from(params[0]).length !== Buffer.from(token).length || timingSafeEqual(Buffer.from(params[0]), Buffer.from(token)) === false) {
                    throw new Error(invalidTokenProvided);
                }

                if (Array.isArray(params)) {
                    (params as any[]).shift();
                }

                return callback(...params);
            };
        }

        return new Promise<AspireRpcServer>((resolve, reject) => {
            const server = tls.createServer({ key, cert });

            server.on('error', (err) => {
                extensionLogOutputChannel.error(rpcServerError(err));
                reject(err);
            });

            extensionLogOutputChannel.info('Setting up RPC server.');
            server.listen(0, () => {
                const addressInfo = server?.address();
                if (typeof addressInfo === 'object' && addressInfo?.port) {
                    const fullAddress = `localhost:${addressInfo.port}`;
                    extensionLogOutputChannel.info(`RPC server listening on ${fullAddress}`);

                    const connectionInfo: RpcServerConnectionInfo = {
                        token: token,
                        address: fullAddress,
                        cert: cert
                    };

                    const rpcServer = new AspireRpcServer(server, connectionInfo);

                    server.on('secureConnection', async (socket) => {
                        extensionLogOutputChannel.info('Client connected to RPC server');
                        const connection = createMessageConnection(
                            new StreamMessageReader(socket),
                            new StreamMessageWriter(socket)
                        );

                        connection.onRequest('getCapabilities', withAuthentication(async () => {
                            return getSupportedCapabilities();
                        }));

                        connection.onRequest('ping', withAuthentication(async () => {
                            return 'pong';
                        }));

                        // Create the RPC client with a null debug session ID initially.
                        // Register all interaction service endpoints BEFORE calling listen()
                        // to avoid a race condition where the CLI sends requests (e.g. displayEmptyLine)
                        // before handlers are registered.
                        const rpcClient = rpcClientFactory(connectionInfo, connection, token, null);
                        connection.onClose(() => rpcServer.removeConnection(rpcClient));
                        if (!rpcServer._ownConnection(rpcClient)) {
                            return;
                        }

                        try {
                            addInteractionServiceEndpoints(connection, rpcClient.interactionService, rpcClient, withAuthentication);
                            connection.listen();

                            const clientDebugSessionId = await connection.sendRequest<string | null>('getDebugSessionId');
                            rpcClient.debugSessionId = clientDebugSessionId;
                            rpcServer.addConnection(rpcClient);
                        }
                        catch (error) {
                            // MessageConnection disposal rejects its outstanding request with
                            // PendingResponseRejected during normal CLI exit. The ownership check
                            // keeps the same response from a still-live client visible as a warning.
                            const transportDisposedDuringHandshake =
                                error instanceof ResponseError &&
                                error.code === ErrorCodes.PendingResponseRejected &&
                                !rpcServer._ownedConnections.has(rpcClient);
                            if (transportDisposedDuringHandshake) {
                                extensionLogOutputChannel.info(`RPC client transport closed during initialization: ${error}`);
                            }
                            else {
                                extensionLogOutputChannel.warn(`Failed to initialize RPC client: ${error}`);
                            }

                            rpcServer.removeConnection(rpcClient);
                        }
                    });

                    resolve(rpcServer);
                }
                else {
                    extensionLogOutputChannel.error(rpcServerAddressError);
                    vscode.window.showErrorMessage(rpcServerAddressError);
                    reject(new Error(rpcServerAddressError));
                }
            });

            server.on('error', (err) => {
                extensionLogOutputChannel.error(rpcServerError(err));
                reject(err);
            });
        });
    }
}
