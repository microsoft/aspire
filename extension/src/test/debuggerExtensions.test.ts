import * as assert from 'assert';
import * as sinon from 'sinon';
import type { AspireDebugSession } from '../debugger/AspireDebugSession';
import { createDebugSessionConfiguration, ResourceDebuggerExtension } from '../debugger/debuggerExtensions';
import { ExecutableLaunchConfiguration } from '../dcp/types';
import {
    ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR,
    ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR,
    ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR,
    getAspireExtensionEnvironment,
} from '../utils/cliPathEnvironment';

suite('debuggerExtensions tests', () => {
    test('uses the active prerelease extension identity over AppHost debug environment overrides', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const extensionEnvironment = getAspireExtensionEnvironment({
            version: '1.17.0',
            preRelease: true,
        }, {
            appName: 'Visual Studio Code - Insiders',
            uriScheme: 'vscode-insiders',
        });
        assert.ok(extensionEnvironment);
        const debuggerExtension: ResourceDebuggerExtension = {
            resourceType: 'project',
            debugAdapter: 'coreclr',
            extensionId: null,
            getDisplayName: () => 'AppHost',
            getProjectFile: () => '/workspace/AppHost/AppHost.csproj',
            getSupportedFileTypes: () => ['.csproj'],
        };

        try {
            const configuration = await createDebugSessionConfiguration(
                {
                    type: 'aspire',
                    request: 'launch',
                    name: 'Aspire',
                    program: '/workspace/AppHost/AppHost.csproj',
                    debuggers: {
                        apphost: {
                            env: {
                                aspire_vscode_extension_version: 'debugger-version',
                                aspire_vscode_extension_channel: 'stable',
                                aspire_vscode_extension_source: 'other',
                                CALLER_SETTING: 'preserved',
                            },
                        },
                    },
                },
                {
                    type: 'project',
                    project_path: '/workspace/AppHost/AppHost.csproj',
                } as ExecutableLaunchConfiguration,
                [],
                [
                    { name: 'aspire_vscode_extension_version', value: 'cli-version' },
                    { name: 'aspire_vscode_extension_channel', value: 'stable' },
                    { name: 'aspire_vscode_extension_source', value: 'other' },
                ],
                {
                    debug: true,
                    runId: 'apphost-run',
                    debugSessionId: 'aspire-session',
                    isApphost: true,
                    debugSession: { aspireExtensionEnvironment: extensionEnvironment } as AspireDebugSession,
                },
                debuggerExtension);

            assert.strictEqual(configuration.env?.[ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR], '1.17.0');
            assert.strictEqual(configuration.env?.[ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR], 'prerelease');
            assert.strictEqual(configuration.env?.[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR], 'microsoft-marketplace');
            assert.strictEqual(configuration.env?.aspire_vscode_extension_version, undefined);
            assert.strictEqual(configuration.env?.aspire_vscode_extension_channel, undefined);
            assert.strictEqual(configuration.env?.aspire_vscode_extension_source, undefined);
            assert.strictEqual(configuration.env?.CALLER_SETTING, 'preserved');
        }
        finally {
            platformStub.restore();
        }
    });

    test('reapplies the active extension identity after the AppHost debugger callback replaces the environment', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const extensionEnvironment = getAspireExtensionEnvironment({
            version: '1.17.0',
            preRelease: true,
        }, {
            appName: 'Visual Studio Code - Insiders',
            uriScheme: 'vscode-insiders',
        });
        assert.ok(extensionEnvironment);
        const debuggerExtension: ResourceDebuggerExtension = {
            resourceType: 'project',
            debugAdapter: 'coreclr',
            extensionId: null,
            getDisplayName: () => 'AppHost',
            getProjectFile: () => '/workspace/AppHost/AppHost.csproj',
            getSupportedFileTypes: () => ['.csproj'],
            createDebugSessionConfigurationCallback: async (_launchConfig, _args, _env, _launchOptions, configuration) => {
                configuration.env = {
                    aspire_vscode_extension_version: 'callback-version',
                    aspire_vscode_extension_channel: 'stable',
                    aspire_vscode_extension_source: 'other',
                    CALLBACK_SETTING: 'preserved',
                };
            },
        };

        try {
            const configuration = await createDebugSessionConfiguration(
                {
                    type: 'aspire',
                    request: 'launch',
                    name: 'Aspire',
                    program: '/workspace/AppHost/AppHost.csproj',
                },
                {
                    type: 'project',
                    project_path: '/workspace/AppHost/AppHost.csproj',
                } as ExecutableLaunchConfiguration,
                [],
                [],
                {
                    debug: true,
                    runId: 'apphost-run',
                    debugSessionId: 'aspire-session',
                    isApphost: true,
                    debugSession: { aspireExtensionEnvironment: extensionEnvironment } as AspireDebugSession,
                },
                debuggerExtension);

            assert.deepStrictEqual(
                Object.keys(configuration.env ?? {})
                    .filter(name => name.toLowerCase().startsWith('aspire_vscode_extension_'))
                    .sort(),
                [
                    ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR,
                    ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR,
                    ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR,
                ].sort());
            assert.strictEqual(configuration.env?.[ASPIRE_VSCODE_EXTENSION_VERSION_ENV_VAR], '1.17.0');
            assert.strictEqual(configuration.env?.[ASPIRE_VSCODE_EXTENSION_CHANNEL_ENV_VAR], 'prerelease');
            assert.strictEqual(configuration.env?.[ASPIRE_VSCODE_EXTENSION_SOURCE_ENV_VAR], 'microsoft-marketplace');
            assert.strictEqual(configuration.env?.CALLBACK_SETTING, 'preserved');
        }
        finally {
            platformStub.restore();
        }
    });
});
