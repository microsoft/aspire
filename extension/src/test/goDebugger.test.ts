import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession } from '../debugger/AspireDebugSession';
import * as debuggerExtensionsModule from '../debugger/debuggerExtensions';
import { getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { createGoResourceAttachProvider, goDebuggerExtension, goResourceAttachProvider, parseGoAppHostCommand } from '../debugger/languages/go';
import { extensionResourceAttachProviders } from '../debugger/resourceAttachProviders';
import type { ResourceAttachProvider, ResourceDebugResourceSnapshot } from '../debugger/resourceDebugContracts';
import { AspireExtendedDebugConfiguration, AspireResourceExtendedDebugConfiguration, EnvVar, GoLaunchConfiguration } from '../dcp/types';
import { goAppHostCommandNotRecognized, goDebuggerExtensionNotInstalled } from '../loc/strings';

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
        assert.ok(capabilities.includes('go-apphost.v1'));
        assert.ok(getResourceDebuggerExtensions().some(extension => extension.resourceType === 'go'));
    });

    test('does not advertise Go support when the Go extension is missing', () => {
        sinon.stub(vscode.extensions, 'getExtension').returns(undefined);

        const capabilities = getSupportedCapabilities();
        assert.ok(!capabilities.includes('go'));
        assert.ok(!capabilities.includes('golang.go'));
        // The CLI only hands apphost.go to the extension when this is advertised, so it has to disappear
        // with the Go extension or the CLI would ask for a debugger that is not there.
        assert.ok(!capabilities.includes('go-apphost.v1'));
        assert.ok(!getResourceDebuggerExtensions().some(extension => extension.resourceType === 'go'));
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

suite('Go AppHost Command Parsing Tests', () => {
    // The CLI starts every Go AppHost as `go run .`, so the extension can recover the package to debug
    // from the command line instead of re-deriving the module layout.
    test('parses the go run command the CLI sends', () => {
        assert.deepStrictEqual(parseGoAppHostCommand(['go', 'run', '.']), { packagePath: '.', appHostArgs: [] });
    });

    test('accepts a toolchain referenced by an absolute path', () => {
        for (const executable of ['/usr/local/go/bin/go', 'C:\\Program Files\\Go\\bin\\go.exe']) {
            assert.deepStrictEqual(
                parseGoAppHostCommand([executable, 'run', '.']),
                { packagePath: '.', appHostArgs: [] },
                executable);
        }
    });

    test('matches the toolchain name regardless of case', () => {
        // Windows reports the executable with whatever casing the file system stored.
        assert.deepStrictEqual(
            parseGoAppHostCommand(['C:\\Go\\bin\\GO.EXE', 'run', '.']),
            { packagePath: '.', appHostArgs: [] });
    });

    test('accepts the shim extensions a Windows version manager can resolve go to', () => {
        // The CLI looks go up through PATH/PATHEXT, so the first match can be a .cmd or .bat shim.
        for (const executable of ['C:\\Users\\dev\\shims\\go.cmd', 'C:\\Users\\dev\\shims\\GO.BAT']) {
            assert.deepStrictEqual(
                parseGoAppHostCommand([executable, 'run', '.']),
                { packagePath: '.', appHostArgs: [] },
                executable);
        }
    });

    test('keeps every argument after the package for the AppHost', () => {
        // Anything after the package belongs to the program, including values that look like go flags.
        assert.deepStrictEqual(
            parseGoAppHostCommand(['go', 'run', '.', '--operation', 'run', '-tags=ignored']),
            { packagePath: '.', appHostArgs: ['--operation', 'run', '-tags=ignored'] });
    });

    test('keeps a package path other than the current directory', () => {
        assert.strictEqual(parseGoAppHostCommand(['go', 'run', './cmd/apphost'])?.packagePath, './cmd/apphost');
    });

    test('keeps a relative package directory written with either path separator', () => {
        for (const packagePath of ['.\\cmd\\apphost', '../apphost', '..\\apphost', '..']) {
            assert.strictEqual(parseGoAppHostCommand(['go', 'run', packagePath])?.packagePath, packagePath, packagePath);
        }
    });

    test('does not re-split arguments that contain spaces', () => {
        // The CLI has already tokenized the command line, so a value with a space is still one argument.
        assert.deepStrictEqual(
            parseGoAppHostCommand(['C:\\Program Files\\Go\\bin\\go.exe', 'run', '.', '--name', 'my app']),
            { packagePath: '.', appHostArgs: ['--name', 'my app'] });
    });

    test('returns null when go run flags precede the package', () => {
        // The launch configuration has no place for them on this path, and the flag would otherwise be
        // taken as the program to debug.
        assert.strictEqual(parseGoAppHostCommand(['go', 'run', '-tags=dev', '.']), null);
        assert.strictEqual(parseGoAppHostCommand(['go', 'run', '-race', '.']), null);
    });

    test('returns null when the package is not a relative directory', () => {
        // The caller resolves the package against the AppHost directory, so an import path would silently
        // become a directory under it.
        assert.strictEqual(parseGoAppHostCommand(['go', 'run', 'example.com/apphost']), null);
        assert.strictEqual(parseGoAppHostCommand(['go', 'run', 'cmd/apphost']), null);
        assert.strictEqual(parseGoAppHostCommand(['go', 'run', '.hidden']), null);
    });

    test('returns null when the command is not go run', () => {
        assert.strictEqual(parseGoAppHostCommand(['go', 'build', '.']), null);
        assert.strictEqual(parseGoAppHostCommand(['go', 'test', '.']), null);
        assert.strictEqual(parseGoAppHostCommand(['go', 'tool', 'dlv']), null);
    });

    test('returns null when the executable is not the go toolchain', () => {
        assert.strictEqual(parseGoAppHostCommand(['dotnet', 'run', '.']), null);
        assert.strictEqual(parseGoAppHostCommand(['gofmt', 'run', '.']), null);
        // The shim extensions are only forgiven for go itself.
        assert.strictEqual(parseGoAppHostCommand(['C:\\tools\\air.cmd', 'run', '.']), null);
        // The name is read from the last path segment, not from a directory on the way to the executable.
        assert.strictEqual(parseGoAppHostCommand(['/usr/local/go/bin/air', 'run', '.']), null);
    });

    test('returns null when the command is too short to name a package', () => {
        assert.strictEqual(parseGoAppHostCommand([]), null);
        assert.strictEqual(parseGoAppHostCommand(['go']), null);
        assert.strictEqual(parseGoAppHostCommand(['go', 'run']), null);
    });
});

suite('Go AppHost Launch Tests', () => {
    const appHostPath = '/workspace/apphost.go';

    teardown(() => sinon.restore());

    function createAppHostDebugSession(configuration: Partial<AspireExtendedDebugConfiguration> = {}): AspireDebugSession {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: appHostPath,
                command: 'run',
                ...configuration
            },
        };

        return new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { });
    }

    test('stops a Go AppHost launch with install guidance when the Go extension is not installed', async () => {
        // startAppHost builds the debugger descriptor directly instead of going through
        // getResourceDebuggerExtensions, so without its own gate VS Code fails the session with the raw
        // "configured debug type is not supported" rather than naming the extension to install.
        stubInstalledExtensions([]);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration');
        const debugSession = createAppHostDebugSession();
        sinon.stub(debugSession, 'createDebugAdapterTrackerCore');

        await debugSession.startAppHost(appHostPath, ['go', 'run', '.'], [], true, { forceBuild: false });

        sinon.assert.notCalled(createDebugSessionConfiguration);
        sinon.assert.calledOnce(showErrorMessage);
        assert.strictEqual(showErrorMessage.firstCall.args[0], goDebuggerExtensionNotInstalled(goDebuggerExtension.extensionId!));
    });

    test('stops a Go AppHost launch when the command is not go run with a package', async () => {
        // Debugging the wrong program would be the alternative to failing loudly.
        stubInstalledExtensions(['golang.go']);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration');
        const debugSession = createAppHostDebugSession();
        const trackerStub = sinon.stub(debugSession, 'createDebugAdapterTrackerCore');

        await debugSession.startAppHost(appHostPath, ['go', 'run', '-race', '.'], [], true, { forceBuild: false });

        sinon.assert.notCalled(trackerStub);
        sinon.assert.notCalled(createDebugSessionConfiguration);
        sinon.assert.calledOnce(showErrorMessage);
        assert.strictEqual(showErrorMessage.firstCall.args[0], goAppHostCommandNotRecognized());
    });

    test('debugs the AppHost package with Delve and forwards the AppHost server environment', async () => {
        stubInstalledExtensions(['golang.go']);
        const debugSession = createAppHostDebugSession();
        sinon.stub(debugSession, 'createDebugAdapterTrackerCore');
        const startAndGetDebugSession = sinon.stub(debugSession, 'startAndGetDebugSession').resolves(undefined);
        const environment: EnvVar[] = [
            { name: 'REMOTE_APP_HOST_SOCKET_PATH', value: '/tmp/aspire-remote.sock' },
            { name: 'ASPIRE_APPHOST_FILEPATH', value: appHostPath }
        ];

        await debugSession.startAppHost(
            appHostPath,
            ['/usr/local/go/bin/go', 'run', '.', '--operation', 'run'],
            environment,
            true,
            { forceBuild: false });

        sinon.assert.calledOnce(startAndGetDebugSession);
        const debugConfig = startAndGetDebugSession.firstCall.args[0];
        assert.strictEqual(debugConfig.type, 'go');
        assert.strictEqual(debugConfig.request, 'launch');
        assert.strictEqual(debugConfig.mode, 'debug');
        assert.strictEqual(debugConfig.debugAdapter, 'dlv-dap');
        // `go run .` compiles every file in the package, so the program is the AppHost directory rather
        // than apphost.go, which would leave out its sibling files.
        assert.strictEqual(debugConfig.program, path.resolve('/workspace'));
        assert.strictEqual(debugConfig.cwd, '/workspace');
        assert.deepStrictEqual(debugConfig.args, ['--operation', 'run']);
        assert.strictEqual(debugConfig.noDebug, false);
        assert.strictEqual(debugConfig.isApphost, true);
        assert.strictEqual(debugConfig.resourceType, 'go');
        assert.strictEqual(debugConfig.env?.REMOTE_APP_HOST_SOCKET_PATH, '/tmp/aspire-remote.sock');
        assert.strictEqual(debugConfig.env?.ASPIRE_APPHOST_FILEPATH, appHostPath);
        assert.strictEqual(debugConfig.buildFlags, undefined);
    });

    test('starts the AppHost without debugging when debugging is off', async () => {
        stubInstalledExtensions(['golang.go']);
        const debugSession = createAppHostDebugSession();
        sinon.stub(debugSession, 'createDebugAdapterTrackerCore');
        const startAndGetDebugSession = sinon.stub(debugSession, 'startAndGetDebugSession').resolves(undefined);

        await debugSession.startAppHost(appHostPath, ['go', 'run', '.'], [], false, { forceBuild: false });

        const debugConfig = startAndGetDebugSession.firstCall.args[0];
        assert.strictEqual(debugConfig.type, 'go');
        assert.strictEqual(debugConfig.noDebug, true);
    });

    test('resolves a package path other than the current directory against the AppHost directory', async () => {
        stubInstalledExtensions(['golang.go']);
        const debugSession = createAppHostDebugSession();
        sinon.stub(debugSession, 'createDebugAdapterTrackerCore');
        const startAndGetDebugSession = sinon.stub(debugSession, 'startAndGetDebugSession').resolves(undefined);

        await debugSession.startAppHost(appHostPath, ['go', 'run', './cmd/apphost'], [], true, { forceBuild: false });

        const debugConfig = startAndGetDebugSession.firstCall.args[0];
        assert.strictEqual(debugConfig.program, path.resolve('/workspace', 'cmd', 'apphost'));
        assert.strictEqual(debugConfig.cwd, '/workspace');
    });

    test('keeps the build flags configured for the AppHost debugger', async () => {
        // The AppHost command carries no build flags, so aspire.config.json and launch.json are the only
        // place to ask for them.
        stubInstalledExtensions(['golang.go']);
        const debugSession = createAppHostDebugSession({
            debuggers: { apphost: { buildFlags: '-tags=integration' } }
        });
        sinon.stub(debugSession, 'createDebugAdapterTrackerCore');
        const startAndGetDebugSession = sinon.stub(debugSession, 'startAndGetDebugSession').resolves(undefined);

        await debugSession.startAppHost(appHostPath, ['go', 'run', '.'], [], true, { forceBuild: false });

        const debugConfig = startAndGetDebugSession.firstCall.args[0];
        assert.strictEqual(debugConfig.buildFlags, '-tags=integration');
    });

    test('tracks the Go debug session so the toolbar restart restarts the whole Aspire session', async () => {
        stubInstalledExtensions(['golang.go']);
        const debugSession = createAppHostDebugSession();
        const trackerStub = sinon.stub(debugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(debugSession, 'startAndGetDebugSession').resolves(undefined);

        await debugSession.startAppHost(appHostPath, ['go', 'run', '.'], [], true, { forceBuild: false });

        sinon.assert.calledOnce(trackerStub);
        assert.strictEqual(trackerStub.firstCall.args[0], 'go');
        const restartHandler = trackerStub.firstCall.args[1]?.onRestartRequested;
        assert.ok(restartHandler);
        // Returning true suppresses VS Code's own restart of the child session; the Aspire session is
        // restarted instead.
        assert.strictEqual(restartHandler(debugSession.debugSessionId), true);
    });

    test('hides the Delve restart request capability so the toolbar restart reaches the tracker', async () => {
        stubInstalledExtensions(['golang.go']);
        const debugSession = createAppHostDebugSession();
        const trackerStub = sinon.stub(debugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(debugSession, 'startAndGetDebugSession').resolves(undefined);

        await debugSession.startAppHost(appHostPath, ['go', 'run', '.'], [], true, { forceBuild: false });

        sinon.assert.calledOnce(trackerStub);
        assert.strictEqual(trackerStub.firstCall.args[1]?.disableDirectRestart, true);
    });

    test('keeps the restart request capability of an AppHost in another language', async () => {
        const debugSession = createAppHostDebugSession({ program: '/workspace/apphost.ts' });
        const trackerStub = sinon.stub(debugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(debugSession, 'startAndGetDebugSession').resolves(undefined);

        await debugSession.startAppHost('/workspace/apphost.ts', ['node', 'apphost.ts'], [], true, { forceBuild: false });

        sinon.assert.calledOnce(trackerStub);
        assert.ok(!trackerStub.firstCall.args[1]?.disableDirectRestart);
    });
});

function stubInstalledExtensions(extensionIds: string[]): void {
    sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
        return extensionIds.includes(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined;
    });
}

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
