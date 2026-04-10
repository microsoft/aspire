import * as assert from 'assert';
import waitForExpect from 'wait-for-expect';
import * as vscode from 'vscode';
import * as tls from 'tls';

import { createMessageConnection } from 'vscode-jsonrpc';
import { StreamMessageReader, StreamMessageWriter } from 'vscode-jsonrpc/node';
import { getAndActivateExtension } from '../common';
import { RpcServerConnectionInfo } from '../../server/AspireRpcServer';

suite('End-to-end RPC server auth tests', () => {
	vscode.window.showInformationMessage('Starting end-to-end rpc server tests.');

	test('rpcServer authenticated call succeeds', async () => {
		// Arrange
		const { connection, rpcServerInfo, client } = await getRealRpcServer();

		// Act & Assert
		const response = await connection.sendRequest('ping', rpcServerInfo.token);
		assert.deepStrictEqual(response, 'pong');

		connection.dispose();
		client.end();
	});

	test("rpcServer unauthenticated call fails", async () => {
		// Arrange
		const { connection, client } = await getRealRpcServer();

		// Act & Assert
		assert.rejects(() => connection.sendRequest('ping', { token: 'invalid-token' }));
	});

	async function getRealRpcServer() {
		const extension = await getAndActivateExtension();

		// Wait for the RPC server to start and get the port
		await waitForExpect(() => {
			assert.ok(extension.exports.rpcServerInfo);
		}, 2000, 50);

		const rpcServerInfo = extension.exports.rpcServerInfo as RpcServerConnectionInfo;

		const port = Number(rpcServerInfo.address.replace('localhost:', ''));
		const client = tls.connect({
			port,
			host: 'localhost',
			rejectUnauthorized: true,
		});
		await new Promise<void>((resolve) => client.once('secureConnect', resolve));

		const connection = createMessageConnection(
			new StreamMessageReader(client),
			new StreamMessageWriter(client)
		);

		connection.listen();
		return { connection, rpcServerInfo, client };
	}
});
