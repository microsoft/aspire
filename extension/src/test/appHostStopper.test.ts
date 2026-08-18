import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import { EventEmitter } from 'events';
import { PassThrough } from 'stream';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { stopExternalAppHost } from '../services/AppHostStopper';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { windowCliPathTarget, workspaceFolderCliPathTarget } from '../utils/cliPathVariables';

suite('AppHostStopper', () => {
    test('waits for aspire stop to exit successfully', async () => {
        const childState = createTestChildProcess();
        const child = childState as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(child);
        const terminalProvider = createTerminalProvider();
        const appHostPath = `/repo/AppHost'; touch /tmp/not-executed #/$(whoami)/"bad"/AppHost.csproj`;
        let settled = false;

        try {
            const stopping = stopExternalAppHost(
                terminalProvider,
                appHostPath,
                new vscode.CancellationTokenSource().token).then(() => { settled = true; });
            await new Promise(resolve => setImmediate(resolve));

            assert.strictEqual(spawnStub.calledOnce, true);
            assert.deepStrictEqual(spawnStub.firstCall.args.slice(0, 2), [
                '/usr/local/bin/aspire',
                ['stop', '--apphost', appHostPath],
            ]);
            assert.strictEqual(settled, false);

            childState.exitCode = 0;
            child.emit('close', 0);
            await stopping;
            assert.strictEqual(settled, true);
        }
        finally {
            spawnStub.restore();
        }
    });

    test('resolves the CLI using the target derived from the AppHost path workspace folder', async () => {
        const childState = createTestChildProcess();
        const child = childState as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        sinon.stub(nodeChildProcess, 'spawn').returns(child);
        const folder = { name: 'a', index: 0, uri: vscode.Uri.file('/repo') } as vscode.WorkspaceFolder;
        const getWorkspaceFolderStub = sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(folder);
        const getAspireCliExecutablePathStub = sinon.stub().resolves('/repo/bin/aspire');
        const terminalProvider = {
            getAspireCliExecutablePath: getAspireCliExecutablePathStub,
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: async () => { },
        } as unknown as AspireTerminalProvider;

        try {
            const stopping = stopExternalAppHost(
                terminalProvider,
                '/repo/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token);
            await new Promise(resolve => setImmediate(resolve));

            assert.ok(getAspireCliExecutablePathStub.calledOnceWith(workspaceFolderCliPathTarget(folder)));

            childState.exitCode = 0;
            child.emit('close', 0);
            await stopping;
        }
        finally {
            getWorkspaceFolderStub.restore();
            (nodeChildProcess.spawn as sinon.SinonStub).restore();
        }
    });

    test('resolves the CLI using the window target when no folder owns the AppHost path', async () => {
        const childState = createTestChildProcess();
        const child = childState as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        sinon.stub(nodeChildProcess, 'spawn').returns(child);
        const getWorkspaceFolderStub = sinon.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        const getAspireCliExecutablePathStub = sinon.stub().resolves('/usr/local/bin/aspire');
        const terminalProvider = {
            getAspireCliExecutablePath: getAspireCliExecutablePathStub,
            createEnvironment: () => ({}),
            sendAspireCommandToAspireTerminal: async () => { },
        } as unknown as AspireTerminalProvider;

        try {
            const stopping = stopExternalAppHost(
                terminalProvider,
                '/outside/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token);
            await new Promise(resolve => setImmediate(resolve));

            assert.ok(getAspireCliExecutablePathStub.calledOnceWith(windowCliPathTarget));

            childState.exitCode = 0;
            child.emit('close', 0);
            await stopping;
        }
        finally {
            getWorkspaceFolderStub.restore();
            (nodeChildProcess.spawn as sinon.SinonStub).restore();
        }
    });

    test('rejects when aspire stop exits unsuccessfully', async () => {
        const childState = createTestChildProcess();
        const child = childState as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(child);

        try {
            const stopping = stopExternalAppHost(
                createTerminalProvider(),
                '/repo/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token);
            await new Promise(resolve => setImmediate(resolve));

            childState.stderr.write('No running AppHost matched the path.');
            childState.exitCode = 7;
            child.emit('close', 7);

            await assert.rejects(stopping, /aspire stop exited with code 7: No running AppHost matched the path\./);
        }
        finally {
            spawnStub.restore();
        }
    });

    test('disposes cancellation tracking when aspire stop fails to spawn', async () => {
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').throws(new Error('spawn failed'));
        let cancellationRegistrationDisposed = false;
        const cancellationToken = {
            isCancellationRequested: false,
            onCancellationRequested: () => new vscode.Disposable(() => { cancellationRegistrationDisposed = true; }),
        } as vscode.CancellationToken;

        try {
            await assert.rejects(
                stopExternalAppHost(
                    createTerminalProvider(),
                    '/repo/AppHost/AppHost.csproj',
                    cancellationToken),
                /spawn failed/);

            assert.strictEqual(cancellationRegistrationDisposed, true);
        }
        finally {
            spawnStub.restore();
        }
    });

    test('preserves cancellation when aspire stop fails to spawn after cancellation', async () => {
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').throws(new Error('spawn failed'));
        let cancellationRegistrationDisposed = false;
        const cancellationToken = {
            isCancellationRequested: false,
            onCancellationRequested: (listener: () => void) => {
                listener();
                return new vscode.Disposable(() => { cancellationRegistrationDisposed = true; });
            },
        } as vscode.CancellationToken;

        try {
            await assert.rejects(
                stopExternalAppHost(
                    createTerminalProvider(),
                    '/repo/AppHost/AppHost.csproj',
                    cancellationToken),
                error => error instanceof vscode.CancellationError);

            assert.strictEqual(cancellationRegistrationDisposed, true);
        }
        finally {
            spawnStub.restore();
        }
    });

    test('bounds stderr retained from a failed aspire stop', async () => {
        const childState = createTestChildProcess();
        const child = childState as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(child);

        try {
            const stopping = stopExternalAppHost(
                createTerminalProvider(),
                '/repo/AppHost/AppHost.csproj',
                new vscode.CancellationTokenSource().token);
            await new Promise(resolve => setImmediate(resolve));

            childState.stderr.write(`first diagnostic:${'x'.repeat(1_000_000)}`);
            childState.exitCode = 7;
            child.emit('close', 7);

            await assert.rejects(stopping, error =>
                error instanceof Error &&
                error.message.startsWith('aspire stop exited with code 7: first diagnostic:') &&
                error.message.length < 20_000);
        }
        finally {
            spawnStub.restore();
        }
    });

    test('cancels and terminates an in-flight aspire stop process', async () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        const childState = createTestChildProcess();
        const child = childState as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(child);
        const processKillStub = sinon.stub(process, 'kill').returns(true);
        const cancellationSource = new vscode.CancellationTokenSource();
        let settled = false;

        try {
            const stopping = stopExternalAppHost(
                createTerminalProvider(),
                '/repo/AppHost/AppHost.csproj',
                cancellationSource.token).finally(() => { settled = true; });
            await new Promise(resolve => setImmediate(resolve));

            cancellationSource.cancel();
            await new Promise(resolve => setImmediate(resolve));

            assert.deepStrictEqual(processKillStub.firstCall.args, [-1234, 'SIGTERM']);
            assert.strictEqual(settled, false);
            childState.signalCode = 'SIGTERM';
            child.emit('close', null);
            await assert.rejects(stopping, error => error instanceof vscode.CancellationError);
            assert.strictEqual(settled, true);
        }
        finally {
            cancellationSource.dispose();
            processKillStub.restore();
            spawnStub.restore();
            platformStub.restore();
        }
    });

    test('bounds cancellation when the child never reports completion', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const platformStub = sinon.stub(process, 'platform').value('linux');
        const childState = createTestChildProcess();
        const child = childState as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(child);
        const processKillStub = sinon.stub(process, 'kill').returns(true);
        const cancellationSource = new vscode.CancellationTokenSource();
        let cancelled = false;

        try {
            const stopping = stopExternalAppHost(
                createTerminalProvider(),
                '/repo/AppHost/AppHost.csproj',
                cancellationSource.token).catch(error => {
                    cancelled = error instanceof vscode.CancellationError;
                });
            await clock.tickAsync(0);

            cancellationSource.cancel();
            await clock.tickAsync(11_000);

            assert.strictEqual(cancelled, true);
            await stopping;
        }
        finally {
            cancellationSource.dispose();
            processKillStub.restore();
            spawnStub.restore();
            platformStub.restore();
            clock.restore();
        }
    });

    test('preserves the exit result when cancellation arrives after the process exits', async () => {
        const childState = createTestChildProcess();
        const child = childState as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(child);
        const cancellationSource = new vscode.CancellationTokenSource();

        try {
            const stopping = stopExternalAppHost(
                createTerminalProvider(),
                '/repo/AppHost/AppHost.csproj',
                cancellationSource.token);
            await new Promise(resolve => setImmediate(resolve));

            childState.exitCode = 0;
            cancellationSource.cancel();
            child.emit('close', 0);

            await stopping;
            assert.strictEqual(childState.kill.called, false);
        }
        finally {
            cancellationSource.dispose();
            spawnStub.restore();
        }
    });
});

function createTestChildProcess() {
    return Object.assign(new EventEmitter(), {
        stdin: new PassThrough(),
        stdout: new PassThrough(),
        stderr: new PassThrough(),
        pid: 1234,
        exitCode: null as number | null,
        signalCode: null as NodeJS.Signals | null,
        killed: false,
        kill: sinon.stub().returns(true),
    });
}

function createTerminalProvider(): AspireTerminalProvider {
    return {
        getAspireCliExecutablePath: async () => '/usr/local/bin/aspire',
        createEnvironment: () => ({}),
        sendAspireCommandToAspireTerminal: async () => { },
    } as unknown as AspireTerminalProvider;
}