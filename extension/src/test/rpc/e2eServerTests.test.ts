import * as assert from 'assert';
import waitForExpect from 'wait-for-expect';
import * as vscode from 'vscode';
import * as tls from 'tls';

import { createMessageConnection } from 'vscode-jsonrpc';
import { StreamMessageReader, StreamMessageWriter } from 'vscode-jsonrpc/node';
import { getAndActivateExtension } from '../common';
import { RpcServerConnectionInfo } from '../../server/AspireRpcServer';
import { AcquiredTestRunSession, TestRunSessionAcquireOptions } from '../../dcp/TestRunSessionManager';

interface TestRunSessionApi {
	acquireTestRunSession(options: TestRunSessionAcquireOptions): AcquiredTestRunSession;
	releaseTestRunSession(id: string): Promise<void>;
}

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

	test('exports test run session API for C# Dev Kit', async () => {
		const extension = await getAndActivateExtension();

		await waitForExpect(() => {
			assert.ok(extension.exports.acquireTestRunSession);
			assert.ok(extension.exports.releaseTestRunSession);
		}, 2000, 50);

		const api = extension.exports as TestRunSessionApi;
		const lease = api.acquireTestRunSession({ debug: true });

		assert.ok(lease.id);
		assert.ok(lease.env.DEBUG_SESSION_TOKEN);
		assert.ok(lease.env.DEBUG_SESSION_PORT);
		assert.strictEqual(lease.env.DCP_INSTANCE_ID_PREFIX, `${lease.sessionId}-`);

		await api.releaseTestRunSession(lease.id);
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
