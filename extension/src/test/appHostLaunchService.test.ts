import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { AspireExtendedDebugConfiguration } from '../dcp/types';
import { AppHostLaunchService } from '../services/AppHostLaunchService';

suite('AppHostLaunchService', () => {
    let service: AppHostLaunchService;
    let startDebuggingStub: sinon.SinonStub;

    setup(() => {
        service = new AppHostLaunchService();
        startDebuggingStub = sinon.stub(vscode.debug, 'startDebugging').resolves(true);
    });

    teardown(() => {
        service.dispose();
        startDebuggingStub.restore();
    });

    test('isLaunching returns false before launch', () => {
        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('launch marks path as launching', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', true);

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), true);
    });

    test('launch fires onDidChangeLaunchingState event', async () => {
        let fired = false;
        service.onDidChangeLaunchingState(() => { fired = true; });

        await service.launch('/repo/AppHost.csproj', 'run', true);

        assert.strictEqual(fired, true);
    });

    test('launch starts a debug session with correct configuration', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', false);

        assert.ok(startDebuggingStub.calledOnce);
        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.type, 'aspire');
        assert.strictEqual(config.request, 'launch');
        assert.strictEqual(config.program, '/repo/AppHost.csproj');
        assert.strictEqual(config.command, 'run');
        assert.strictEqual(config.noDebug, false);
        assert.strictEqual(config.step, undefined);
    });

    test('launch includes step when doStep is provided', async () => {
        await service.launch('/repo/AppHost.csproj', 'do', true, 'deploy');

        const config = startDebuggingStub.firstCall.args[1] as AspireExtendedDebugConfiguration;
        assert.strictEqual(config.command, 'do');
        assert.strictEqual(config.step, 'deploy');
    });

    test('clearLaunching removes the path from launching state', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', true);
        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), true);

        service.clearLaunching('/repo/AppHost.csproj');

        assert.strictEqual(service.isLaunching('/repo/AppHost.csproj'), false);
    });

    test('clearLaunching fires onDidChangeLaunchingState event', async () => {
        await service.launch('/repo/AppHost.csproj', 'run', true);

        let fired = false;
        service.onDidChangeLaunchingState(() => { fired = true; });
        service.clearLaunching('/repo/AppHost.csproj');

        assert.strictEqual(fired, true);
    });

    test('clearLaunching does not fire event when path was not launching', () => {
        let fired = false;
        service.onDidChangeLaunchingState(() => { fired = true; });

        service.clearLaunching('/repo/nonexistent.csproj');

        assert.strictEqual(fired, false);
    });

    test('multiple paths can be tracked independently', async () => {
        await service.launch('/repo/AppHost1.csproj', 'run', true);
        await service.launch('/repo/AppHost2.csproj', 'run', true);

        assert.strictEqual(service.isLaunching('/repo/AppHost1.csproj'), true);
        assert.strictEqual(service.isLaunching('/repo/AppHost2.csproj'), true);

        service.clearLaunching('/repo/AppHost1.csproj');

        assert.strictEqual(service.isLaunching('/repo/AppHost1.csproj'), false);
        assert.strictEqual(service.isLaunching('/repo/AppHost2.csproj'), true);
    });
});
