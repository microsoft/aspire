import * as vscode from 'vscode';
import { createMessageConnection, MessageConnection } from 'vscode-jsonrpc';
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

    private _onNewConnection = new vscode.EventEmitter<ICliRpcClient>();
    public readonly onNewConnection = this._onNewConnection.event;

    constructor(server: tls.Server, connectionInfo: RpcServerConnectionInfo) {
        this.server = server;
        this.connectionInfo = connectionInfo;
    }

    public getConnection(debugSessionId: string): ICliRpcClient | null {
        return this.connections.find(connection => connection.debugSessionId === debugSessionId) || null;
    }

    public addConnection(connection: ICliRpcClient) {
        this.connections.push(connection);
        this._onNewConnection.fire(connection);
    }

    public removeConnection(connection: ICliRpcClient) {
        const index = this.connections.indexOf(connection);
        if (index !== -1) {
            this.connections.splice(index, 1);
        }
    }

    public dispose() {
        extensionLogOutputChannel.info(`Disposing RPC server`);
        this._onNewConnection.dispose();
        this.server.close();
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

            extensionLogOutputChannel.info(`Setting up RPC server with token: ${token}`);
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
                        addInteractionServiceEndpoints(connection, rpcClient.interactionService, rpcClient, withAuthentication);

                        connection.listen();

                        const clientDebugSessionId = await connection.sendRequest<string | null>('getDebugSessionId');
                        rpcClient.debugSessionId = clientDebugSessionId;

                        rpcServer.addConnection(rpcClient);

                        connection.onClose(() => rpcServer.removeConnection(rpcClient));

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

