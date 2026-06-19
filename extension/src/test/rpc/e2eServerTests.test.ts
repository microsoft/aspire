import * as assert from 'assert';
import waitForExpect from 'wait-for-expect';
import * as vscode from 'vscode';
import * as tls from 'tls';

import { createMessageConnection } from 'vscode-jsonrpc';
import { StreamMessageReader, StreamMessageWriter } from 'vscode-jsonrpc/node';
import { getAndActivateExtension } from '../common';
import { RpcServerConnectionInfo } from '../../server/AspireRpcServer';

interface TestOnlyExtensionExports {
	__testOnlyRpcServerInfo?: RpcServerConnectionInfo;
}

suite('End-to-end RPC server auth tests', () => {
	vscode.window.showInformationMessage('Starting end-to-end rpc server tests.');

	test('rpcServer authenticated call succeeds', async () => {
		const { connection, rpcServerInfo, client } = await getRealRpcServer();

		try {
			const response = await connection.sendRequest('ping', rpcServerInfo.token);
			assert.deepStrictEqual(response, 'pong');
		}
		finally {
			connection.dispose();
			client.end();
		}
	});

	test("rpcServer unauthenticated call fails", async () => {
		const { connection, client } = await getRealRpcServer();

		try {
			await assert.rejects(() => connection.sendRequest('ping', { token: 'invalid-token' }));
		}
		finally {
			connection.dispose();
			client.end();
		}
	});

	async function getRealRpcServer() {
		const extension = await getAndActivateExtension();
		const exports = extension.exports as TestOnlyExtensionExports;

		// Wait for the RPC server to start and get the port
		await waitForExpect(() => {
			assert.ok(exports.__testOnlyRpcServerInfo);
		}, 2000, 50);

		const rpcServerInfo = exports.__testOnlyRpcServerInfo as RpcServerConnectionInfo;

		const port = Number(rpcServerInfo.address.replace('localhost:', ''));
		const client = tls.connect({
			port,
			host: 'localhost',
			rejectUnauthorized: false,
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
