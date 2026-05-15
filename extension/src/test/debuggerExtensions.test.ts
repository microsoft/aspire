import * as assert from 'assert';
import * as sinon from 'sinon';
import { createDebugSessionConfiguration, ResourceDebuggerExtension } from '../debugger/debuggerExtensions';
import { AspireExtendedDebugConfiguration, ProjectLaunchConfiguration } from '../dcp/types';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import * as io from '../utils/io';

suite('Debugger Extensions Tests', () => {
    teardown(() => sinon.restore());

    function createProjectDebuggerExtension(): ResourceDebuggerExtension {
        return {
            resourceType: 'project',
            debugAdapter: 'coreclr',
            extensionId: null,
            getDisplayName: () => 'Test Project',
            getProjectFile: (launchConfig) => (launchConfig as ProjectLaunchConfiguration).project_path,
            getSupportedFileTypes: () => ['.csproj']
        };
    }

    function createLaunchOptions() {
        return {
            debug: true,
            runId: 'run-id',
            debugSessionId: 'debug-session-id',
            isApphost: false,
            debugSession: {} as AspireDebugSession
        };
    }

    test('applies launch configuration serverReadyAction to the debug configuration', async () => {
        sinon.stub(io, 'isDirectory').resolves(false);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/workspace/TestProject/TestProject.csproj',
            serverReadyAction: {
                action: 'openExternally',
                pattern: '\\bNow listening on:\\s+(https?://\\S+)',
                uriFormat: 'https://localhost:5001'
            }
        };

        const debugSessionConfig: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/AppHost/AppHost.csproj'
        };

        const result = await createDebugSessionConfiguration(
            debugSessionConfig,
            launchConfig,
            undefined,
            [],
            createLaunchOptions(),
            createProjectDebuggerExtension()
        );

        assert.deepStrictEqual(result.serverReadyAction, launchConfig.serverReadyAction);
    });

    test('launch configuration debugger overrides apphost serverReadyAction', async () => {
        sinon.stub(io, 'isDirectory').resolves(false);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/workspace/TestProject/TestProject.csproj',
            serverReadyAction: {
                action: 'openExternally',
                pattern: 'from-apphost',
                uriFormat: 'https://localhost:5001'
            }
        };

        const debugSessionConfig: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            request: 'launch',
            name: 'Aspire',
            program: '/workspace/AppHost/AppHost.csproj',
            debuggers: {
                project: {
                    serverReadyAction: {
                        action: 'openExternally',
                        pattern: 'from-launch-json',
                        uriFormat: 'https://localhost:7001'
                    }
                }
            }
        };

        const result = await createDebugSessionConfiguration(
            debugSessionConfig,
            launchConfig,
            undefined,
            [],
            createLaunchOptions(),
            createProjectDebuggerExtension()
        );

        assert.deepStrictEqual(result.serverReadyAction, {
            action: 'openExternally',
            pattern: 'from-launch-json',
            uriFormat: 'https://localhost:7001'
        });
    });
});
