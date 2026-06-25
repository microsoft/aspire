import * as assert from 'assert';
import { getAndActivateExtension } from './common';
import { AspireExtensionApi } from '../types/extensionApi';

suite('extension exports', () => {
    test('exposes deterministic API for end-to-end tests', async () => {
        const extension = await getAndActivateExtension();
        const api = extension.exports as AspireExtensionApi;

        assert.strictEqual(api.apiVersion, 2);
        assert.ok(api.rpcServerInfo.address);
        assert.ok(api.dcpServerInfo.address);
        assert.strictEqual('token' in api.rpcServerInfo, false);
        assert.strictEqual('token' in api.dcpServerInfo, false);
        assert.strictEqual('certificate' in api.dcpServerInfo, false);
        assert.ok(api.logDirectory);
        assert.strictEqual(typeof api.onDidChangeState, 'function');

        const state = api.state;
        assert.strictEqual(state.viewMode, 'workspace');
        assert.strictEqual(Array.isArray(state.workspaceAppHostCandidatePaths), true);
        assert.strictEqual(Array.isArray(state.workspaceResources), true);
        assert.strictEqual(Array.isArray(state.appHosts), true);
        assert.strictEqual(Array.isArray(state.launchingPaths), true);
        assert.strictEqual(Array.isArray(state.debugSessions), true);

        const waitedState = await api.waitForState(s => s.viewMode === 'workspace', { timeoutMs: 1000 });
        assert.strictEqual(waitedState.viewMode, 'workspace');
        assert.strictEqual(api.getDashboardUrl(), undefined);
    });
});
