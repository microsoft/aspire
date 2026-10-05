/// <reference types="mocha" />

import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import {
    DebuggerInstallHintService,
    getDebuggerInstallHintForResource,
    launchConfigurationTypePropertyName,
} from '../debugger/debuggerInstallHints';
import { getSupportedCapabilities } from '../capabilities';
import { debuggerSetupAction, dontShowAgainLabel, errorMessage } from '../loc/strings';
import { isCommandCancellation } from '../utils/telemetry';
import { ResourceState } from '../editor/resourceConstants';

function createResource(
    launchConfigurationType?: string,
    state: string = ResourceState.Running,
): { state: string; properties: Record<string, string | null> } {
    return {
        state,
        properties: launchConfigurationType !== undefined
            ? { [launchConfigurationTypePropertyName]: launchConfigurationType }
            : {},
    };
}

function createMemento(): vscode.Memento {
    const values = new Map<string, unknown>();
    return {
        keys: () => [...values.keys()],
        get: <T>(key: string, defaultValue?: T) => values.has(key) ? values.get(key) as T : defaultValue,
        update: (key: string, value: unknown) => {
            value === undefined ? values.delete(key) : values.set(key, value);
            return Promise.resolve();
        },
    };
}

suite('debugger install hints', () => {
    teardown(() => sinon.restore());

    test('maps the supported missing debugger extensions', () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);

        assert.deepStrictEqual(
            ['python', 'go', 'bun', 'java', 'maui', 'azure-functions'].map(type =>
                getDebuggerInstallHintForResource(createResource(type))),
            [
                {
                    debuggerName: 'Python',
                    debuggerType: 'python',
                    extensionIds: ['ms-python.debugpy'],
                },
                {
                    debuggerName: 'Go',
                    debuggerType: 'go',
                    extensionIds: ['golang.go'],
                },
                {
                    debuggerName: 'Bun',
                    debuggerType: 'bun',
                    extensionIds: ['oven.bun-vscode'],
                },
                {
                    debuggerName: 'Java',
                    debuggerType: 'java',
                    extensionIds: ['redhat.java', 'vscjava.vscode-java-debug'],
                },
                {
                    debuggerName: '.NET MAUI',
                    debuggerType: 'maui',
                    extensionIds: ['ms-dotnettools.dotnet-maui'],
                },
                {
                    debuggerName: 'Azure Functions',
                    debuggerType: 'azure-functions',
                    extensionIds: ['ms-dotnettools.csharp', 'ms-azuretools.vscode-azurefunctions'],
                },
            ]);
    });

    test('recommends CodeLLDB for Rust on Windows when no debugger adapter is installed', () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);

        assert.deepStrictEqual(
            getDebuggerInstallHintForResource(createResource('rust'), 'win32'),
            {
                debuggerName: 'Rust',
                debuggerType: 'rust',
                extensionIds: ['vadimcn.vscode-lldb'],
            });
    });

    test('returns no Rust hint on Windows when the C++ debugger is installed', () => {
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            extensionId === 'ms-vscode.cpptools' ? { id: extensionId } as vscode.Extension<unknown> : undefined);

        assert.strictEqual(
            getDebuggerInstallHintForResource(createResource('rust'), 'win32'),
            undefined);
    });

    test('returns no Rust hint on Windows when CodeLLDB is installed', () => {
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            extensionId === 'vadimcn.vscode-lldb' ? { id: extensionId } as vscode.Extension<unknown> : undefined);

        assert.strictEqual(
            getDebuggerInstallHintForResource(createResource('rust'), 'win32'),
            undefined);
    });

    test('recommends CodeLLDB for Rust on Linux and macOS', () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);

        assert.deepStrictEqual(
            getDebuggerInstallHintForResource(createResource('rust'), 'linux'),
            {
                debuggerName: 'Rust',
                debuggerType: 'rust',
                extensionIds: ['vadimcn.vscode-lldb'],
            });
        assert.deepStrictEqual(
            getDebuggerInstallHintForResource(createResource('rust'), 'darwin'),
            {
                debuggerName: 'Rust',
                debuggerType: 'rust',
                extensionIds: ['vadimcn.vscode-lldb'],
            });
    });

    test('returns no hint for missing, empty, unknown, or fully installed debugger types', () => {
        const installedExtensionIds = new Set(['redhat.java', 'vscjava.vscode-java-debug']);
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            installedExtensionIds.has(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined);

        assert.deepStrictEqual(
            [
                createResource(),
                createResource(''),
                createResource('project'),
                createResource('java'),
            ].map(resource => getDebuggerInstallHintForResource(resource)),
            [undefined, undefined, undefined, undefined]);
    });

    test('returns the complete Java hint when any required extension is missing', () => {
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            extensionId === 'redhat.java' ? { id: extensionId } as vscode.Extension<unknown> : undefined);

        assert.deepStrictEqual(
            getDebuggerInstallHintForResource(createResource('java')),
            {
                debuggerName: 'Java',
                debuggerType: 'java',
                extensionIds: ['redhat.java', 'vscjava.vscode-java-debug'],
            });
    });

    test('returns the complete Azure Functions hint when any required extension is missing', () => {
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            extensionId === 'ms-dotnettools.csharp' ? { id: extensionId } as vscode.Extension<unknown> : undefined);

        assert.deepStrictEqual(
            getDebuggerInstallHintForResource(createResource('azure-functions')),
            {
                debuggerName: 'Azure Functions',
                debuggerType: 'azure-functions',
                extensionIds: ['ms-dotnettools.csharp', 'ms-azuretools.vscode-azurefunctions'],
            });
    });

    test('keeps debugger product names out of localization resources', () => {
        const extensionRoot = path.resolve(__dirname, '..', '..');
        const packageNls = JSON.parse(fs.readFileSync(path.join(extensionRoot, 'package.nls.json'), 'utf8')) as Record<string, string>;

        assert.deepStrictEqual(
            ['pythonDebuggerName', 'goDebuggerName', 'bunDebuggerName', 'javaDebuggerName', 'rustDebuggerName', 'mauiDebuggerName', 'azureFunctionsDebuggerName'].map(name =>
                packageNls[`aspire-vscode.strings.${name}`]),
            [undefined, undefined, undefined, undefined, undefined, undefined, undefined]);
    });

    test('recognizes the standalone debugpy extension as Python debug support', () => {
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            extensionId === 'ms-python.debugpy' ? { id: extensionId } as vscode.Extension<unknown> : undefined);

        assert.ok(getSupportedCapabilities().includes('python'));
    });

    test('shows one install notification and installs the selected debugger', async () => {
        let installed = false;
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            installed ? { id: extensionId } as vscode.Extension<unknown> : undefined);
        const showWarningMessage = sinon.stub(vscode.window, 'showWarningMessage');
        showWarningMessage.onFirstCall().resolves(debuggerSetupAction as any);
        const showInformationMessage = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').callsFake(async () => {
            installed = true;
        });
        const service = new DebuggerInstallHintService(createMemento());

        await service.notifyMissingDebuggers([
            createResource('python'),
            createResource('python'),
            createResource('go', ResourceState.Stopped),
            createResource(),
        ]);

        assert.strictEqual(showWarningMessage.callCount, 1);
        assert.strictEqual(showWarningMessage.firstCall.args[0], 'Set up Python debugging support to debug resources in this app.');
        assert.deepStrictEqual(showWarningMessage.firstCall.args.slice(1), [debuggerSetupAction, dontShowAgainLabel]);
        assert.ok(executeCommand.firstCall.calledWithExactly(
            'workbench.extensions.installExtension',
            'ms-python.debugpy'));
        assert.strictEqual(showInformationMessage.callCount, 1);
        assert.strictEqual(showInformationMessage.firstCall.args[0], 'The extensions required for Python debugging are installed. Restart the AppHost to enable debugging.');
    });

    test('re-evaluates missing debuggers when extension enablement changes', async () => {
        let goRegistered = true;
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            extensionId === 'golang.go' && goRegistered ? { id: extensionId } as vscode.Extension<unknown> : undefined);
        let extensionChangeListener: (() => unknown) | undefined;
        const onDidChange: vscode.Event<void> = listener => {
            extensionChangeListener = listener;
            return { dispose: sinon.stub() };
        };
        sinon.stub(vscode.extensions, 'onDidChange').get(() => onDidChange);
        let notified!: () => void;
        const notification = new Promise<void>(resolve => notified = resolve);
        const showWarningMessage = sinon.stub(vscode.window, 'showWarningMessage').callsFake((async () => {
            notified();
            return undefined;
        }) as any);
        const dataChanges = new vscode.EventEmitter<void>();
        const service = new DebuggerInstallHintService(createMemento());
        const observation = service.watchForMissingDebuggers({
            workspaceAppHostCandidatePaths: ['/workspace/apphost.cs'],
            workspaceResources: [createResource('go')],
            appHosts: [],
            onDidChangeData: dataChanges.event,
            keepDataActive: sinon.stub().returns({ dispose: sinon.stub() }),
        });

        try {
            await new Promise(resolve => setImmediate(resolve));
            assert.strictEqual(showWarningMessage.callCount, 0);

            // Disabling an extension removes it from the registry without changing AppHost data.
            goRegistered = false;
            assert.ok(extensionChangeListener);
            extensionChangeListener();
            await notification;

            assert.strictEqual(showWarningMessage.callCount, 1);
            assert.strictEqual(showWarningMessage.firstCall.args[0], 'Set up Go debugging support to debug resources in this app.');
        } finally {
            observation.dispose();
            dataChanges.dispose();
        }
    });

    test('waits for a fresh install to appear in the extension registry', async () => {
        let installed = false;
        let extensionChangeListener: (() => unknown) | undefined;
        let subscriptionRegisteredResolve!: () => void;
        const subscriptionRegistered = new Promise<void>(resolve => subscriptionRegisteredResolve = resolve);
        const getExtension = sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            installed ? { id: extensionId } as vscode.Extension<unknown> : undefined);
        const onDidChange: vscode.Event<void> = listener => {
            extensionChangeListener = listener;
            subscriptionRegisteredResolve();
            return { dispose: sinon.stub() };
        };
        sinon.stub(vscode.extensions, 'onDidChange').get(() => onDidChange);
        const showInformationMessage = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        sinon.stub(vscode.commands, 'executeCommand').resolves();
        const service = new DebuggerInstallHintService(createMemento());

        const installation = service.installDebuggerExtension({
            debuggerName: 'Java',
            debuggerType: 'java',
            extensionIds: ['redhat.java', 'vscjava.vscode-java-debug'],
        });
        await subscriptionRegistered;

        assert.strictEqual(showInformationMessage.callCount, 0);

        installed = true;
        assert.ok(extensionChangeListener);
        extensionChangeListener();
        await installation;

        assert.ok(getExtension.calledWith('redhat.java'));
        assert.ok(getExtension.calledWith('vscjava.vscode-java-debug'));
        assert.strictEqual(showInformationMessage.callCount, 1);
        assert.strictEqual(
            showInformationMessage.firstCall.args[0],
            'The extensions required for Java debugging are installed. Restart the AppHost to enable debugging.');
    });

    test('does not show a setup notification while installing multiple debugger requirements', async () => {
        const registeredExtensionIds = new Set(['redhat.java', 'vscjava.vscode-java-debug']);
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            registeredExtensionIds.has(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined);
        const extensionChanges = new vscode.EventEmitter<void>();
        sinon.stub(vscode.extensions, 'onDidChange').get(() => extensionChanges.event);
        const showWarningMessage = sinon.stub(vscode.window, 'showWarningMessage').resolves(undefined);
        sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        sinon.stub(vscode.commands, 'executeCommand').callsFake(async (command, extensionId) => {
            if (command === 'workbench.extensions.installExtension') {
                registeredExtensionIds.add(extensionId as string);
                extensionChanges.fire();
            }
        });
        const dataChanges = new vscode.EventEmitter<void>();
        const service = new DebuggerInstallHintService(createMemento());
        const observation = service.watchForMissingDebuggers({
            workspaceAppHostCandidatePaths: ['/workspace/apphost.cs'],
            workspaceResources: [createResource('java')],
            appHosts: [],
            onDidChangeData: dataChanges.event,
            keepDataActive: sinon.stub().returns({ dispose: sinon.stub() }),
        });

        try {
            await new Promise(resolve => setImmediate(resolve));
            registeredExtensionIds.clear();

            await service.installDebuggerExtension({
                debuggerName: 'Java',
                debuggerType: 'java',
                extensionIds: ['redhat.java', 'vscjava.vscode-java-debug'],
            });

            assert.strictEqual(showWarningMessage.callCount, 0);
        } finally {
            observation.dispose();
            dataChanges.dispose();
            extensionChanges.dispose();
        }
    });

    test('coalesces concurrent installs for the same debugger', async () => {
        let installed = false;
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            installed ? { id: extensionId } as vscode.Extension<unknown> : undefined);
        const showInformationMessage = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        let installResolve!: () => void;
        const installCommand = new Promise<void>(resolve => installResolve = resolve);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').returns(installCommand);
        const service = new DebuggerInstallHintService(createMemento());
        const hint = {
            debuggerName: 'Python',
            debuggerType: 'python',
            extensionIds: ['ms-python.debugpy'],
        };

        const firstInstallation = service.installDebuggerExtension(hint);
        const secondInstallation = service.installDebuggerExtension(hint);
        await Promise.resolve();
        installed = true;
        installResolve();
        await Promise.all([firstInstallation, secondInstallation]);

        assert.ok(executeCommand.calledOnceWithExactly(
            'workbench.extensions.installExtension',
            'ms-python.debugpy'));
        assert.strictEqual(showInformationMessage.callCount, 1);
    });

    test('allows retry after a failed install and suppresses setup notifications during the retry', async () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        const showWarningMessage = sinon.stub(vscode.window, 'showWarningMessage').resolves(undefined);
        sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        let rejectSecondInstall!: (reason: unknown) => void;
        const secondInstallCommand = new Promise<never>((_, reject) => rejectSecondInstall = reject);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand');
        executeCommand.onFirstCall().rejects(new Error('First install failed.'));
        executeCommand.onSecondCall().returns(secondInstallCommand);
        const service = new DebuggerInstallHintService(createMemento());
        const hint = {
            debuggerName: 'Bun',
            debuggerType: 'bun',
            extensionIds: ['oven.bun-vscode'],
        };

        const firstInstallation = service.installDebuggerExtension(hint);
        await firstInstallation;

        const secondInstallation = service.installDebuggerExtension(hint);
        await service.notifyMissingDebuggers([createResource('bun')]);

        assert.strictEqual(showWarningMessage.callCount, 0);

        rejectSecondInstall(new vscode.CancellationError());
        await assert.rejects(secondInstallation, error => isCommandCancellation(error));
    });

    test('reports a disabled debugger extension instead of claiming installation succeeded', async () => {
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const getExtension = sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        sinon.stub(vscode.extensions, 'onDidChange').returns({ dispose: sinon.stub() });
        const showWarningMessage = sinon.stub(vscode.window, 'showWarningMessage').resolves('Open Extensions' as any);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').resolves();
        const service = new DebuggerInstallHintService(createMemento());

        const installation = service.installDebuggerExtension({
            debuggerName: 'Java',
            debuggerType: 'java',
            extensionIds: ['redhat.java', 'vscjava.vscode-java-debug'],
        });
        await clock.tickAsync(5_000);
        await installation;

        assert.ok(executeCommand.firstCall.calledWithExactly(
            'workbench.extensions.installExtension',
            'redhat.java'));
        assert.ok(executeCommand.secondCall.calledWithExactly(
            'workbench.extensions.installExtension',
            'vscjava.vscode-java-debug'));
        assert.ok(getExtension.calledWith('redhat.java'));
        assert.ok(getExtension.calledWith('vscjava.vscode-java-debug'));
        assert.strictEqual(
            showWarningMessage.firstCall.args[0],
            'One or more extensions required for Java debugging did not become available. They may be disabled or require a window reload. Open the Extensions view to continue setup.');
        assert.deepStrictEqual(showWarningMessage.firstCall.args.slice(1), ['Open Extensions']);
        assert.ok(executeCommand.thirdCall.calledWithExactly(
            'workbench.extensions.search',
            '@id:redhat.java @id:vscjava.vscode-java-debug'));
    });

    test('reports debugger installation failures as handled command failures', async () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        const showInformationMessage = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        const error = new TypeError('Debugger installation failed.');
        sinon.stub(vscode.commands, 'executeCommand').rejects(error);
        const service = new DebuggerInstallHintService(createMemento());

        const result = await service.installDebuggerExtension({
            debuggerName: 'Python',
            debuggerType: 'python',
            extensionIds: ['ms-python.debugpy'],
        });

        assert.deepStrictEqual(result, { success: false, errorKind: 'TypeError' });
        assert.strictEqual(showErrorMessage.callCount, 1);
        // Asserting the literal text: comparing against errorMessage(error) passes even when the
        // localized placeholder is never substituted.
        assert.strictEqual(showErrorMessage.firstCall.args[0], 'Error: Debugger installation failed.');
        assert.strictEqual(showInformationMessage.callCount, 0);
    });

    test('treats a cancelled debugger setup as a dismissal rather than an error', async () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        const showInformationMessage = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        sinon.stub(vscode.commands, 'executeCommand').rejects(new vscode.CancellationError());
        const service = new DebuggerInstallHintService(createMemento());

        await assert.rejects(
            service.installDebuggerExtension({
                debuggerName: 'Bun',
                debuggerType: 'bun',
                extensionIds: ['oven.bun-vscode'],
            }),
            error => isCommandCancellation(error));

        assert.strictEqual(showErrorMessage.callCount, 0);
        assert.strictEqual(showInformationMessage.callCount, 0);
    });

    test('treats cancellation from the setup notification as a dismissal rather than an error', async () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        const showWarningMessage = sinon.stub(vscode.window, 'showWarningMessage').resolves(debuggerSetupAction as any);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        sinon.stub(vscode.commands, 'executeCommand').rejects(new vscode.CancellationError());
        const service = new DebuggerInstallHintService(createMemento());

        await service.notifyMissingDebuggers([createResource('bun')]);
        await service.notifyMissingDebuggers([createResource('bun')]);

        assert.strictEqual(showWarningMessage.callCount, 1);
        assert.strictEqual(showErrorMessage.callCount, 0);
    });

    test('installs only the missing Java debugger requirement', async () => {
        const registeredExtensionIds = new Set(['redhat.java']);
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            registeredExtensionIds.has(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined);
        const showInformationMessage = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').callsFake(async (_command, extensionId) => {
            registeredExtensionIds.add(extensionId as string);
        });
        const service = new DebuggerInstallHintService(createMemento());

        await service.installDebuggerExtension({
            debuggerName: 'Java',
            debuggerType: 'java',
            extensionIds: ['redhat.java', 'vscjava.vscode-java-debug'],
        });

        assert.ok(executeCommand.calledOnceWithExactly(
            'workbench.extensions.installExtension',
            'vscjava.vscode-java-debug'));
        assert.strictEqual(showInformationMessage.callCount, 1);
        assert.strictEqual(
            showInformationMessage.firstCall.args[0],
            'The extensions required for Java debugging are installed. Restart the AppHost to enable debugging.');
    });

    test('installs all missing Java requirements sequentially and waits for every registration', async () => {
        const registeredExtensionIds = new Set<string>();
        let extensionChangeListener: (() => unknown) | undefined;
        let subscriptionRegisteredResolve!: () => void;
        const subscriptionRegistered = new Promise<void>(resolve => subscriptionRegisteredResolve = resolve);
        sinon.stub(vscode.extensions, 'getExtension').callsFake(extensionId =>
            registeredExtensionIds.has(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined);
        const onDidChange: vscode.Event<void> = listener => {
            extensionChangeListener = listener;
            subscriptionRegisteredResolve();
            return { dispose: sinon.stub() };
        };
        sinon.stub(vscode.extensions, 'onDidChange').get(() => onDidChange);
        const showInformationMessage = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
        let firstInstallResolve!: () => void;
        const firstInstall = new Promise<void>(resolve => firstInstallResolve = resolve);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand');
        executeCommand.onFirstCall().returns(firstInstall);
        executeCommand.onSecondCall().resolves();
        const service = new DebuggerInstallHintService(createMemento());

        const installation = service.installDebuggerExtension({
            debuggerName: 'Java',
            debuggerType: 'java',
            extensionIds: ['redhat.java', 'vscjava.vscode-java-debug'],
        });
        await Promise.resolve();

        assert.ok(executeCommand.firstCall.calledWithExactly(
            'workbench.extensions.installExtension',
            'redhat.java'));
        assert.strictEqual(executeCommand.callCount, 1);

        firstInstallResolve();
        await subscriptionRegistered;

        assert.ok(executeCommand.secondCall.calledWithExactly(
            'workbench.extensions.installExtension',
            'vscjava.vscode-java-debug'));
        assert.ok(extensionChangeListener);
        assert.strictEqual(showInformationMessage.callCount, 0);

        registeredExtensionIds.add('redhat.java');
        extensionChangeListener();
        await Promise.resolve();
        assert.strictEqual(showInformationMessage.callCount, 0);

        registeredExtensionIds.add('vscjava.vscode-java-debug');
        extensionChangeListener();
        await installation;

        assert.strictEqual(showInformationMessage.callCount, 1);
        assert.strictEqual(
            showInformationMessage.firstCall.args[0],
            'The extensions required for Java debugging are installed. Restart the AppHost to enable debugging.');
    });

    test('starts background observation only after discovering an AppHost candidate', () => {
        const dataChanges = new vscode.EventEmitter<void>();
        const candidatePaths: string[] = [];
        const keepDataActive = sinon.stub().returns({ dispose: sinon.stub() });
        const service = new DebuggerInstallHintService(createMemento());
        const observation = service.watchForMissingDebuggers({
            get workspaceAppHostCandidatePaths() {
                return candidatePaths;
            },
            workspaceResources: [],
            appHosts: [],
            onDidChangeData: dataChanges.event,
            keepDataActive,
        });

        try {
            assert.strictEqual(keepDataActive.callCount, 0);

            candidatePaths.push('/workspace/AppHost.csproj');
            dataChanges.fire();

            assert.strictEqual(keepDataActive.callCount, 1);
        } finally {
            observation.dispose();
            dataChanges.dispose();
        }
    });

    test('stops background observation after the last AppHost candidate is removed', () => {
        const dataChanges = new vscode.EventEmitter<void>();
        const candidatePaths = ['/workspace/AppHost.csproj'];
        const dataLease = { dispose: sinon.stub() };
        const service = new DebuggerInstallHintService(createMemento());
        const observation = service.watchForMissingDebuggers({
            get workspaceAppHostCandidatePaths() {
                return candidatePaths;
            },
            workspaceResources: [],
            appHosts: [],
            onDidChangeData: dataChanges.event,
            keepDataActive: sinon.stub().returns(dataLease),
        });

        try {
            assert.strictEqual(dataLease.dispose.callCount, 0);

            candidatePaths.splice(0);
            dataChanges.fire();

            assert.strictEqual(dataLease.dispose.callCount, 1);
        } finally {
            observation.dispose();
            dataChanges.dispose();
        }
    });

    test("Don't Show Again suppresses future sessions for that debugger", async () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        const showWarningMessage = sinon.stub(vscode.window, 'showWarningMessage').resolves(dontShowAgainLabel as any);
        const globalState = createMemento();

        const firstService = new DebuggerInstallHintService(globalState);
        await firstService.notifyMissingDebuggers([createResource('go')]);

        const secondService = new DebuggerInstallHintService(globalState);
        await secondService.notifyMissingDebuggers([createResource('go')]);

        assert.strictEqual(showWarningMessage.callCount, 1);
    });

    test('uses stable logical debugger types for notification suppression', async () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
        const showWarningMessage = sinon.stub(vscode.window, 'showWarningMessage').resolves(dontShowAgainLabel as any);
        const globalState = createMemento();
        const service = new DebuggerInstallHintService(globalState);

        await service.notifyMissingDebuggers([
            createResource('java'),
            createResource('java'),
            createResource('rust'),
        ]);

        assert.strictEqual(showWarningMessage.callCount, 2);
        assert.deepStrictEqual(
            [...globalState.keys()].sort(),
            [
                'aspire.debuggerInstallHint.suppressed.java',
                'aspire.debuggerInstallHint.suppressed.rust',
            ]);
    });
});
