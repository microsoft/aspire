import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession, getLoggableDebugConfiguration } from '../debugger/AspireDebugSession';
import { createDebugSessionConfiguration, getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { executeMauiCommandWithTimeout, useMauiDeviceListProviderForTests } from '../debugger/languages/maui';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import { runWithRunStartWrappers } from '../debugger/runStartRegistry';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration } from '../dcp/types';
import { getEnvironmentWithoutE2EBridgeVariables } from '../utils/environment';

async function delay(ms: number): Promise<void> {
    await new Promise(resolve => setTimeout(resolve, ms));
}

suite('MAUI Debugger Extension Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    teardown(() => {
        useMauiDeviceListProviderForTests(undefined);
        sinon.restore();
    });

    test('advertises MAUI support when the MAUI extension is installed', () => {
        installMauiExtensionStub();

        const capabilities = getSupportedCapabilities() as string[];
        assert.ok(capabilities.includes('maui'));
        assert.ok(capabilities.includes('ms-dotnettools.dotnet-maui'));
        assert.ok(getResourceDebuggerExtensions().some(extension => extension.resourceType === 'maui'));
    });

    test('does not advertise MAUI support when the MAUI extension is not installed', () => {
        installNoMauiExtensionStub();

        const capabilities = getSupportedCapabilities() as string[];
        assert.ok(!capabilities.includes('maui'));
        assert.ok(!capabilities.includes('ms-dotnettools.dotnet-maui'));
        assert.ok(!getResourceDebuggerExtensions().some(extension => extension.resourceType === 'maui'));
    });

    test('configures VS Code MAUI debugger for Mac Catalyst', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            target_kind: 'device',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        const originalServiceName = process.env.OTEL_SERVICE_NAME;
        try {
            await debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-maccatalyst', '-r', 'maccatalyst-arm64', '-p:OpenArguments=-W', '-p:CustomAfterMicrosoftCommonTargets=/tmp/maui.env.targets'],
                [{ name: 'OTEL_SERVICE_NAME', value: 'mauiapp-maccatalyst' }],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                debugConfig);
        } finally {
            cleanupRun('1');
            if (originalServiceName === undefined) {
                delete process.env.OTEL_SERVICE_NAME;
            } else {
                process.env.OTEL_SERVICE_NAME = originalServiceName;
            }
        }

        assert.strictEqual(debugConfig.type, 'maui');
        assert.strictEqual(debugConfig.request, 'launch');
        assert.strictEqual(debugConfig.project, '/workspace/MauiApp/MauiApp.csproj');
        assert.strictEqual(debugConfig.configuration, 'Debug');
        assert.strictEqual(debugConfig.targetFramework, 'net10.0-maccatalyst');
        assert.strictEqual(debugConfig.platform, 'maccatalyst');
        assert.strictEqual(debugConfig.device, 'my-mac');
        assert.strictEqual(debugConfig.runtimeIdentifier, 'maccatalyst-arm64');
        assertMsBuildProperties(debugConfig, {
            OpenArguments: '-W',
            CustomAfterMicrosoftCommonTargets: '/tmp/maui.env.targets',
        });
        assert.deepStrictEqual(getLoggableDebugConfiguration(debugConfig, false).msbuildProperties, {
            OpenArguments: '-W',
            CustomAfterMicrosoftCommonTargets: '/tmp/maui.env.targets',
        });
        assert.strictEqual(debugConfig.environmentVariables, 'OTEL_SERVICE_NAME=mauiapp-maccatalyst');
        assert.strictEqual(debugConfig.noDebug, false);
        assert.strictEqual(debugConfig.skipDebug, false);
        assert.strictEqual(debugConfig.args, undefined);
    });

    test('uses VS Code bridge default targets for desktop MAUI platforms', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const macDebugConfig = createDebugConfig('maui-mac-default-target');
        await debuggerExtension.createDebugSessionConfigurationCallback!(
            {
                type: 'maui',
                project_path: '/workspace/MauiApp/MauiApp.csproj',
                target_framework: 'net10.0-maccatalyst',
                platform: 'maccatalyst',
                target_kind: 'device'
            } as ExecutableLaunchConfiguration,
            ['run', '-f', 'net10.0-maccatalyst'],
            [],
            { debug: true, runId: 'maui-mac-default-target', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            macDebugConfig);

        const windowsDebugConfig = createDebugConfig('maui-windows-default-target');
        await debuggerExtension.createDebugSessionConfigurationCallback!(
            {
                type: 'maui',
                project_path: 'C:\\workspace\\MauiApp\\MauiApp.csproj',
                target_framework: 'net10.0-windows10.0.19041.0',
                platform: 'windows',
                target_kind: 'device'
            } as ExecutableLaunchConfiguration,
            ['run', '-f', 'net10.0-windows10.0.19041.0'],
            [],
            { debug: true, runId: 'maui-windows-default-target', debugSessionId: '2', isApphost: false, debugSession: fakeAspireDebugSession },
            windowsDebugConfig);

        assert.strictEqual(macDebugConfig.device, 'my-mac');
        assert.strictEqual(windowsDebugConfig.device, 'windowsmachine');
    });

    test('sets the MAUI debug session id used by the MAUI launch pipeline', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const debugConfig = createDebugConfig('maui-ios-session-id');

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            {
                type: 'maui',
                project_path: '/workspace/MauiApp/MauiApp.csproj',
                target_framework: 'net10.0-ios',
                platform: 'ios',
                target_kind: 'simulator',
                device: 'ios-simulator-udid'
            } as ExecutableLaunchConfiguration,
            ['run', '-f', 'net10.0-ios'],
            [],
            { debug: true, runId: 'maui-ios-session-id', debugSessionId: 'aspire-session', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.sessionId, 'maui-ios-session-id');
    });

    test('uses the MAUI extension dynamic build task without requiring a workspace task definition', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-ios',
            platform: 'ios',
            target_kind: 'simulator',
            device: 'E25BBE37-69BA-4720-B6FD-D54C97791E79'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-ios', '-p:_DeviceName=:v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.preLaunchTask, 'maui: Build');
        assert.strictEqual(debugConfig.device, 'E25BBE37-69BA-4720-B6FD-D54C97791E79');
        assert.strictEqual(debugConfig.runtimeIdentifier, process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64');
        assert.strictEqual(debugConfig.isEmulator, true);
        assert.strictEqual(debugConfig.debugTarget, 'E25BBE37-69BA-4720-B6FD-D54C97791E79');
        assertMsBuildProperties(debugConfig, {
            _DeviceName: ':v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79',
            RuntimeIdentifier: process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64',
        });
    });

    test('does not pass Aspire extension backchannel variables to the MAUI debug adapter', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            target_kind: 'device',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;
        const previousCommonPropertyBagPath = process.env.CommonPropertyBagPath;
        const previousHostOnlySecret = process.env.HOST_ONLY_SECRET;
        process.env.CommonPropertyBagPath = '/Users/test/Library/Application Support/csdevkit/db.json';
        process.env.HOST_ONLY_SECRET = 'host-secret';

        const debugConfig = await createDebugSessionConfiguration(
            { type: 'aspire', request: 'launch', name: 'Aspire', program: '' },
            launchConfig,
            ['run', '-f', 'net10.0-maccatalyst'],
            [
                { name: 'ASPIRE_EXTENSION_ENDPOINT', value: 'localhost:1234' },
                { name: 'ASPIRE_EXTENSION_TOKEN', value: 'secret-token' },
                { name: 'ASPIRE_EXTENSION_CERT', value: 'secret-cert' },
                { name: 'ASPIRE_BACKCHANNEL_PATH', value: '/tmp/aspire.sock' },
                { name: 'ASPIRE_CLI_PID', value: '12345' },
                { name: 'ASPIRE_CLI_CUSTOM_TEST', value: 'cli-infra' },
                { name: 'ASPIRE_TERMINAL_HOST_CUSTOM_TEST', value: 'terminal-infra' },
                { name: 'VSCODE_NLS_CONFIG', value: '{"locale":"en"}' },
                { name: 'CommonPropertyBagPath', value: '/Users/test/Library/Application Support/csdevkit/db.json' },
                { name: 'HOST_ONLY_SECRET', value: 'host-secret' },
                { name: 'OTEL_SERVICE_NAME', value: 'mobile-maccatalyst' },
                { name: 'SSL_CERT_DIR', value: '/tmp/aspire-dcp/mobile/certs' },
            ],
            { debug: true, runId: 'maui-env-filter', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debuggerExtension);

        try {
            assert.strictEqual(debugConfig.env?.ASPIRE_EXTENSION_ENDPOINT, undefined);
            assert.strictEqual(debugConfig.env?.ASPIRE_EXTENSION_TOKEN, undefined);
            assert.strictEqual(debugConfig.env?.ASPIRE_EXTENSION_CERT, undefined);
            assert.strictEqual(debugConfig.env?.ASPIRE_BACKCHANNEL_PATH, undefined);
            assert.strictEqual(debugConfig.env?.ASPIRE_CLI_PID, undefined);
            assert.strictEqual(debugConfig.environmentVariables?.includes('ASPIRE_EXTENSION_ENDPOINT='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('ASPIRE_EXTENSION_TOKEN='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('ASPIRE_EXTENSION_CERT='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('ASPIRE_BACKCHANNEL_PATH='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('ASPIRE_CLI_PID='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('ASPIRE_CLI_CUSTOM_TEST='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('ASPIRE_TERMINAL_HOST_CUSTOM_TEST='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('VSCODE_NLS_CONFIG='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('CommonPropertyBagPath='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('HOST_ONLY_SECRET='), false);
            assert.strictEqual(debugConfig.environmentVariables?.includes('OTEL_SERVICE_NAME=mobile-maccatalyst'), true);
            assert.strictEqual(debugConfig.environmentVariables?.includes('SSL_CERT_DIR=/tmp/aspire-dcp/mobile/certs'), true);
        } finally {
            if (previousCommonPropertyBagPath === undefined) {
                delete process.env.CommonPropertyBagPath;
            } else {
                process.env.CommonPropertyBagPath = previousCommonPropertyBagPath;
            }
            if (previousHostOnlySecret === undefined) {
                delete process.env.HOST_ONLY_SECRET;
            } else {
                process.env.HOST_ONLY_SECRET = previousHostOnlySecret;
            }
            cleanupRun('maui-env-filter');
        }
    });

    test('includes the MAUI target in the debug session name when available', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'Pixel_9a',
                platform: 'android',
                platforms: ['android'],
                isEmulator: true,
                isRunning: false,
                name: 'Pixel 9a'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const debugConfig = await createDebugSessionConfiguration(
            { type: 'aspire', request: 'launch', name: 'Aspire', program: '' },
            {
                type: 'maui',
                project_path: '/workspace/MauiApp/MauiApp.csproj',
                target_framework: 'net10.0-android',
                platform: 'android',
                target_kind: 'emulator',
                device: 'Pixel_9a',
                msbuild_properties: {
                    AdbTarget: '-s Pixel_9a'
                }
            } as ExecutableLaunchConfiguration,
            ['run', '-f', 'net10.0-android'],
            [],
            { debug: true, runId: 'maui-session-name', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debuggerExtension);

        assert.strictEqual(debugConfig.name, 'Debug MAUI: /workspace/MauiApp/MauiApp.csproj (android emulator Pixel_9a)');
    });

    test('temporarily enables MAUI launch-json configurations while starting a run', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const updates: Array<{ key: string; value: unknown; target: vscode.ConfigurationTarget | boolean | null | undefined }> = [];
        const getConfigurationStub = sinon.stub(vscode.workspace, 'getConfiguration').returns({
            get: (key: string, defaultValue?: unknown) => key === 'maui.configuration.useLaunchJsonConfigurations' ? false : defaultValue,
            inspect: (key: string) => key === 'maui.configuration.useLaunchJsonConfigurations' ? {} : undefined,
            update: async (key: string, value: unknown, target?: vscode.ConfigurationTarget | boolean | null) => {
                updates.push({ key, value, target });
            }
        } as unknown as vscode.WorkspaceConfiguration);
        const getCommandsStub = sinon.stub(vscode.commands, 'getCommands').resolves(['vscode-maui.mauiStartDebugSession']);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves();

        try {
            const launchConfig = {
                type: 'maui',
                project_path: '/workspace/MauiApp/MauiApp.csproj',
                target_framework: 'net10.0-maccatalyst',
                platform: 'maccatalyst',
                target_kind: 'device',
                device: 'my-mac'
            } as ExecutableLaunchConfiguration;

            await debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-maccatalyst'],
                [],
                { debug: true, runId: 'maui-launch-json-mode', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig('maui-launch-json-mode'));

            assert.strictEqual(updates.length, 0);
            await runWithRunStartWrappers('maui-launch-json-mode', async () => {
                assert.deepStrictEqual(updates, [{
                    key: 'maui.configuration.useLaunchJsonConfigurations',
                    value: true,
                    target: vscode.ConfigurationTarget.WorkspaceFolder
                }]);
                assert.strictEqual(executeCommandStub.notCalled, true);
            });

            assert.deepStrictEqual(updates, [
                {
                    key: 'maui.configuration.useLaunchJsonConfigurations',
                    value: true,
                    target: vscode.ConfigurationTarget.WorkspaceFolder
                },
                {
                    key: 'maui.configuration.useLaunchJsonConfigurations',
                    value: undefined,
                    target: vscode.ConfigurationTarget.WorkspaceFolder
                }
            ]);
        } finally {
            cleanupRun('maui-launch-json-mode');
            getConfigurationStub.restore();
            getCommandsStub.restore();
            executeCommandStub.restore();
        }
    });

    test('temporarily overrides MAUI launch-json workspace folder settings while starting a run', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const updates: Array<{ key: string; value: unknown; target: vscode.ConfigurationTarget | boolean | null | undefined }> = [];
        const getConfigurationStub = sinon.stub(vscode.workspace, 'getConfiguration').callsFake((_section?: string, scope?: vscode.ConfigurationScope | null) => {
            assert.strictEqual(scope instanceof vscode.Uri ? path.normalize(scope.fsPath) : undefined, path.normalize('/workspace/MauiApp/MauiApp.csproj'));

            return {
                get: (key: string, defaultValue?: unknown) => key === 'maui.configuration.useLaunchJsonConfigurations' ? false : defaultValue,
                inspect: (key: string) => key === 'maui.configuration.useLaunchJsonConfigurations' ? { workspaceFolderValue: false } : undefined,
                update: async (key: string, value: unknown, target?: vscode.ConfigurationTarget | boolean | null) => {
                    updates.push({ key, value, target });
                }
            } as unknown as vscode.WorkspaceConfiguration;
        });
        const getCommandsStub = sinon.stub(vscode.commands, 'getCommands').resolves(['vscode-maui.mauiStartDebugSession']);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves();

        try {
            const launchConfig = {
                type: 'maui',
                project_path: '/workspace/MauiApp/MauiApp.csproj',
                target_framework: 'net10.0-maccatalyst',
                platform: 'maccatalyst',
                target_kind: 'device',
                device: 'my-mac'
            } as ExecutableLaunchConfiguration;

            await debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-maccatalyst'],
                [],
                { debug: true, runId: 'maui-launch-json-workspace-folder-mode', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig('maui-launch-json-workspace-folder-mode'));

            await runWithRunStartWrappers('maui-launch-json-workspace-folder-mode', async () => {
                assert.deepStrictEqual(updates, [{
                    key: 'maui.configuration.useLaunchJsonConfigurations',
                    value: true,
                    target: vscode.ConfigurationTarget.WorkspaceFolder
                }]);
                assert.strictEqual(executeCommandStub.notCalled, true);
            });

            assert.deepStrictEqual(updates, [
                {
                    key: 'maui.configuration.useLaunchJsonConfigurations',
                    value: true,
                    target: vscode.ConfigurationTarget.WorkspaceFolder
                },
                {
                    key: 'maui.configuration.useLaunchJsonConfigurations',
                    value: false,
                    target: vscode.ConfigurationTarget.WorkspaceFolder
                }
            ]);
        } finally {
            cleanupRun('maui-launch-json-workspace-folder-mode');
            getConfigurationStub.restore();
            getCommandsStub.restore();
            executeCommandStub.restore();
        }
    });

    test('sets the MAUI active target before starting an iOS simulator run', async () => {
        const debugTargetsManager = {
            selectedTargets: [] as Array<{ device: string; platform: string }>,
            async setActiveDebugTarget(device: string, platform: string): Promise<{ id: string }> {
                this.selectedTargets.push({ device, platform });

                return { id: device };
            }
        };
        installMauiExtensionStub({
            maui: {
                debugTargetsManager
            }
        });
        installMauiRunStartWrapperStubs();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            {
                type: 'maui',
                project_path: '/workspace/MauiApp/MauiApp.csproj',
                target_framework: 'net10.0-ios',
                platform: 'ios',
                target_kind: 'simulator',
                device: 'ios-simulator-udid'
            } as ExecutableLaunchConfiguration,
            ['run', '-f', 'net10.0-ios'],
            [],
            { debug: true, runId: 'maui-ios-active-target', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            createDebugConfig('maui-ios-active-target'));

        try {
            await runWithRunStartWrappers('maui-ios-active-target', async () => {
                assert.deepStrictEqual(debugTargetsManager.selectedTargets, [{
                    device: 'ios-simulator-udid',
                    platform: 'ios'
                }]);
            });
        } finally {
            cleanupRun('maui-ios-active-target');
        }
    });

    test('sets the MAUI active target before starting a Mac Catalyst run', async () => {
        const debugTargetsManager = {
            selectedTargets: [] as Array<{ device: string; platform: string }>,
            async setActiveDebugTarget(device: string, platform: string): Promise<{ id: string }> {
                this.selectedTargets.push({ device, platform });

                return { id: device };
            }
        };
        installMauiExtensionStub({
            maui: {
                debugTargetsManager
            }
        });
        installMauiRunStartWrapperStubs();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            {
                type: 'maui',
                project_path: '/workspace/MauiApp/MauiApp.csproj',
                target_framework: 'net10.0-maccatalyst',
                platform: 'maccatalyst',
                target_kind: 'device',
                device: 'my-mac'
            } as ExecutableLaunchConfiguration,
            ['run', '-f', 'net10.0-maccatalyst'],
            [],
            { debug: true, runId: 'maui-maccatalyst-active-target', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            createDebugConfig('maui-maccatalyst-active-target'));

        try {
            await runWithRunStartWrappers('maui-maccatalyst-active-target', async () => {
                assert.deepStrictEqual(debugTargetsManager.selectedTargets, [{
                    device: 'my-mac',
                    platform: 'maccatalyst'
                }]);
            });
        } finally {
            cleanupRun('maui-maccatalyst-active-target');
        }
    });

    test('scrubs Aspire infrastructure variables from the MAUI adapter launch environment', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        process.env.ASPIRE_EXTENSION_ENDPOINT = 'localhost:1234';
        process.env.ASPIRE_EXTENSION_TOKEN = 'secret-token';
        process.env.ASPIRE_CLI_PACKAGES = '/tmp/packages';
        process.env.ASPIRE_TERMINAL_HOST_PATH = '/tmp/terminalhost';
        const getConfigurationStub = sinon.stub(vscode.workspace, 'getConfiguration').returns({
            get: (key: string, defaultValue?: unknown) => key === 'maui.configuration.useLaunchJsonConfigurations' ? true : defaultValue,
            inspect: (key: string) => key === 'maui.configuration.useLaunchJsonConfigurations' ? { globalValue: true } : undefined,
            update: async () => { },
        } as unknown as vscode.WorkspaceConfiguration);
        const getCommandsStub = sinon.stub(vscode.commands, 'getCommands').resolves(['vscode-maui.mauiStartDebugSession']);
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand').resolves();

        try {
            await debuggerExtension.createDebugSessionConfigurationCallback!(
                {
                    type: 'maui',
                    project_path: '/workspace/MauiApp/MauiApp.csproj',
                    target_framework: 'net10.0-maccatalyst',
                    platform: 'maccatalyst',
                    target_kind: 'device',
                    device: 'my-mac'
                } as ExecutableLaunchConfiguration,
                ['run', '-f', 'net10.0-maccatalyst'],
                [],
                { debug: true, runId: 'maui-process-env-scrub', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig('maui-process-env-scrub'));

            await runWithRunStartWrappers('maui-process-env-scrub', async () => {
                assert.strictEqual(process.env.ASPIRE_EXTENSION_ENDPOINT, undefined);
                assert.strictEqual(process.env.ASPIRE_EXTENSION_TOKEN, undefined);
                assert.strictEqual(process.env.ASPIRE_CLI_PACKAGES, undefined);
                assert.strictEqual(process.env.ASPIRE_TERMINAL_HOST_PATH, undefined);
            });

            assert.strictEqual(process.env.ASPIRE_EXTENSION_ENDPOINT, 'localhost:1234');
            assert.strictEqual(process.env.ASPIRE_EXTENSION_TOKEN, 'secret-token');
            assert.strictEqual(process.env.ASPIRE_CLI_PACKAGES, '/tmp/packages');
            assert.strictEqual(process.env.ASPIRE_TERMINAL_HOST_PATH, '/tmp/terminalhost');
        } finally {
            delete process.env.ASPIRE_EXTENSION_ENDPOINT;
            delete process.env.ASPIRE_EXTENSION_TOKEN;
            delete process.env.ASPIRE_CLI_PACKAGES;
            delete process.env.ASPIRE_TERMINAL_HOST_PATH;
            cleanupRun('maui-process-env-scrub');
            getConfigurationStub.restore();
            getCommandsStub.restore();
            executeCommandStub.restore();
        }
    });

    test('times out MAUI commands that leave a Quick Pick open', async () => {
        const executeCommandStub = sinon.stub(vscode.commands, 'executeCommand');
        executeCommandStub.withArgs('vscode-maui.pickStartUpProject', vscode.Uri.file('/workspace/MauiApp/MauiApp.csproj'))
            .returns(new Promise(() => undefined) as Thenable<unknown>);
        executeCommandStub.withArgs('workbench.action.closeQuickOpen').resolves();

        const result = await executeMauiCommandWithTimeout(
            'vscode-maui.pickStartUpProject',
            1,
            vscode.Uri.file('/workspace/MauiApp/MauiApp.csproj'));

        assert.deepStrictEqual(result, { timedOut: true });
        assert.strictEqual(executeCommandStub.calledWith('workbench.action.closeQuickOpen'), true);
    });

    test('configures MAUI run without debugger using MAUI skipDebug flag', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            target_kind: 'device',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-maccatalyst'],
            [],
            { debug: false, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.noDebug, true);
        assert.strictEqual(debugConfig.skipDebug, true);
    });

    test('overlays Mac Catalyst environment values without losing equals signs', async () => {
        installMauiExtensionStub();
        installMauiRunStartWrapperStubs();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            target_kind: 'device',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig('mac-env-overlay');
        const previousValue = process.env.ASPIRE_MAUI_EQUALS_TEST;
        process.env.ASPIRE_MAUI_EQUALS_TEST = 'previous';

        try {
            await debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-maccatalyst'],
                [{ name: 'ASPIRE_MAUI_EQUALS_TEST', value: 'service.instance.id=abc123' }],
                { debug: true, runId: 'mac-env-overlay', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                debugConfig);

            assert.strictEqual(process.env.ASPIRE_MAUI_EQUALS_TEST, 'previous');
            assert.strictEqual(getEnvironmentWithoutE2EBridgeVariables().ASPIRE_MAUI_EQUALS_TEST, 'previous');
            assert.strictEqual(debugConfig.environmentVariables, undefined);

            await runWithRunStartWrappers('mac-env-overlay', async () => {
                assert.strictEqual(process.env.ASPIRE_MAUI_EQUALS_TEST, 'service.instance.id=abc123');
                assert.strictEqual(getEnvironmentWithoutE2EBridgeVariables().ASPIRE_MAUI_EQUALS_TEST, undefined);
            });

            assert.strictEqual(process.env.ASPIRE_MAUI_EQUALS_TEST, 'previous');
            assert.strictEqual(getEnvironmentWithoutE2EBridgeVariables().ASPIRE_MAUI_EQUALS_TEST, 'previous');
        } finally {
            cleanupRun('mac-env-overlay');
            if (previousValue === undefined) {
                delete process.env.ASPIRE_MAUI_EQUALS_TEST;
            } else {
                process.env.ASPIRE_MAUI_EQUALS_TEST = previousValue;
            }
        }
    });

    test('keeps overlapping Mac Catalyst environment overlays isolated until each run cleans up', async () => {
        installMauiExtensionStub();
        installMauiRunStartWrapperStubs();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            target_kind: 'device',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;
        const previousValue = process.env.ASPIRE_MAUI_OVERLAP_TEST;
        delete process.env.ASPIRE_MAUI_OVERLAP_TEST;

        try {
            await debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-maccatalyst'],
                [{ name: 'ASPIRE_MAUI_OVERLAP_TEST', value: 'first=value' }],
                { debug: true, runId: 'mac-env-overlay-a', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig('mac-env-overlay-a'));
            assert.strictEqual(process.env.ASPIRE_MAUI_OVERLAP_TEST, undefined);
            assert.strictEqual(getEnvironmentWithoutE2EBridgeVariables().ASPIRE_MAUI_OVERLAP_TEST, undefined);

            await debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-maccatalyst'],
                [{ name: 'ASPIRE_MAUI_OVERLAP_TEST', value: 'second=value' }],
                { debug: true, runId: 'mac-env-overlay-b', debugSessionId: '2', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig('mac-env-overlay-b'));
            assert.strictEqual(process.env.ASPIRE_MAUI_OVERLAP_TEST, undefined);
            assert.strictEqual(getEnvironmentWithoutE2EBridgeVariables().ASPIRE_MAUI_OVERLAP_TEST, undefined);

            let releaseFirstLaunch: () => void = () => { };
            let secondLaunchStarted = false;
            let firstLaunch: Promise<void>;
            const firstLaunchEntered = new Promise<void>(resolve => {
                firstLaunch = runWithRunStartWrappers('mac-env-overlay-a', async () => {
                    assert.strictEqual(process.env.ASPIRE_MAUI_OVERLAP_TEST, 'first=value');
                    assert.strictEqual(getEnvironmentWithoutE2EBridgeVariables().ASPIRE_MAUI_OVERLAP_TEST, undefined);
                    resolve();
                    await new Promise<void>(release => {
                        releaseFirstLaunch = release;
                    });
                });
            });

            await firstLaunchEntered;
            const secondLaunch = runWithRunStartWrappers('mac-env-overlay-b', async () => {
                secondLaunchStarted = true;
                assert.strictEqual(process.env.ASPIRE_MAUI_OVERLAP_TEST, 'second=value');
                assert.strictEqual(getEnvironmentWithoutE2EBridgeVariables().ASPIRE_MAUI_OVERLAP_TEST, undefined);
            });
            let nonOverlayLaunchStarted = false;
            const nonOverlayLaunch = runWithRunStartWrappers('non-overlay-run', async () => {
                nonOverlayLaunchStarted = true;
            });

            await delay(10);
            assert.strictEqual(secondLaunchStarted, false);
            assert.strictEqual(nonOverlayLaunchStarted, true);
            assert.strictEqual(process.env.ASPIRE_MAUI_OVERLAP_TEST, 'first=value');
            releaseFirstLaunch();
            await firstLaunch!;
            await secondLaunch;
            await nonOverlayLaunch;
            assert.strictEqual(process.env.ASPIRE_MAUI_OVERLAP_TEST, undefined);
            assert.strictEqual(getEnvironmentWithoutE2EBridgeVariables().ASPIRE_MAUI_OVERLAP_TEST, undefined);
        } finally {
            cleanupRun('mac-env-overlay-a');
            cleanupRun('mac-env-overlay-b');
            if (previousValue === undefined) {
                delete process.env.ASPIRE_MAUI_OVERLAP_TEST;
            } else {
                process.env.ASPIRE_MAUI_OVERLAP_TEST = previousValue;
            }
        }
    });

    test('does not serialize resource starts that have no MAUI run-start wrappers', async () => {
        installMauiExtensionStub();
        installMauiRunStartWrapperStubs();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            target_kind: 'device',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;

        try {
            await debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-maccatalyst'],
                [{ name: 'ASPIRE_MAUI_SERIALIZATION_TEST', value: 'overlay=value' }],
                { debug: true, runId: 'wrapped-maui-run', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig('wrapped-maui-run'));

            let releaseWrappedRun: () => void = () => { };
            const wrappedRunEntered = new Promise<void>(resolve => {
                void runWithRunStartWrappers('wrapped-maui-run', async () => {
                    resolve();
                    await new Promise<void>(release => {
                        releaseWrappedRun = release;
                    });
                });
            });

            await wrappedRunEntered;

            let unwrappedRunStarted = false;
            await runWithRunStartWrappers('plain-dotnet-run', async () => {
                unwrappedRunStarted = true;
            });

            assert.strictEqual(unwrappedRunStarted, true);
            releaseWrappedRun();
        } finally {
            cleanupRun('wrapped-maui-run');
        }
    });

    test('waits to snapshot other resource environments until Mac Catalyst overlay start finishes', async () => {
        installMauiExtensionStub();
        installMauiRunStartWrapperStubs();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            target_kind: 'device',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;
        const previousValue = process.env.ASPIRE_MAUI_SNAPSHOT_TEST;
        process.env.ASPIRE_MAUI_SNAPSHOT_TEST = 'original';

        try {
            await debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-maccatalyst'],
                [{ name: 'ASPIRE_MAUI_SNAPSHOT_TEST', value: 'overlay=value' }],
                { debug: true, runId: 'mac-env-overlay-snapshot', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig('mac-env-overlay-snapshot'));

            let releaseOverlayStart: () => void = () => { };
            let overlayStart: Promise<void>;
            const overlayStartEntered = new Promise<void>(resolve => {
                overlayStart = runWithRunStartWrappers('mac-env-overlay-snapshot', async () => {
                    assert.strictEqual(process.env.ASPIRE_MAUI_SNAPSHOT_TEST, 'overlay=value');
                    resolve();
                    await new Promise<void>(release => {
                        releaseOverlayStart = release;
                    });
                });
            });

            await overlayStartEntered;
            let configCreated = false;
            const otherConfigPromise = createDebugSessionConfiguration(
                { type: 'aspire', request: 'launch', name: 'Aspire', program: '' },
                { type: 'project', project_path: '/workspace/Api/Api.csproj' } as ExecutableLaunchConfiguration,
                [],
                [],
                { debug: true, runId: 'other-run', debugSessionId: '2', isApphost: false, debugSession: fakeAspireDebugSession },
                {
                    resourceType: 'project',
                    debugAdapter: 'coreclr',
                    extensionId: null,
                    getDisplayName: () => 'Api',
                    getProjectFile: () => '/workspace/Api/Api.csproj',
                    getSupportedFileTypes: () => ['.csproj'],
                }).then(config => {
                    configCreated = true;
                    return config;
                });

            await delay(10);
            assert.strictEqual(configCreated, false);
            releaseOverlayStart();
            await overlayStart!;
            const otherConfig = await otherConfigPromise;
            assert.strictEqual(otherConfig.env?.ASPIRE_MAUI_SNAPSHOT_TEST, 'original');
        } finally {
            cleanupRun('mac-env-overlay-snapshot');
            cleanupRun('other-run');
            if (previousValue === undefined) {
                delete process.env.ASPIRE_MAUI_SNAPSHOT_TEST;
            } else {
                process.env.ASPIRE_MAUI_SNAPSHOT_TEST = previousValue;
            }
        }
    });

    test('rejects non-Mac Catalyst environment values that cannot be serialized', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-android',
            platform: 'android',
            target_kind: 'device',
            device: 'physical-device'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await assert.rejects(
            () => debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-android'],
                [{ name: 'ASPIRE_MAUI_MULTILINE_TEST', value: 'line1\nline2' }],
                { debug: true, runId: 'android-env-newline', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                debugConfig),
            /contains a newline/);

        assert.strictEqual(debugConfig.environmentVariables, undefined);
    });

    test('derives MAUI platform from target framework for older AppHosts', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-ios'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-ios'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.platform, 'ios');
    });

    test('does not parse application arguments after dotnet run separator as build settings', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            target_kind: 'device',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-maccatalyst', '--', '--configuration', 'Release', '-p:OpenArguments=-W'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.configuration, 'Debug');
        assert.strictEqual(debugConfig.msbuildProperties, undefined);
    });

    test('preserves dotnet run configuration switches', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;
        const releaseDebugConfig = createDebugConfig();
        const customDebugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-maccatalyst', '--configuration', 'Release'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            releaseDebugConfig);

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-maccatalyst', '-c=Custom'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            customDebugConfig);

        assert.strictEqual(releaseDebugConfig.configuration, 'Release');
        assert.strictEqual(customDebugConfig.configuration, 'Custom');
    });

    test('preserves MSBuild configuration properties', async () => {
        installMauiExtensionStub();

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-maccatalyst',
            platform: 'maccatalyst',
            device: 'my-mac'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-maccatalyst', '-p:Configuration=Release'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.configuration, 'Release');
        assertMsBuildProperties(debugConfig, {
            Configuration: 'Release',
        });
        assert.deepStrictEqual(getLoggableDebugConfiguration(debugConfig, false).msbuildProperties, {
            Configuration: 'Release',
        });
    });

    test('resolves default Android device selectors when a matching target is available', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'physical-device',
                platform: 'android',
                platforms: ['android'],
                isEmulator: false,
                isRunning: true,
                name: 'Pixel Device'
            },
            {
                identifier: 'emulator-5554',
                platform: 'android',
                platforms: ['android'],
                isEmulator: true,
                isRunning: true,
                name: 'Pixel Emulator'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-android',
            platform: 'android',
            target_kind: 'device'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-android', '-p:AdbTarget=-d'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, 'physical-device');
        assert.strictEqual(debugConfig.debugTarget, undefined);
        assertMsBuildProperties(debugConfig, {
            AdbTarget: '-s physical-device',
        });
    });

    test('uses structured Android launch metadata while preserving generated fallback MSBuild properties', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'physical-device',
                platform: 'android',
                platforms: ['android'],
                isEmulator: false,
                isRunning: true,
                name: 'Pixel Device'
            },
            {
                identifier: 'emulator-5554',
                platform: 'android',
                platforms: ['android'],
                isEmulator: true,
                isRunning: true,
                name: 'Pixel Emulator'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-android',
            platform: 'android',
            target_kind: 'device',
            msbuild_properties: {
                AdbTarget: '-d'
            }
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-android', '-p:AdbTarget=-e', '-p:CustomAfterMicrosoftCommonTargets=/tmp/maui-env.targets'],
            [],
            { debug: true, runId: 'structured-android-device', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, 'physical-device');
        assert.strictEqual(debugConfig.debugTarget, undefined);
        assertMsBuildProperties(debugConfig, {
            AdbTarget: '-s physical-device',
            CustomAfterMicrosoftCommonTargets: '/tmp/maui-env.targets',
        });
    });

    test('reports default Android emulator selector failures before MAUI can prompt', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => []);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-android',
            platform: 'android',
            target_kind: 'emulator',
            msbuild_properties: {
                AdbTarget: '-e'
            }
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await assert.rejects(
            debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-android', '-p:AdbTarget=-e'],
                [],
                { debug: true, runId: 'structured-android-emulator', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                debugConfig),
            /Unable to resolve a default android emulator target/);
    });

    test('reports default Android physical device selector failures before MAUI can prompt', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'Pixel_9a',
                platform: 'android',
                platforms: ['android'],
                isEmulator: true,
                isRunning: false,
                name: 'Pixel 9a'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-android',
            platform: 'android',
            target_kind: 'device',
            msbuild_properties: {
                AdbTarget: '-d'
            }
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await assert.rejects(
            debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-android', '-p:AdbTarget=-d'],
                [],
                { debug: true, runId: 'structured-android-device', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                debugConfig),
            /Unable to resolve a default android device target/);
    });

    test('resolves default Android emulator selectors when a matching target is available', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'emulator-5554',
                platform: 'android',
                platforms: ['android'],
                isEmulator: true,
                isRunning: true,
                name: 'Pixel Emulator'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-android',
            platform: 'android',
            target_kind: 'emulator',
            msbuild_properties: {
                AdbTarget: '-e'
            }
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-android', '-p:AdbTarget=-e'],
            [],
            { debug: true, runId: 'structured-android-emulator', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, 'emulator-5554');
        assertMsBuildProperties(debugConfig, {
            AdbTarget: '-s emulator-5554',
        });
    });

    test('uses MAUI active target without stale AdbTarget for stopped Android emulators', async () => {
        const debugTargetsManager = {
            selectedTargets: [] as Array<{ device: string; platform: string }>,
            async setActiveDebugTarget(device: string, platform: string): Promise<{ id: string }> {
                this.selectedTargets.push({ device, platform });

                return { id: device };
            }
        };
        installMauiExtensionStub({
            maui: {
                debugTargetsManager
            }
        });
        installMauiRunStartWrapperStubs();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'Pixel_9a',
                platform: 'android',
                platforms: ['android'],
                isEmulator: true,
                isRunning: false,
                name: 'Pixel 9a'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-android',
            platform: 'android',
            target_kind: 'emulator',
            msbuild_properties: {
                AdbTarget: '-e'
            }
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig('stopped-android-emulator');

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-android', '-p:AdbTarget=-e'],
            [],
            { debug: true, runId: 'stopped-android-emulator', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, undefined);
        assertNoMsBuildProperties(debugConfig);

        try {
            await runWithRunStartWrappers('stopped-android-emulator', async () => {
                assert.deepStrictEqual(debugTargetsManager.selectedTargets, [{
                    device: 'Pixel_9a',
                    platform: 'android'
                }]);
            });
        } finally {
            cleanupRun('stopped-android-emulator');
        }
    });

    test('resolves explicit Android emulator names to MAUI target identifiers', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'emulator-5554',
                emulatorId: 'Pixel_5_API_33',
                platform: 'android',
                platforms: ['android'],
                isEmulator: true,
                isRunning: true,
                name: 'Pixel 5 API 33'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-android',
            platform: 'android',
            target_kind: 'emulator',
            device: 'Pixel_5_API_33',
            msbuild_properties: {
                AdbTarget: '-s Pixel_5_API_33'
            }
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-android'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, 'emulator-5554');
        assertMsBuildProperties(debugConfig, {
            AdbTarget: '-s emulator-5554',
        });
    });

    test('infers legacy iOS device targets from RuntimeIdentifier MSBuild properties', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'physical-ios-device',
                platform: 'ios',
                platforms: ['ios'],
                isEmulator: false,
                isRunning: true,
                name: 'iPhone Device'
            },
            {
                identifier: 'booted-simulator',
                platform: 'ios',
                platforms: ['ios'],
                isEmulator: true,
                isRunning: true,
                name: 'iPhone Simulator'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-ios',
            platform: 'ios',
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-ios', '-p:RuntimeIdentifier=ios-arm64'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.runtimeIdentifier, 'ios-arm64');
        assert.strictEqual(debugConfig.device, 'physical-ios-device');
        assert.strictEqual(debugConfig.isEmulator, false);
        assert.strictEqual(debugConfig.debugTarget, 'physical-ios-device');
        assertMsBuildProperties(debugConfig, {
            RuntimeIdentifier: 'ios-arm64',
            _DeviceName: 'physical-ios-device',
        });
    });

    test('resolves default iOS simulator targets to the running simulator', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'shutdown-simulator',
                platform: 'ios',
                platforms: ['ios'],
                isEmulator: true,
                isRunning: false,
                name: 'iPhone Shutdown'
            },
            {
                identifier: 'booted-simulator',
                platform: 'ios',
                platforms: ['ios'],
                isEmulator: true,
                isRunning: true,
                name: 'iPhone Booted'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-ios',
            platform: 'ios',
            target_kind: 'simulator'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-ios'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, 'booted-simulator');
        assert.strictEqual(debugConfig.runtimeIdentifier, process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64');
        assert.strictEqual(debugConfig.isEmulator, true);
        assert.strictEqual(debugConfig.debugTarget, 'booted-simulator');
        assertMsBuildProperties(debugConfig, {
            _DeviceName: ':v2:udid=booted-simulator',
            RuntimeIdentifier: process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64',
        });
    });

    test('resolves legacy iOS launch configs without target kind to the running simulator', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'booted-simulator',
                platform: 'ios',
                platforms: ['ios'],
                isEmulator: true,
                isRunning: true,
                name: 'iPhone Booted'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-ios',
            platform: 'ios'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-ios'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, 'booted-simulator');
        assert.strictEqual(debugConfig.runtimeIdentifier, process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64');
        assert.strictEqual(debugConfig.isEmulator, true);
        assert.strictEqual(debugConfig.debugTarget, 'booted-simulator');
        assertMsBuildProperties(debugConfig, {
            _DeviceName: ':v2:udid=booted-simulator',
            RuntimeIdentifier: process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64',
        });
    });

    test('resolves default iOS simulator targets to the first running simulator when multiple are running', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'booted-simulator-1',
                platform: 'ios',
                platforms: ['ios'],
                isEmulator: true,
                isRunning: true,
                name: 'iPhone Booted 1'
            },
            {
                identifier: 'booted-simulator-2',
                platform: 'ios',
                platforms: ['ios'],
                isEmulator: true,
                isRunning: true,
                name: 'iPhone Booted 2'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-ios',
            platform: 'ios',
            target_kind: 'simulator'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-ios'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, 'booted-simulator-1');
        assert.strictEqual(debugConfig.runtimeIdentifier, process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64');
        assertMsBuildProperties(debugConfig, {
            _DeviceName: ':v2:udid=booted-simulator-1',
            RuntimeIdentifier: process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64',
        });
    });

    test('resolves default iOS simulator targets to the first available simulator when none are running', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'shutdown-simulator-1',
                platform: 'ios',
                platforms: ['ios'],
                isEmulator: true,
                isRunning: false,
                name: 'iPhone Shutdown 1'
            },
            {
                identifier: 'shutdown-simulator-2',
                platform: 'ios',
                platforms: ['ios'],
                isEmulator: true,
                isRunning: false,
                name: 'iPhone Shutdown 2'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-ios',
            platform: 'ios',
            target_kind: 'simulator'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-ios'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, 'shutdown-simulator-1');
        assert.strictEqual(debugConfig.runtimeIdentifier, process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64');
        assertMsBuildProperties(debugConfig, {
            _DeviceName: ':v2:udid=shutdown-simulator-1',
            RuntimeIdentifier: process.arch === 'arm64' ? 'iossimulator-arm64' : 'iossimulator-x64',
        });
    });

    test('fails default mobile target resolution when multiple matching targets are available', async () => {
        installMauiExtensionStub();
        useMauiDeviceListProviderForTests(async () => [
            {
                identifier: 'emulator-5554',
                platform: 'android',
                platforms: ['android'],
                isEmulator: true,
                isRunning: true,
                name: 'Pixel 1'
            },
            {
                identifier: 'emulator-5556',
                platform: 'android',
                platforms: ['android'],
                isEmulator: true,
                isRunning: true,
                name: 'Pixel 2'
            }
        ]);

        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-android',
            platform: 'android',
            target_kind: 'emulator'
        } as ExecutableLaunchConfiguration;

        await assert.rejects(
            debuggerExtension.createDebugSessionConfigurationCallback!(
                launchConfig,
                ['run', '-f', 'net10.0-android'],
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                createDebugConfig()),
            /multiple targets are available/);
    });

    test('derives explicit device from legacy MSBuild selector arguments', async () => {
        installMauiExtensionStub();
        const debuggerExtension = getResourceDebuggerExtensions().find(extension => extension.resourceType === 'maui');
        assert.ok(debuggerExtension);

        const launchConfig = {
            type: 'maui',
            project_path: '/workspace/MauiApp/MauiApp.csproj',
            target_framework: 'net10.0-ios',
            platform: 'ios'
        } as ExecutableLaunchConfiguration;
        const debugConfig = createDebugConfig();

        await debuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['run', '-f', 'net10.0-ios', '-p:_DeviceName=:v2:udid=E25BBE37-69BA-4720-B6FD-D54C97791E79'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.device, 'E25BBE37-69BA-4720-B6FD-D54C97791E79');
    });
});

