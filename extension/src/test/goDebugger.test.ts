import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import { getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { createGoResourceAttachProvider, goDebuggerExtension, goResourceAttachProvider } from '../debugger/languages/go';
import { extensionResourceAttachProviders } from '../debugger/resourceAttachProviders';
import type { ResourceAttachProvider, ResourceDebugResourceSnapshot } from '../debugger/resourceDebugContracts';
import { AspireResourceExtendedDebugConfiguration, GoLaunchConfiguration } from '../dcp/types';

interface GoProcessResolver {
    resolveApplicationPid(parentPid: number, cancellationToken?: vscode.CancellationToken): Promise<number>;
}

function createGoResource(overrides: Partial<ResourceDebugResourceSnapshot> = {}): ResourceDebugResourceSnapshot {
    return {
        name: 'api',
        displayName: 'API',
        resourceType: 'Executable',
        state: 'Running',
        properties: {
            'resource.launchConfigurationType': 'go',
            'executable.path': 'go',
            'executable.pid': 123,
        },
        ...overrides,
    };
}

function createGoAttachProvider(resolvedProcessId = 456): {
    provider: ResourceAttachProvider;
    resolver: GoProcessResolver & { parentPids: number[] };
} {
    const resolver: GoProcessResolver & { parentPids: number[] } = {
        parentPids: [],
        async resolveApplicationPid(parentPid: number): Promise<number> {
            this.parentPids.push(parentPid);
            return resolvedProcessId;
        },
    };

    return {
        provider: createGoResourceAttachProvider(resolver),
        resolver,
    };
}

suite('Go Debugger Extension Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    teardown(() => sinon.restore());

    test('exposes an attach provider independently from the Go launch provider', () => {
        assert.notStrictEqual(goResourceAttachProvider, goDebuggerExtension);
    });

    test('keeps Go after .NET in the extension resource attach provider registry', () => {
        assert.deepStrictEqual(extensionResourceAttachProviders.map(provider => provider.id), ['dotnet', 'go']);
    });

    test('recognizes only Go launch-configuration resources with Go executable metadata', () => {
        const { provider } = createGoAttachProvider();

        assert.strictEqual(provider.canRecognizeResource(createGoResource()), true);
        assert.strictEqual(provider.canRecognizeResource(createGoResource({
            properties: {
                'resource.launchConfigurationType': 'node',
                'executable.path': 'node',
                'executable.pid': 123,
            },
        })), false);
        assert.strictEqual(provider.canRecognizeResource(createGoResource({
            properties: {
                'resource.launchConfigurationType': 'go',
                'executable.path': 'python',
                'executable.pid': 123,
            },
        })), false);
        assert.strictEqual(provider.canRecognizeResource(createGoResource({
            properties: {
                'resource.launchConfigurationType': 'go',
                'executable.path': 'bun',
                'executable.pid': 123,
            },
        })), false);
        assert.strictEqual(provider.canRecognizeResource(createGoResource({
            properties: {
                'resource.launchConfigurationType': 'GO',
                'executable.path': 'go',
                'executable.pid': 123,
            },
        })), false);
    });

    test('accepts numeric and numeric-string Go parent process IDs', () => {
        const { provider } = createGoAttachProvider();

        assert.strictEqual(provider.canAttachToResource(createGoResource()), true);
        assert.strictEqual(provider.canAttachToResource(createGoResource({
            properties: {
                'resource.launchConfigurationType': 'go',
                'executable.path': 'go.exe',
                'executable.pid': '123',
            },
        })), true);
    });

    test('requires a running Go resource with a valid parent process ID', () => {
        const { provider } = createGoAttachProvider();

        assert.strictEqual(provider.canRecognizeResource(createGoResource({ state: 'Finished' })), true);
        assert.strictEqual(provider.canAttachToResource(createGoResource({ state: 'Finished' })), false);
        assert.strictEqual(provider.canAttachToResource(createGoResource({
            properties: {
                'resource.launchConfigurationType': 'go',
                'executable.path': 'go',
                'executable.pid': '12.5',
            },
        })), false);
        assert.strictEqual(provider.canAttachToResource(createGoResource({
            properties: {
                'resource.launchConfigurationType': 'go',
                'executable.path': 'go',
                'executable.pid': 0,
            },
        })), false);
    });

    test('creates the exact Go attach configuration for the resolved application process', async () => {
        const { provider, resolver } = createGoAttachProvider(456);

        const configuration = await provider.createDebugConfiguration(createGoResource({
            displayName: null,
            name: 'api',
        }));

        assert.deepStrictEqual(configuration, {
            type: 'go',
            request: 'attach',
            mode: 'local',
            debugAdapter: 'dlv-dap',
            name: 'Attach debugger: api',
            processId: 456,
        });
        assert.deepStrictEqual(resolver.parentPids, [123]);
    });

    test('advertises Go support when the Go extension is installed', () => {
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
            return extensionId === 'golang.go' ? { id: extensionId } as vscode.Extension<unknown> : undefined;
        });

        const capabilities = getSupportedCapabilities();
        assert.ok(capabilities.includes('go'));
        assert.ok(capabilities.includes('golang.go'));
        assert.ok(getResourceDebuggerExtensions().some(extension => extension.resourceType === 'go'));
    });

    test('configures VS Code Go debugger with dlv-dap', async () => {
        const launchConfig: GoLaunchConfiguration = {
            type: 'go',
            program: '/workspace/api/cmd/server',
            working_directory: '/workspace/api',
            build_flags: "-tags='integration' -gcflags='all=-N -l'"
        };
        const debugConfig = createDebugConfig();

        await goDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['--listen', ':8080'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.type, 'go');
        assert.strictEqual(debugConfig.request, 'launch');
        assert.strictEqual(debugConfig.mode, 'debug');
        assert.strictEqual(debugConfig.debugAdapter, 'dlv-dap');
        assert.strictEqual(debugConfig.program, '/workspace/api/cmd/server');
        assert.strictEqual(debugConfig.cwd, '/workspace/api');
        assert.strictEqual(debugConfig.buildFlags, "-tags='integration' -gcflags='all=-N -l'");
        assert.deepStrictEqual(debugConfig.args, ['--listen', ':8080']);
        assert.strictEqual(debugConfig.noDebug, false);
    });

    test('sets noDebug when launch option disables debugging', async () => {
        const launchConfig: GoLaunchConfiguration = {
            type: 'go',
            program: '/workspace/api',
            working_directory: '/workspace/api'
        };
        const debugConfig = createDebugConfig();

        await goDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: false, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.noDebug, true);
    });

    test('uses working directory as program when program is absent', async () => {
        const launchConfig: GoLaunchConfiguration = {
            type: 'go',
            working_directory: '/workspace/api'
        };
        const debugConfig = createDebugConfig();

        await goDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.program, '/workspace/api');
        assert.strictEqual(debugConfig.cwd, '/workspace/api');
    });
});

function createDebugConfig(): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'go',
        name: 'Go',
        request: 'launch',
        program: '/workspace/api',
        args: []
    };
}
