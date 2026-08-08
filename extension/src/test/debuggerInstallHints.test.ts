/// <reference types="mocha" />

import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import {
    DebuggableResourceSnapshot,
    DebuggerInstallHint,
    DebuggerInstallHintService,
    DebuggerInstallHintServiceDependencies,
    getDebuggerInstallHintForResource,
    installDebuggerExtension,
    launchConfigurationTypePropertyName,
} from '../debugger/debuggerInstallHints';
import { bunDebuggerExtension } from '../debugger/languages/bun';
import { goDebuggerExtension } from '../debugger/languages/go';
import { pythonDebuggerExtension } from '../debugger/languages/python';
import { ResourceState } from '../editor/resourceConstants';
import {
    debuggerInstallAction,
    debuggerInstalledRestartAppHost,
    debuggerInstallFailed,
    dontShowAgainLabel,
} from '../loc/strings';

/**
 * Builds the part of a resource snapshot the install hints read. The property name is spelled out
 * rather than taken from the exported constant because it is a contract with Aspire.Hosting
 * (`KnownProperties.Resource.LaunchConfigurationType`) that must not change silently.
 */
function createResource(launchConfigurationType: string | null, state: string | null = ResourceState.Running): DebuggableResourceSnapshot {
    return {
        state,
        properties: launchConfigurationType === null
            ? null
            : { 'resource.launchConfigurationType': launchConfigurationType },
    };
}

function hintFor(launchConfigurationType: string): DebuggerInstallHint {
    const hint = getDebuggerInstallHintForResource(createResource(launchConfigurationType));
    assert.ok(hint, `expected an install hint for '${launchConfigurationType}'`);
    return hint;
}

function createTestMemento(): vscode.Memento {
    const values = new Map<string, unknown>();
    return {
        keys: () => [...values.keys()],
        get: <T>(key: string, defaultValue?: T) => values.has(key) ? values.get(key) as T : defaultValue,
        update: (key: string, value: unknown) => {
            if (value === undefined) {
                values.delete(key);
            } else {
                values.set(key, value);
            }
            return Promise.resolve();
        },
        setKeysForSync: () => { },
    } as vscode.Memento;
}

function createDependencies(overrides: Partial<DebuggerInstallHintServiceDependencies> = {}): {
    dependencies: DebuggerInstallHintServiceDependencies;
    extensionChanges: vscode.EventEmitter<void>;
} {
    const extensionChanges = new vscode.EventEmitter<void>();
    return {
        dependencies: {
            getExtension: () => undefined,
            onDidChangeExtensions: extensionChanges.event,
            showInformationMessage: () => Promise.resolve(undefined),
            showErrorMessage: () => Promise.resolve(undefined),
            installExtension: () => Promise.resolve(),
            ...overrides,
        },
        extensionChanges,
    };
}

