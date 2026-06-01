import * as assert from 'assert';
import * as sinon from 'sinon';
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import { ChildProcessWithoutNullStreams } from 'child_process';

import { createAspireExtensionApi } from '../api';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { AppHostDataRepository } from '../views/AppHostDataRepository';
import * as cliModule from '../debugger/languages/cli';

class TestChildProcess extends EventEmitter {
    stdout = new PassThrough();
    stderr = new PassThrough();
    killed = false;
    exitCode: number | null = null;
    signalCode: NodeJS.Signals | null = null;

    kill(): boolean {
        this.killed = true;
        this.exitCode = 0;
        this.emit('close', null);
        return true;
    }
}

suite('Aspire extension API', () => {
    let subscriptions: { dispose(): void }[];
    let terminalProvider: AspireTerminalProvider;
    let getCliPathStub: sinon.SinonStub;
    let spawnStub: sinon.SinonStub;

    setup(() => {
        subscriptions = [];
        terminalProvider = new AspireTerminalProvider(subscriptions);
        getCliPathStub = sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
        spawnStub = sinon.stub(cliModule, 'spawnCliProcess').returns(new TestChildProcess() as unknown as ChildProcessWithoutNullStreams);
    });

    teardown(() => {
        spawnStub.restore();
        getCliPathStub.restore();
        subscriptions.forEach(subscription => subscription.dispose());
    });

    test('does not expose AppHost change event until the contract has a reliable data source', () => {
        const api = createApi();

        assert.strictEqual('onDidChangeAppHosts' in api, false);
    });

    test('resource commands require a non-empty AppHost path', async () => {
        const api = createApi();

        await assert.rejects(() => api.startResource('api', ''), /appHostPath must be a non-empty absolute path/);

        assert.strictEqual(spawnStub.called, false);
    });

    test('resource command failures include CLI output and cannot prompt through extension RPC', async () => {
        const api = createApi();

        const stopPromise = api.stopResource('api', '/workspace/AppHost.csproj');
        await waitForMicrotasks();

        assert.deepStrictEqual(spawnStub.firstCall.args[2], ['resource', 'api', 'stop', '--apphost', '/workspace/AppHost.csproj']);
        assert.strictEqual(spawnStub.firstCall.args[3].noExtensionVariables, true);

        spawnStub.firstCall.args[3].stderrCallback("resource 'api' not found");
        spawnStub.firstCall.args[3].exitCallback(42);

        await assert.rejects(stopPromise, /aspire resource stop exited with code 42: resource 'api' not found/);
    });

    function createApi() {
        const dataRepository = {
            fetchAppHostsOnce: async () => [],
        } as unknown as AppHostDataRepository;

        return createAspireExtensionApi(dataRepository, terminalProvider, {
            acquireTestRunSession: () => ({
                id: 'lease',
                sessionId: 'session',
                env: {},
            }),
            releaseTestRunSession: async () => { },
        });
    }
});

function waitForMicrotasks(): Promise<void> {
    return new Promise(resolve => setImmediate(resolve));
}