function installMauiExtensionStub(exports: unknown = undefined): void {
    sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
        return extensionId === 'ms-dotnettools.dotnet-maui'
            ? { id: extensionId, extensionPath: '/maui-extension', exports, activate: async () => exports } as unknown as vscode.Extension<unknown>
            : undefined;
    });
}

function installNoMauiExtensionStub(): void {
    sinon.stub(vscode.extensions, 'getExtension').returns(undefined);
}

function installMauiRunStartWrapperStubs(): void {
    sinon.stub(vscode.workspace, 'getConfiguration').returns({
        get: (key: string, defaultValue?: unknown) => key === 'maui.configuration.useLaunchJsonConfigurations' ? true : defaultValue,
        inspect: (key: string) => key === 'maui.configuration.useLaunchJsonConfigurations' ? { globalValue: true } : undefined,
        update: async () => { },
    } as unknown as vscode.WorkspaceConfiguration);
    sinon.stub(vscode.commands, 'getCommands').resolves(['vscode-maui.mauiStartDebugSession']);
}

function createDebugConfig(runId = '1'): AspireResourceExtendedDebugConfiguration {
    return {
        runId,
        debugSessionId: '1',
        type: 'maui',
        name: 'MAUI',
        request: 'launch',
        program: '/workspace/MauiApp/MauiApp.csproj',
        args: []
    };
}

function assertMsBuildProperties(debugConfig: AspireResourceExtendedDebugConfiguration, expected: Record<string, string>): void {
    assert.ok(debugConfig.msbuildProperties instanceof Map);
    assert.deepStrictEqual(Object.fromEntries(debugConfig.msbuildProperties), expected);
}

function assertNoMsBuildProperties(debugConfig: AspireResourceExtendedDebugConfiguration): void {
    assert.strictEqual(debugConfig.msbuildProperties, undefined);
}