suite('debugger install hints', () => {
    teardown(() => sinon.restore());

    test('maps every launch configuration type Aspire can debug to its debug adapter extension', () => {
        const mappings = [
            ['project', 'ms-dotnettools.csharp', 'C#'],
            ['azure-functions', 'ms-dotnettools.csharp', 'C#'],
            ['python', 'ms-python.debugpy', 'Python'],
            ['go', 'golang.go', 'Go'],
            ['bun', 'oven.bun-vscode', 'Bun'],
            ['maui', 'ms-dotnettools.dotnet-maui', '.NET MAUI'],
        ];

        assert.deepStrictEqual(
            mappings.map(([launchConfigurationType]) => {
                const hint = getDebuggerInstallHintForResource(createResource(launchConfigurationType));
                return [launchConfigurationType, hint?.extensionId, hint?.debuggerName];
            }),
            mappings);
    });

    test('has no hint for adapters built into VS Code, unknown types, or resources without debug support', () => {
        assert.deepStrictEqual(
            [
                // js-debug ships with VS Code, so there is nothing to install.
                getDebuggerInstallHintForResource(createResource('node')),
                getDebuggerInstallHintForResource(createResource('browser')),
                // A launch configuration type from an integration this build does not know about.
                getDebuggerInstallHintForResource(createResource('contoso')),
                // A resource with no SupportsDebuggingAnnotation publishes no property at all.
                getDebuggerInstallHintForResource(createResource(null)),
                getDebuggerInstallHintForResource({ state: ResourceState.Running, properties: {} }),
            ],
            [undefined, undefined, undefined, undefined, undefined]);
    });

    test('reads the launch configuration type property Aspire.Hosting publishes', () => {
        assert.strictEqual(launchConfigurationTypePropertyName, 'resource.launchConfigurationType');
    });

    test('reuses the extension ids registered by the resource debugger extensions', () => {
        assert.deepStrictEqual(
            [
                hintFor('python').extensionId,
                hintFor('go').extensionId,
                hintFor('bun').extensionId,
            ],
            [
                pythonDebuggerExtension.extensionId,
                goDebuggerExtension.extensionId,
                bunDebuggerExtension.extensionId,
            ]);
    });

    test('only returns hints while the debugger extension is missing', () => {
        const installed = new Set(['ms-python.debugpy']);
        const { dependencies, extensionChanges } = createDependencies({
            getExtension: extensionId => installed.has(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined,
        });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        assert.strictEqual(service.getMissingDebugger(createResource('python')), undefined);
        assert.strictEqual(service.getMissingDebugger(createResource('go'))?.extensionId, 'golang.go');

        service.dispose();
        extensionChanges.dispose();
    });

    test('shows one notification per missing extension id in an extension session', async () => {
        const globalState = createTestMemento();
        const showInformationMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({ showInformationMessage });
        const service = new DebuggerInstallHintService(globalState, dependencies);

        await service.showNotificationIfNeeded(hintFor('python'));
        await service.showNotificationIfNeeded(hintFor('python'));
        await service.showNotificationIfNeeded(hintFor('go'));
        await service.showNotificationIfNeeded(hintFor('go'));

        assert.strictEqual(showInformationMessage.callCount, 2);
        service.dispose();
        extensionChanges.dispose();
    });

    test('coalesces concurrent notifications for the same missing extension', async () => {
        let resolveNotification!: (selection: string | undefined) => void;
        const notification = new Promise<string | undefined>(resolve => resolveNotification = resolve);
        const showInformationMessage = sinon.stub().returns(notification);
        const { dependencies, extensionChanges } = createDependencies({ showInformationMessage });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        const first = service.showNotificationIfNeeded(hintFor('python'));
        const second = service.showNotificationIfNeeded(hintFor('python'));

        assert.strictEqual(showInformationMessage.callCount, 1);
        resolveNotification(undefined);
        await Promise.all([first, second]);

        service.dispose();
        extensionChanges.dispose();
    });

    test('dismissal allows notification in a future extension session', async () => {
        const globalState = createTestMemento();
        const showInformationMessage = sinon.stub().resolves(undefined);
        const first = createDependencies({ showInformationMessage });
        const firstService = new DebuggerInstallHintService(globalState, first.dependencies);

        await firstService.showNotificationIfNeeded(hintFor('python'));
        firstService.dispose();
        first.extensionChanges.dispose();

        const second = createDependencies({ showInformationMessage });
        const secondService = new DebuggerInstallHintService(globalState, second.dependencies);
        await secondService.showNotificationIfNeeded(hintFor('python'));

        assert.strictEqual(showInformationMessage.callCount, 2);
        secondService.dispose();
        second.extensionChanges.dispose();
    });

    test("Don't show again suppresses notifications in future extension sessions", async () => {
        const globalState = createTestMemento();
        const showInformationMessage = sinon.stub().resolves(dontShowAgainLabel);
        const first = createDependencies({ showInformationMessage });
        const firstService = new DebuggerInstallHintService(globalState, first.dependencies);

        await firstService.showNotificationIfNeeded(hintFor('go'));
        firstService.dispose();
        first.extensionChanges.dispose();

        const second = createDependencies({ showInformationMessage });
        const secondService = new DebuggerInstallHintService(globalState, second.dependencies);
        await secondService.showNotificationIfNeeded(hintFor('go'));

        assert.strictEqual(showInformationMessage.callCount, 1);
        secondService.dispose();
        second.extensionChanges.dispose();
    });

    test('notification install action installs the mapped extension', async () => {
        const installExtension = sinon.stub().resolves();
        const showInformationMessage = sinon.stub().resolves(debuggerInstallAction);
        const { dependencies, extensionChanges } = createDependencies({
            showInformationMessage,
            installExtension,
        });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        await service.showNotificationIfNeeded(hintFor('bun'));

        assert.strictEqual(showInformationMessage.firstCall.args[1], debuggerInstallAction);
        assert.strictEqual(showInformationMessage.firstCall.args[2], dontShowAgainLabel);
        assert.ok(installExtension.calledOnceWithExactly('oven.bun-vscode'));
        service.dispose();
        extensionChanges.dispose();
    });

    test('notification suppression does not install an extension', async () => {
        const installExtension = sinon.stub().resolves();
        const { dependencies, extensionChanges } = createDependencies({
            showInformationMessage: () => Promise.resolve(dontShowAgainLabel),
            installExtension,
        });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        await service.showNotificationIfNeeded(hintFor('go'));

        assert.strictEqual(installExtension.callCount, 0);
        service.dispose();
        extensionChanges.dispose();
    });

    test("does not durably suppress after saving Don't show again fails", async () => {
        let notificationShown = false;
        let updateCount = 0;
        const globalState = {
            keys: () => notificationShown ? ['shown'] : [],
            get: <T>(_key: string, defaultValue?: T) => notificationShown ? true as T : defaultValue,
            update: () => {
                updateCount++;
                if (updateCount === 1) {
                    return Promise.reject(new Error('persistence failed'));
                }

                notificationShown = true;
                return Promise.resolve();
            },
            setKeysForSync: () => { },
        } as vscode.Memento;
        const showInformationMessage = sinon.stub().resolves(dontShowAgainLabel);
        const first = createDependencies({ showInformationMessage });
        const firstService = new DebuggerInstallHintService(globalState, first.dependencies);
        const hint = hintFor('go');

        await assert.rejects(firstService.showNotificationIfNeeded(hint), /persistence failed/);
        firstService.dispose();
        first.extensionChanges.dispose();

        const second = createDependencies({ showInformationMessage });
        const secondService = new DebuggerInstallHintService(globalState, second.dependencies);
        await secondService.showNotificationIfNeeded(hint);

        assert.strictEqual(showInformationMessage.callCount, 2);
        secondService.dispose();
        second.extensionChanges.dispose();
    });

    test('retries notification after display fails', async () => {
        const showInformationMessage = sinon.stub();
        showInformationMessage.onFirstCall().rejects(new Error('display failed'));
        showInformationMessage.onSecondCall().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({ showInformationMessage });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);
        const hint = hintFor('bun');

        await assert.rejects(service.showNotificationIfNeeded(hint), /display failed/);
        await service.showNotificationIfNeeded(hint);

        assert.strictEqual(showInformationMessage.callCount, 2);
        service.dispose();
        extensionChanges.dispose();
    });

    test('install command delegates to VS Code extension installation', async () => {
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').resolves();

        await installDebuggerExtension('ms-python.debugpy');

        assert.ok(executeCommand.calledOnceWithExactly('workbench.extensions.installExtension', 'ms-python.debugpy'));
    });

    test('guides the user to restart the AppHost once the installed extension becomes available', async () => {
        const installed = new Set<string>();
        const showInformationMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({
            getExtension: extensionId => installed.has(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined,
            showInformationMessage,
            // The install command resolves before the extension host publishes the extension, so
            // `getExtension` still reports it as missing when `installExtension` returns.
            installExtension: () => Promise.resolve(),
        });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        await service.installExtension(hintFor('go'));
        assert.strictEqual(showInformationMessage.callCount, 0);

        installed.add('golang.go');
        extensionChanges.fire();

        assert.deepStrictEqual(
            showInformationMessage.args,
            [[debuggerInstalledRestartAppHost('Go')]]);

        extensionChanges.fire();
        assert.strictEqual(showInformationMessage.callCount, 1);

        service.dispose();
        extensionChanges.dispose();
    });

    test('reports install failures and keeps the hint available', async () => {
        const showErrorMessage = sinon.stub().resolves(undefined);
        const showInformationMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({
            showErrorMessage,
            showInformationMessage,
            installExtension: () => Promise.reject(new Error('offline')),
        });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        await service.installExtension(hintFor('bun'));

        assert.deepStrictEqual(
            showErrorMessage.args,
            [[debuggerInstallFailed('Bun', 'offline')]]);
        assert.strictEqual(showInformationMessage.callCount, 0);
        assert.strictEqual(service.getMissingDebugger(createResource('bun'))?.extensionId, 'oven.bun-vscode');

        service.dispose();
        extensionChanges.dispose();
    });

    test('install failure from the notification action does not reject', async () => {
        const showErrorMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({
            showErrorMessage,
            showInformationMessage: () => Promise.resolve(debuggerInstallAction),
            installExtension: () => Promise.reject(new Error('no marketplace')),
        });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        await service.showNotificationIfNeeded(hintFor('python'));

        assert.deepStrictEqual(
            showErrorMessage.args,
            [[debuggerInstallFailed('Python', 'no marketplace')]]);

        service.dispose();
        extensionChanges.dispose();
    });

    test('extension changes refresh missing hints and supported capabilities', () => {
        let pythonInstalled = false;
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) =>
            pythonInstalled && extensionId === 'ms-python.debugpy'
                ? { id: extensionId } as vscode.Extension<unknown>
                : undefined);
        const { dependencies, extensionChanges } = createDependencies({
            getExtension: extensionId => vscode.extensions.getExtension(extensionId),
        });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);
        let refreshCount = 0;
        const subscription = service.onDidChange(() => refreshCount++);

        assert.strictEqual(service.getMissingDebugger(createResource('python'))?.extensionId, 'ms-python.debugpy');
        assert.ok(!getSupportedCapabilities().includes('python'));

        pythonInstalled = true;
        extensionChanges.fire();

        assert.strictEqual(refreshCount, 1);
        assert.strictEqual(service.getMissingDebugger(createResource('python')), undefined);
        assert.ok(getSupportedCapabilities().includes('python'));

        subscription.dispose();
        service.dispose();
        extensionChanges.dispose();
    });

    test('notifies for running resources whose debug adapter is missing', async () => {
        const showInformationMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({ showInformationMessage });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        service.notifyMissingDebuggers([
            createResource('python'),
            createResource('go'),
        ]);
        await Promise.resolve();

        assert.deepStrictEqual(
            showInformationMessage.args.map(args => args[0]),
            [
                'Install the Python debugger extension to debug resources in this app.',
                'Install the Go debugger extension to debug resources in this app.',
            ]);

        service.dispose();
        extensionChanges.dispose();
    });

    test('notifies once per debug adapter no matter how many resources need it', async () => {
        const showInformationMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({ showInformationMessage });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        // Several Python resources, and a project and an Azure Functions resource that share the C#
        // extension, spread across what would be two AppHosts.
        service.notifyMissingDebuggers([
            createResource('python'),
            createResource('python'),
            createResource('python'),
            createResource('project'),
            createResource('azure-functions'),
        ]);
        await Promise.resolve();
        service.notifyMissingDebuggers([createResource('python')]);
        await Promise.resolve();

        assert.deepStrictEqual(
            showInformationMessage.args.map(args => args[0]),
            [
                'Install the Python debugger extension to debug resources in this app.',
                'Install the C# debugger extension to debug resources in this app.',
            ]);

        service.dispose();
        extensionChanges.dispose();
    });

    test('ignores resources that are not running yet', async () => {
        const showInformationMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({ showInformationMessage });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        service.notifyMissingDebuggers([
            createResource('python', ResourceState.Starting),
            createResource('go', ResourceState.Exited),
            createResource('bun', null),
        ]);
        await Promise.resolve();

        assert.strictEqual(showInformationMessage.callCount, 0);

        // The same resource reaching Running does prompt, so the state check only defers the hint.
        service.notifyMissingDebuggers([createResource('python')]);
        await Promise.resolve();

        assert.deepStrictEqual(
            showInformationMessage.args.map(args => args[0]),
            ['Install the Python debugger extension to debug resources in this app.']);

        service.dispose();
        extensionChanges.dispose();
    });

    test('ignores resources with no debug support or a built-in debug adapter', async () => {
        const showInformationMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({ showInformationMessage });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        service.notifyMissingDebuggers([
            createResource(null),
            createResource('node'),
            createResource('browser'),
            createResource('contoso'),
        ]);
        await Promise.resolve();

        assert.strictEqual(showInformationMessage.callCount, 0);

        service.dispose();
        extensionChanges.dispose();
    });

    test('does not notify for debug adapters that are already installed', async () => {
        const installed = new Set(['ms-python.debugpy']);
        const showInformationMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({
            getExtension: extensionId => installed.has(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined,
            showInformationMessage,
        });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        service.notifyMissingDebuggers([createResource('python'), createResource('go')]);
        await Promise.resolve();

        assert.deepStrictEqual(
            showInformationMessage.args.map(args => args[0]),
            ['Install the Go debugger extension to debug resources in this app.']);

        service.dispose();
        extensionChanges.dispose();
    });

    test('does not notify after dispose', async () => {
        const showInformationMessage = sinon.stub().resolves(undefined);
        const { dependencies, extensionChanges } = createDependencies({ showInformationMessage });
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);

        service.dispose();
        service.notifyMissingDebuggers([createResource('python')]);
        await Promise.resolve();

        assert.strictEqual(showInformationMessage.callCount, 0);
        extensionChanges.dispose();
    });

    test('dispose removes the extension change listener', () => {
        const { dependencies, extensionChanges } = createDependencies();
        const service = new DebuggerInstallHintService(createTestMemento(), dependencies);
        let refreshCount = 0;
        service.onDidChange(() => refreshCount++);

        service.dispose();
        extensionChanges.fire();

        assert.strictEqual(refreshCount, 0);
        extensionChanges.dispose();
    });
});
