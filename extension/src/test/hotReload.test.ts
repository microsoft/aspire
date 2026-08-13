import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import {
    getHotReloadDiagnostics,
    logHotReloadDiagnostics,
    showHotReloadDisabledAdvisoryIfNeeded
} from '../debugger/hotReload';
import { hotReloadDisabledNotice, openSettingsLabel } from '../loc/strings';
import { extensionLogOutputChannel } from '../utils/logging';

suite('Hot Reload Tests', () => {
    let restoreTrust: (() => void) | undefined;
    type HotReloadInspection = NonNullable<ReturnType<vscode.WorkspaceConfiguration['inspect']>>;

    teardown(() => {
        restoreTrust?.();
        restoreTrust = undefined;
        sinon.restore();
    });

    function stubDevKit(installed: boolean): void {
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) =>
            installed && extensionId === 'ms-dotnettools.csdevkit'
                ? { id: extensionId, isActive: false } as unknown as vscode.Extension<unknown>
                : undefined);
    }

    function stubWorkspaceTrust(trusted: boolean): void {
        const descriptor = Object.getOwnPropertyDescriptor(vscode.workspace, 'isTrusted');
        Object.defineProperty(vscode.workspace, 'isTrusted', { value: trusted, configurable: true });
        restoreTrust = () => {
            if (descriptor) {
                Object.defineProperty(vscode.workspace, 'isTrusted', descriptor);
            }
        };
    }

    function createHotReloadInspection(values: Partial<HotReloadInspection> = {}): HotReloadInspection {
        return {
            key: 'csharp.experimental.debug.hotReload',
            ...values
        };
    }

    function stubHotReloadSettings(enabled: boolean | undefined, onSave: boolean, inspection: HotReloadInspection | undefined = enabled !== undefined
        ? createHotReloadInspection({ defaultValue: enabled })
        : undefined): void {
        const getConfiguration = sinon.stub(vscode.workspace, 'getConfiguration');
        getConfiguration.withArgs('csharp.experimental.debug').returns({
            get: (name: string) => name === 'hotReload' ? enabled : undefined,
            inspect: (name: string) => name === 'hotReload' ? inspection : undefined
        } as vscode.WorkspaceConfiguration);
        getConfiguration.withArgs('csharp.debug').returns({
            get: (name: string) => name === 'hotReloadOnSave' ? onSave : undefined
        } as vscode.WorkspaceConfiguration);
        getConfiguration.returns({ get: () => undefined } as unknown as vscode.WorkspaceConfiguration);
    }

    test('reports disabled Hot Reload diagnostics', () => {
        stubDevKit(true);
        stubWorkspaceTrust(false);
        stubHotReloadSettings(false, true);
        const info = sinon.stub(extensionLogOutputChannel, 'info');

        const diagnostics = getHotReloadDiagnostics();
        logHotReloadDiagnostics('api', diagnostics);

        assert.deepStrictEqual(diagnostics, {
            devKitInstalled: true,
            workspaceTrusted: false,
            settingContributed: true,
            settingEnabled: false,
            reloadOnSaveEnabled: true
        });
        assert.strictEqual(
            info.calledOnceWithExactly(
                'Hot Reload state for api: devKitInstalled=true, workspaceTrusted=false, ' +
                'csharp.experimental.debug.hotReload.contributed=true, csharp.experimental.debug.hotReload=false, ' +
                'csharp.debug.hotReloadOnSave=true'),
            true);
    });

    test('reports enabled Hot Reload diagnostics', () => {
        stubDevKit(true);
        stubWorkspaceTrust(true);
        stubHotReloadSettings(true, false);
        const info = sinon.stub(extensionLogOutputChannel, 'info');

        const diagnostics = getHotReloadDiagnostics();
        logHotReloadDiagnostics('worker', diagnostics);

        assert.deepStrictEqual(diagnostics, {
            devKitInstalled: true,
            workspaceTrusted: true,
            settingContributed: true,
            settingEnabled: true,
            reloadOnSaveEnabled: false
        });
        assert.strictEqual(
            info.calledOnceWithExactly(
                'Hot Reload state for worker: devKitInstalled=true, workspaceTrusted=true, ' +
                'csharp.experimental.debug.hotReload.contributed=true, csharp.experimental.debug.hotReload=true, ' +
                'csharp.debug.hotReloadOnSave=false'),
            true);
    });

    test('does not show the advisory when C# Dev Kit is absent', async () => {
        stubDevKit(false);
        stubWorkspaceTrust(true);
        stubHotReloadSettings(false, true);
        const notification = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);

        await showHotReloadDisabledAdvisoryIfNeeded(getHotReloadDiagnostics());

        assert.strictEqual(notification.called, false);
    });

    test('does not show the advisory when C# Dev Kit does not contribute the Hot Reload setting', async () => {
        stubDevKit(true);
        stubWorkspaceTrust(true);
        stubHotReloadSettings(undefined, true);
        const notification = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);

        await showHotReloadDisabledAdvisoryIfNeeded(getHotReloadDiagnostics());

        assert.strictEqual(notification.called, false);
    });

    test('does not show the advisory when C# Dev Kit reports only a key-only Hot Reload inspection', async () => {
        stubDevKit(true);
        stubWorkspaceTrust(true);
        stubHotReloadSettings(undefined, true, createHotReloadInspection());
        const notification = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);

        await showHotReloadDisabledAdvisoryIfNeeded(getHotReloadDiagnostics());

        assert.strictEqual(notification.called, false);
    });

    test('does not show the advisory when C# Dev Kit reports only a user-value Hot Reload inspection', async () => {
        stubDevKit(true);
        stubWorkspaceTrust(true);
        stubHotReloadSettings(false, true, createHotReloadInspection({ globalValue: false }));
        const notification = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);

        await showHotReloadDisabledAdvisoryIfNeeded(getHotReloadDiagnostics());

        assert.strictEqual(notification.called, false);
    });

    test('does not show the advisory when Hot Reload is enabled', async () => {
        const notification = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);

        await showHotReloadDisabledAdvisoryIfNeeded({
            devKitInstalled: true,
            workspaceTrusted: true,
            settingContributed: true,
            settingEnabled: true,
            reloadOnSaveEnabled: true
        });

        assert.strictEqual(notification.called, false);
    });

    test('opens C# Dev Kit Hot Reload settings without updating configuration', async () => {
        const update = sinon.stub().resolves();
        const getConfiguration = sinon.stub(vscode.workspace, 'getConfiguration').returns({
            get: () => false,
            update
        } as unknown as vscode.WorkspaceConfiguration);
        const notification = sinon.stub(vscode.window, 'showInformationMessage')
            .resolves(openSettingsLabel as unknown as vscode.MessageItem);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const diagnostics = {
            devKitInstalled: true,
            workspaceTrusted: true,
            settingContributed: true,
            settingEnabled: false,
            reloadOnSaveEnabled: true
        };

        await showHotReloadDisabledAdvisoryIfNeeded(diagnostics);
        await showHotReloadDisabledAdvisoryIfNeeded(diagnostics);

        assert.deepStrictEqual(notification.firstCall.args, [hotReloadDisabledNotice, openSettingsLabel]);
        assert.strictEqual(notification.callCount, 1);
        assert.strictEqual(executeCommand.calledOnceWithExactly(
            'workbench.action.openSettings',
            'csharp.experimental.debug.hotReload'), true);
        assert.strictEqual(getConfiguration.called, false);
        assert.strictEqual(update.called, false);
    });
});
