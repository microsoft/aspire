import * as assert from 'assert';
import waitForExpect from 'wait-for-expect';
import * as vscode from 'vscode';
import * as tls from 'tls';
import * as https from 'https';

import { createMessageConnection } from 'vscode-jsonrpc';
import { StreamMessageReader, StreamMessageWriter } from 'vscode-jsonrpc/node';
import { getAndActivateExtension } from '../common';
import { RpcServerConnectionInfo } from '../../server/AspireRpcServer';
import { AcquiredTestRunSession } from '../../dcp/TestRunSessionManager';
import { AspireExtensionApi } from '../../types/extensionApi';

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

	test('exports test run session API for C# Dev Kit', async () => {
		const extension = await getAndActivateExtension();

		await waitForExpect(() => {
			assert.ok(extension.exports.acquireTestRunSession);
			assert.ok(extension.exports.releaseTestRunSession);
			// dcpServerInfo exposes only the address — sensitive fields (token, certificate) must not leak
			assert.strictEqual('token' in (extension.exports.dcpServerInfo ?? {}), false);
			assert.strictEqual('certificate' in (extension.exports.dcpServerInfo ?? {}), false);
		}, 2000, 50);

		const api = extension.exports as AspireExtensionApi;
		const lease = api.acquireTestRunSession({ debug: true });

		assert.ok(lease.id);
		assert.ok(lease.env.DEBUG_SESSION_TOKEN);
		assert.ok(lease.env.DEBUG_SESSION_PORT);
		assert.strictEqual(lease.env.DCP_INSTANCE_ID_PREFIX, `${lease.sessionId}-`);
		assert.strictEqual(await getDcpInfoStatusCode(lease), 200);

		await api.releaseTestRunSession(lease.id);
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

	async function getDcpInfoStatusCode(lease: AcquiredTestRunSession): Promise<number | undefined> {
		const [host, port] = lease.env.DEBUG_SESSION_PORT.split(':');

		return await new Promise<number | undefined>((resolve, reject) => {
			const request = https.request({
				host,
				port: Number(port),
				path: '/info',
				method: 'GET',
				rejectUnauthorized: false,
				headers: {
					Authorization: `Bearer ${lease.env.DEBUG_SESSION_TOKEN}`,
					'Microsoft-Developer-DCP-Instance-ID': `${lease.sessionId}-test`
				}
			}, response => {
				response.resume();
				response.on('end', () => resolve(response.statusCode));
			});
			request.on('error', reject);
			request.end();
		});
	}
});
