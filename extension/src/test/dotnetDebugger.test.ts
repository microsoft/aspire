import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { createProjectDebuggerExtension, projectDebuggerExtension } from '../debugger/languages/dotnet';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, ProjectLaunchConfiguration } from '../dcp/types';
import * as io from '../utils/io';
import { ResourceDebuggerExtension } from '../debugger/debuggerExtensions';
import { AppHostParentOutputFilter, AspireDebugSession } from '../debugger/AspireDebugSession';

class TestDotNetService {
    private _getDotNetTargetPathStub: sinon.SinonStub;
    private _hasDevKit: boolean;

    public buildDotNetProjectStub: sinon.SinonStub;

    // `dotnet run-api` output returned for file-based (.cs) apps. Tests override this with a serialized
    // RunCommand payload; the default empty string mirrors the not-configured case.
    public runApiOutput: string = '';

    constructor(outputPath: string, rejectBuild: Error | null, hasDevKit: boolean) {
        this._getDotNetTargetPathStub = sinon.stub();
        this._getDotNetTargetPathStub.resolves(outputPath);

        this.buildDotNetProjectStub = sinon.stub();
        if (rejectBuild) {
            this.buildDotNetProjectStub.rejects(rejectBuild);
        } else {
            this.buildDotNetProjectStub.resolves();
        }

        this._hasDevKit = hasDevKit;
    }

    getDotNetTargetPath(projectFile: string): Promise<string> {
        return this._getDotNetTargetPathStub(projectFile);
    }

    buildDotNetProject(projectFile: string): Promise<void> {
        return this.buildDotNetProjectStub(projectFile);
    }

    getAndActivateDevKit(): Promise<boolean> {
        return Promise.resolve(this._hasDevKit);
    }

    getDotNetRunApiOutput(projectPath: string): Promise<string> {
        return Promise.resolve(this.runApiOutput);
    }
}

suite('Dotnet Debugger Extension Tests', () => {
    teardown(() => sinon.restore());

    function createDebuggerExtension(outputPath: string, rejectBuild: Error | null, hasDevKit: boolean, doesOutputFileExist: boolean): { dotNetService: TestDotNetService, extension: ResourceDebuggerExtension, doesFileExistStub: sinon.SinonStub } {
        const fakeDotNetService = new TestDotNetService(outputPath, rejectBuild, hasDevKit);
        return { dotNetService: fakeDotNetService, extension: createProjectDebuggerExtension(() => fakeDotNetService), doesFileExistStub: sinon.stub(io, 'doesFileExist').resolves(doesOutputFileExist) };
    }

    test('failed AppHost start writes error to debug console', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.ts'
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub()
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const outputEvents: any[] = [];
        const outputSubscription = aspireDebugSession.onDidSendMessage(message => outputEvents.push(message));
        const startError = new Error('AppHost build failed');

        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').rejects(startError);
        const showErrorMessageStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();

        await aspireDebugSession.startAppHost('/workspace/apphost.ts', ['node', 'apphost.ts'], [], true, { forceBuild: false });

        assert.ok(showErrorMessageStub.calledWith(startError.message));
        assert.ok(startError.stack);
        assert.ok(outputEvents.some(message =>
            message.type === 'event'
            && message.event === 'output'
            && message.body.category === 'stderr'
            && message.body.output.includes(startError.stack)));

        outputSubscription.dispose();
    });

    test('failed AppHost start does not duplicate already streamed build output', async () => {
        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            workspaceFolder: undefined,
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/apphost.ts'
            },
            customRequest: sinon.stub(),
            getDebugProtocolBreakpoint: sinon.stub()
        } as unknown as vscode.DebugSession;
        const aspireDebugSession = new AspireDebugSession(parentDebugSession, {} as any, {} as any, {} as any, () => { });
        const outputEvents: any[] = [];
        const outputSubscription = aspireDebugSession.onDidSendMessage(message => outputEvents.push(message));
        const startError = new Error('Build FAILED.');
        (startError as Error & { debugConsoleOutputAlreadyWritten?: boolean }).debugConsoleOutputAlreadyWritten = true;

        sinon.stub(aspireDebugSession, 'createDebugAdapterTrackerCore');
        sinon.stub(aspireDebugSession, 'startAndGetDebugSession').rejects(startError);
        const showErrorMessageStub = sinon.stub(vscode.window, 'showErrorMessage').resolves(undefined);
        sinon.stub(vscode.debug, 'stopDebugging').resolves();

        await aspireDebugSession.startAppHost('/workspace/apphost.ts', ['node', 'apphost.ts'], [], true, { forceBuild: false });

        assert.ok(showErrorMessageStub.calledWith(startError.message));
        assert.strictEqual(outputEvents.some(message =>
            message.type === 'event'
            && message.event === 'output'
            && message.body.output.includes(startError.message)), false);

        outputSubscription.dispose();
    });

    test('filters AppHost debugger noise from Aspire parent debug console', () => {
        const filter = new AppHostParentOutputFilter();

        assert.strictEqual(filter.filter("'TestShop.AppHost' (CoreCLR: clrhost): Loaded '/dotnet/System.Private.CoreLib.dll'. Skipped loading symbols.\n", 'console'), undefined);
        assert.strictEqual(filter.filter("TestShop.AppHost.dll (29067): Loaded '/usr/local/share/dotnet/shared/Microsoft.NETCore.App/8.0.14/System.Private.CoreLib.dll'. No se puede encontrar o abrir el archivo PDB.\n", 'console'), undefined);
        assert.strictEqual(filter.filter("Loaded '/dotnet/System.Net.Http.dll'. Skipped loading symbols.\n", 'console'), undefined);
        assert.strictEqual(filter.filter("Exception thrown: 'System.InvalidOperationException' in TestShop.AppHost.dll\n", 'console'), undefined);
        assert.strictEqual(filter.filter('debug adapter details\n', 'debug'), undefined);
        assert.strictEqual(filter.filter('-------------------------------------------------------------------------------\n', 'console'), undefined);
        assert.strictEqual(filter.filter('You may only use the Microsoft Visual Studio .NET/C/C++ Debugger with Visual Studio Code.\n', 'console'), undefined);
        assert.strictEqual(filter.filter('Usando la configuración de inicio de "/workspace/Properties/launchSettings.json" [perfil "https"]...\n', 'console'), undefined);
        assert.strictEqual(filter.filter("dbug: Aspire.Hosting.Health.ResourceHealthCheckService[0]\n      Resource 'apigateway' is ready.\n", 'stdout'), undefined);
        assert.strictEqual(filter.filter("Aspire.Hosting.Health.ResourceHealthCheckService: Debug: Resource 'apigateway' is ready.\n", 'stdout'), undefined);
    });

    test('keeps AppHost fatal output in Aspire parent debug console', () => {
        const filter = new AppHostParentOutputFilter();
        const criticalLog = "crit: TestShop.AppHost[0]\n      Host terminated unexpectedly.\n";
        const unhandledException = 'Unhandled exception. System.InvalidOperationException: boom\n';
        const unhandledBaseException = 'Unhandled exception. System.Exception: This code snippet is for illustrative purposes only.\n   at Program.<Main>$(String[] args) in /workspace/AppHost.cs:line 8\n';
        const javascriptException = 'Uncaught TypeError: Cannot read properties of undefined\n    at file:///workspace/apphost.js:8:3\n';
        const nodeModuleException = "Error [ERR_MODULE_NOT_FOUND]: Cannot find package '@microsoft/aspire'\n    at packageResolve (node:internal/modules/esm/resolve:857:9)\n";
        const consoleError = 'Fatal error: unable to bind port\n';

        assert.deepStrictEqual(filter.filter(criticalLog, 'stdout'), { output: criticalLog, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(unhandledException, 'console'), { output: unhandledException, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(unhandledBaseException, 'console'), { output: unhandledBaseException, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(javascriptException, 'console'), { output: javascriptException, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(nodeModuleException, 'console'), { output: nodeModuleException, category: 'stderr' });
        assert.deepStrictEqual(filter.filter(consoleError, 'console'), { output: consoleError, category: 'stderr' });
    });

    test('keeps AppHost warning and information output that is not debugger console chatter', () => {
        const filter = new AppHostParentOutputFilter();
        const warningLog = "warn: TestShop.AppHost[0]\n      Port is already allocated.\n";
        const normalOutput = 'Now listening on: https://localhost:5001\n';

        assert.deepStrictEqual(filter.filter(warningLog, 'stdout'), { output: warningLog, category: 'stdout' });
        assert.deepStrictEqual(filter.filter(normalOutput, 'stdout'), { output: normalOutput, category: 'stdout' });
    });

    test('does not promote benign AppHost stdout containing words like fail/error to stderr', () => {
        const filter = new AppHostParentOutputFilter();
        const benignFailMention = 'Failed payment retry queued for processing\n';
        const benignErrorMention = 'Loaded handler from /src/error_handler/main.cs\n';
        const benignFailureMention = 'Build complete with no failures detected\n';

        assert.deepStrictEqual(filter.filter(benignFailMention, 'stdout'), { output: benignFailMention, category: 'stdout' });
        assert.deepStrictEqual(filter.filter(benignErrorMention, 'stdout'), { output: benignErrorMention, category: 'stdout' });
        assert.deepStrictEqual(filter.filter(benignFailureMention, 'stdout'), { output: benignFailureMention, category: 'stdout' });
    });

    test('does not classify arbitrary user stdout shaped like prefix:Level: as a structured log', () => {
        const filter = new AppHostParentOutputFilter();
        const userPrint = 'Status: Error: connection refused\n';
        const userDebugPrint = 'Note: Debug: caller line 42\n';

        assert.deepStrictEqual(filter.filter(userPrint, 'stdout'), { output: userPrint, category: 'stdout' });
        assert.deepStrictEqual(filter.filter(userDebugPrint, 'stdout'), { output: userDebugPrint, category: 'stdout' });
    });

    test('continuation state is reset when DAP category changes between events', () => {
        const filter = new AppHostParentOutputFilter();
        // First event: a dropped trace log on stdout. Continuation state would say "drop indented lines".
        assert.strictEqual(filter.filter('trce: Some.Category[0]\n', 'stdout'), undefined);
        // A subsequent event on a different category (console) that happens to start with
        // an indented line must NOT be silently dropped as a continuation of the trace log.
        const indentedConsoleLine = '    Loaded module foo\n';
        assert.strictEqual(filter.filter(indentedConsoleLine, 'console'), undefined); // dropped because console+non-severe, not because of continuation state
        // And an indented stdout line afterwards is emitted normally instead of being dropped.
        const indentedStdoutLine = '    plain user output line\n';
        assert.deepStrictEqual(filter.filter(indentedStdoutLine, 'stdout'), { output: indentedStdoutLine, category: 'stdout' });
    });

    test('treats missing DAP category as console so debugger noise does not leak as stdout', () => {
        const filter = new AppHostParentOutputFilter();
        // Per the DAP spec a missing category should be treated as 'console'. The
        // .NET debug adapter sometimes emits output events without a category, and
        // this debugger chatter must be suppressed the same way as explicit
        // 'console'-category lines instead of being mirrored as stdout.
        const debuggerChatter = "'TestShop.AppHost' (CoreCLR: clrhost): Loaded '/dotnet/System.Private.CoreLib.dll'. Skipped loading symbols.\n";
        assert.strictEqual(filter.filter(debuggerChatter, undefined), undefined);

        // Severe runtime output without a category is still kept and promoted to stderr,
        // matching the existing 'console'-category behavior.
        const unhandledException = 'Unhandled exception. System.InvalidOperationException: boom\n';
        assert.deepStrictEqual(filter.filter(unhandledException, undefined), { output: unhandledException, category: 'stderr' });
    });

    test('project is built when C# dev kit is installed and executable not found', async () => {
        const outputPath = 'C:\\temp\\bin\\Debug\\net7.0\\TestProject.dll';
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, false);

        const projectPath = 'C:\\temp\\TestProject.csproj';
        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, outputPath);
        assert.strictEqual(dotNetService.buildDotNetProjectStub.called, true);
    });

    test('project is not built when C# dev kit is installed and executable found', async () => {
        const outputPath = 'C:\\temp\\bin\\Debug\\net7.0\\TestProject.dll';
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

        const projectPath = 'C:\\temp\\TestProject.csproj';
        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, outputPath);
        assert.strictEqual(dotNetService.buildDotNetProjectStub.notCalled, true);
    });

    test('advertises the coreclr project debugger and extracts project_path for .csproj and file-based .cs', () => {
        // A DotnetProjectResource (AddDotnetProject) advertises the same "project" launch capability as
        // AddProject and emits a ProjectLaunchConfiguration carrying project_path (a .csproj or a file-based
        // .cs). The extension's .NET debugger keys purely off that "project" type + project_path, so it must
        // resolve the same coreclr debugger and project file regardless of which resource produced the config.
        assert.strictEqual(projectDebuggerExtension.resourceType, 'project');
        assert.strictEqual(projectDebuggerExtension.debugAdapter, 'coreclr');
        assert.deepStrictEqual(projectDebuggerExtension.getSupportedFileTypes(), ['.cs', '.csproj']);

        const csprojConfig: ProjectLaunchConfiguration = { type: 'project', project_path: '/tmp/Worker.csproj' };
        const fileBasedConfig: ProjectLaunchConfiguration = { type: 'project', project_path: '/tmp/app.cs' };
        assert.strictEqual(projectDebuggerExtension.getProjectFile(csprojConfig), '/tmp/Worker.csproj');
        assert.strictEqual(projectDebuggerExtension.getProjectFile(fileBasedConfig), '/tmp/app.cs');
    });

    test('file-based .cs project launches the dotnet run-api executable under coreclr', async () => {
        // A file-based DotnetProjectResource emits a "project" launch config whose project_path is a .cs file.
        // Unlike a .csproj (launched from its build output), a .cs app has no build output path, so the
        // extension resolves the runnable program via `dotnet run-api`. This proves the file-based half of the
        // AddDotnetProject debug contract: project_path (.cs) -> run-api ExecutablePath, launched under coreclr.
        const executablePath = '/tmp/obj/Debug/net10.0/app';
        const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
        dotNetService.runApiOutput = JSON.stringify({
            $type: 'RunCommand',
            Version: 1,
            ExecutablePath: executablePath,
            CommandLineArguments: '',
            WorkingDirectory: '',
            EnvironmentVariables: { RUNAPI_ENV: 'from-run-api' }
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/tmp/app.cs'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(extension.debugAdapter, 'coreclr');
        assert.strictEqual(debugConfig.program, executablePath);
        assert.strictEqual(debugConfig.args, undefined);
        // cwd defaults to the file's directory; run-api's WorkingDirectory (empty here) is not consumed.
        assert.strictEqual(debugConfig.cwd, '/tmp');
        // run-api's profile-derived EnvironmentVariables are dropped (only DOTNET_ROOT* would be kept), so
        // RUNAPI_ENV must not appear.
        assert.deepStrictEqual(debugConfig.env, {});
        assert.strictEqual(dotNetService.buildDotNetProjectStub.called, true);
    });

    test('file-based .cs project preserves run-api DOTNET_ROOT host variables but drops profile env', async () => {
        // `dotnet run-api` returns the SDK default launch profile's environment variables mixed with the runtime
        // host-resolution variables the SDK injects (DOTNET_ROOT / DOTNET_ROOT_<ARCH>). The profile-derived values
        // (DOTNET_LAUNCH_PROFILE, ASPNETCORE_URLS, and the profile's own env) must be dropped because the user may
        // have selected a different profile that is resolved separately, but the DOTNET_ROOT* variables must be
        // preserved or an apphost-executable build can resolve the wrong runtime or fail to start.
        const executablePath = '/tmp/obj/Debug/net10.0/app';
        const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
        dotNetService.runApiOutput = JSON.stringify({
            $type: 'RunCommand',
            Version: 1,
            ExecutablePath: executablePath,
            CommandLineArguments: '',
            WorkingDirectory: '',
            EnvironmentVariables: {
                DOTNET_ROOT: '/usr/share/dotnet',
                DOTNET_ROOT_X64: '/usr/share/dotnet/x64',
                DOTNET_LAUNCH_PROFILE: 'default',
                ASPNETCORE_URLS: 'http://localhost:5000',
                RUNAPI_ENV: 'from-run-api'
            }
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/tmp/app.cs'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, executablePath);
        // Only the DOTNET_ROOT* runtime host-resolution variables survive; every profile-derived value is dropped.
        assert.deepStrictEqual(debugConfig.env, {
            DOTNET_ROOT: '/usr/share/dotnet',
            DOTNET_ROOT_X64: '/usr/share/dotnet/x64'
        });
    });

    test('file-based .cs project launched via the dotnet launcher keeps the run-api host DLL and user args', async () => {
        // When the SDK resolves a file-based app to the `dotnet` launcher, run-api returns ExecutablePath=dotnet
        // and CommandLineArguments carrying the built app DLL. The extension must launch that program with the
        // DLL host argument first, then the user application arguments supplied by DCP. Dropping CommandLineArguments
        // would launch the 'dotnet' launcher with nothing to run. The working directory and environment come from
        // the launch profile (here, its defaults) — not from run-api, whose values reflect the SDK default profile.
        const dllPath = '/tmp/obj/Debug/net10.0/app.dll';
        const workingDirectory = '/tmp/obj/Debug/net10.0';
        const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
        dotNetService.runApiOutput = JSON.stringify({
            $type: 'RunCommand',
            Version: 1,
            ExecutablePath: 'dotnet',
            CommandLineArguments: dllPath,
            WorkingDirectory: workingDirectory,
            EnvironmentVariables: { RUNAPI_ENV: 'from-run-api' }
        });

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: '/tmp/app.cs'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, ['--message', 'hello'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, 'dotnet');
        assert.strictEqual(debugConfig.args, `${dllPath} --message hello`);
        // cwd comes from the launch profile default (the file's directory), not run-api's WorkingDirectory.
        assert.strictEqual(debugConfig.cwd, '/tmp');
        // run-api's profile-derived EnvironmentVariables are dropped (only DOTNET_ROOT* would be kept), so
        // RUNAPI_ENV must not appear.
        assert.deepStrictEqual(debugConfig.env, {});
        assert.strictEqual(dotNetService.buildDotNetProjectStub.called, true);
    });

    test('file-based .cs applies the launch profile working directory', async () => {
        // A file-based app can carry a `<name>.run.json` launch profile. When that profile sets an explicit
        // workingDirectory the extension must resolve and apply it, mirroring how launch profiles set the
        // working directory for .csproj projects. run-api's own WorkingDirectory is never consumed.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            const profileWorkingDirectory = path.join(tempRoot, 'from-profile');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        workingDirectory: profileWorkingDirectory
                    }
                }
            }));

            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: 'dotnet',
                CommandLineArguments: '',
                WorkingDirectory: path.join(tempRoot, 'from-run-api'),
                EnvironmentVariables: {}
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.cwd, profileWorkingDirectory);
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('file-based .cs apphost-executable build does not duplicate launch profile arguments from run-api', async () => {
        // For an apphost-executable build, `dotnet run-api` returns the app's own executable as ExecutablePath
        // and — because it always applies the SDK default launch profile — echoes that profile's arguments back
        // in CommandLineArguments. The extension resolves the same profile arguments itself, so it must NOT also
        // prepend run-api's CommandLineArguments or the arguments would appear twice.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-dup-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        commandLineArgs: '--from-profile'
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '--from-profile',
                WorkingDirectory: '',
                EnvironmentVariables: {}
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            // No run session arguments (undefined) so the launch profile's arguments are the ones used.
            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            assert.strictEqual(debugConfig.args, '--from-profile');
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('file-based .cs with disable_launch_profile ignores run-api profile arguments, working directory, and profile environment but keeps DOTNET_ROOT', async () => {
        // With disable_launch_profile the extension selects no launch profile, but `dotnet run-api` still applies
        // the SDK default profile and returns its arguments, working directory, and environment. The profile
        // values may not leak into the debug configuration: args come only from the run session, cwd defaults to
        // the file's directory, and no profile env is applied. The runtime host-resolution variables (DOTNET_ROOT*)
        // are NOT profile-derived, so they must still be preserved even when the profile is disabled.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-disabled-profile-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        commandLineArgs: '--from-disabled-profile',
                        workingDirectory: path.join(tempRoot, 'from-disabled-profile'),
                        environmentVariables: {
                            DISABLED_PROFILE_ENV: 'should-not-appear'
                        }
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '--from-disabled-profile',
                WorkingDirectory: path.join(tempRoot, 'from-run-api'),
                EnvironmentVariables: { DISABLED_PROFILE_ENV: 'should-not-appear', DOTNET_ROOT: '/usr/share/dotnet' }
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                disable_launch_profile: true
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            assert.strictEqual(debugConfig.args, undefined);
            assert.strictEqual(debugConfig.cwd, tempRoot);
            // The profile env (DISABLED_PROFILE_ENV) is dropped; the runtime host variable DOTNET_ROOT is preserved.
            assert.deepStrictEqual(debugConfig.env, { DOTNET_ROOT: '/usr/share/dotnet' });
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('file-based dotnet.cs apphost named dotnet is not mistaken for the launcher', async () => {
        // A file-based app whose entry file is `dotnet.cs` builds an apphost whose AssemblyName — and therefore
        // executable file name — is `dotnet`/`dotnet.exe`, the same name as the launcher but at a full build-output
        // path. run-api returns that full path as ExecutablePath and echoes the SDK default profile's arguments
        // in CommandLineArguments. Because the program is an apphost (a rooted path), not the launcher (a bare
        // command name), the extension must NOT treat CommandLineArguments as host arguments — it resolves the
        // profile arguments itself, so prepending run-api's would duplicate them.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-launchername-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'dotnet.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'dotnet.run.json'), JSON.stringify({
                profiles: {
                    dotnet: {
                        commandName: 'Project',
                        commandLineArgs: '--from-profile'
                    }
                }
            }));

            // The apphost executable derives its name from the .cs file, so it is `dotnet` / `dotnet.exe`.
            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '--from-profile',
                WorkingDirectory: '',
                EnvironmentVariables: {}
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            // No run session arguments (undefined) so the launch profile's arguments are the ones used.
            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            // The apphost's own name is `dotnet`, but it is a full path, so it is not the launcher: the profile
            // arguments appear exactly once and are not prefixed with run-api's CommandLineArguments.
            assert.strictEqual(debugConfig.args, '--from-profile');
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('file-based .cs drops a DOTNET_ROOT defined by the launch profile but keeps the SDK-injected host variable', async () => {
        // `dotnet run-api` applies the SDK default launch profile, whose environmentVariables can define
        // DOTNET_ROOT and then overwrite the SDK-injected value in run-api's output. A profile-derived
        // DOTNET_ROOT must not be treated as an SDK runtime host variable: with the profile disabled (as here)
        // it would otherwise leak and could launch against the wrong runtime. The architecture-specific
        // DOTNET_ROOT_X64 is NOT defined by any profile, so it is a genuine SDK host-resolution variable and
        // must be preserved.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-root-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        environmentVariables: {
                            DOTNET_ROOT: '/profile/dotnet'
                        }
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '',
                WorkingDirectory: '',
                // run-api echoes the profile's DOTNET_ROOT (overwriting the SDK value) alongside the
                // SDK-injected arch-specific DOTNET_ROOT_X64 that no profile defines.
                EnvironmentVariables: { DOTNET_ROOT: '/profile/dotnet', DOTNET_ROOT_X64: '/usr/share/dotnet/x64' }
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                disable_launch_profile: true
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            // DOTNET_ROOT is defined by the (disabled) profile, so run-api's value is dropped; the SDK-injected
            // DOTNET_ROOT_X64 survives.
            assert.deepStrictEqual(debugConfig.env, { DOTNET_ROOT_X64: '/usr/share/dotnet/x64' });
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('file-based .cs falls back to dotnet run (no debugger) when the default profile is an Executable profile and launch profile is disabled', async () => {
        // `dotnet run-api` always applies the first *supported* profile and offers no way to request a
        // no-profile command. Here an 'Executable' profile appears BEFORE a 'Project' profile, so run-api would
        // report the Executable profile's external command (e.g. `some-external-tool --version`), NOT the .cs app.
        // With the launch profile disabled the extension must not trust run-api's program; it launches the app
        // itself via `dotnet run --file <app.cs> --no-cache --no-launch-profile` with the debugger detached.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-exec-default-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    // First supported profile: run-api applies this Executable profile and reports its external
                    // command, not the .cs app.
                    runExe: {
                        commandName: 'Executable',
                        executablePath: 'some-external-tool',
                        commandLineArgs: '--version'
                    },
                    // A later 'Project' profile that is not selected here.
                    app: {
                        commandName: 'Project'
                    }
                }
            }));

            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            // The realistic run-api output for an Executable default profile is the external command. If the
            // extension (incorrectly) trusted run-api, it would launch this program. It must not.
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: 'some-external-tool',
                CommandLineArguments: '--version',
                WorkingDirectory: '',
                EnvironmentVariables: {}
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                disable_launch_profile: true
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            // The extension launches the app itself instead of the Executable profile's external command.
            assert.strictEqual(debugConfig.program, 'dotnet');
            assert.deepStrictEqual(debugConfig.args, ['run', '--file', projectPath, '--no-cache', '--no-launch-profile']);
            assert.strictEqual(debugConfig.noDebug, true);
            assert.strictEqual(debugConfig.cwd, tempRoot);
            assert.deepStrictEqual(debugConfig.env, {});
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('file-based .cs falls back to dotnet run applying the selected Project profile when the default profile is Executable', async () => {
        // The default (first) profile is an 'Executable' profile that `dotnet run-api` would apply, but the user
        // explicitly selected a later 'Project' profile. run-api still reports the Executable profile's external
        // command, so the extension launches the app itself via `dotnet run --file ... --no-launch-profile`
        // (no debugger attach) while applying the selected profile's arguments and environment.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-exec-default-select-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    runExe: {
                        commandName: 'Executable',
                        executablePath: 'some-external-tool',
                        commandLineArgs: '--version'
                    },
                    app: {
                        commandName: 'Project',
                        commandLineArgs: '--from-profile',
                        environmentVariables: {
                            APP_ENV: 'from-project-profile'
                        }
                    }
                }
            }));

            const { extension } = createDebuggerExtension('unused-build-output', null, true, true);

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'app'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            // No run session args (undefined): the selected profile's commandLineArgs are appended after `--`.
            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, 'dotnet');
            // The selected 'app' profile's arguments are preserved verbatim after `--`; only the path is quoted.
            assert.strictEqual(debugConfig.args, `run --file "${projectPath}" --no-cache --no-launch-profile -- --from-profile`);
            assert.strictEqual(debugConfig.noDebug, true);
            assert.strictEqual(debugConfig.cwd, tempRoot);
            assert.deepStrictEqual(debugConfig.env, { APP_ENV: 'from-project-profile' });
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('file-based .cs dotnet run fallback preserves the selected Project profile working directory', async () => {
        // Same fallback as above (an Executable default profile forces `dotnet run --file ... --no-launch-profile`),
        // but the selected Project profile sets a custom workingDirectory. Because --no-launch-profile stops
        // `dotnet run` from applying the profile's workingDirectory itself, the extension must keep the cwd it
        // resolved from the selected profile; the fallback must NOT reset it to the .cs file's own directory.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-exec-default-wd-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    runExe: {
                        commandName: 'Executable',
                        executablePath: 'some-external-tool',
                        commandLineArgs: '--version'
                    },
                    app: {
                        commandName: 'Project',
                        workingDirectory: 'custom',
                        commandLineArgs: '--from-profile',
                        environmentVariables: {
                            APP_ENV: 'from-project-profile'
                        }
                    }
                }
            }));

            const { extension } = createDebuggerExtension('unused-build-output', null, true, true);

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'app'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, 'dotnet');
            assert.strictEqual(debugConfig.args, `run --file "${projectPath}" --no-cache --no-launch-profile -- --from-profile`);
            assert.strictEqual(debugConfig.noDebug, true);
            // The selected profile's relative workingDirectory is resolved against the .cs file's directory and
            // preserved through the fallback (it must NOT be reset to the file's own directory).
            assert.strictEqual(debugConfig.cwd, path.join(tempRoot, 'custom'));
            assert.deepStrictEqual(debugConfig.env, { APP_ENV: 'from-project-profile' });
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('file-based .cs uses the selected profile DOTNET_ROOT rather than the default profile value from run-api', async () => {
        // `dotnet run-api` always applies the SDK *default* (first) profile, so its DOTNET_ROOT reflects that
        // profile. When the extension selects a *different* profile, run-api's default-profile DOTNET_ROOT must
        // not override the selected profile's own DOTNET_ROOT. The selected profile's value wins, and the
        // SDK-injected DOTNET_ROOT_X64 (defined by no profile) is still preserved from run-api.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-root-select-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    app: {
                        commandName: 'Project',
                        environmentVariables: {
                            DOTNET_ROOT: '/default/dotnet'
                        }
                    },
                    other: {
                        commandName: 'Project',
                        environmentVariables: {
                            DOTNET_ROOT: '/selected/dotnet'
                        }
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '',
                WorkingDirectory: '',
                // run-api applied the default profile 'app', so it returns that profile's DOTNET_ROOT.
                EnvironmentVariables: { DOTNET_ROOT: '/default/dotnet', DOTNET_ROOT_X64: '/usr/share/dotnet/x64' }
            });

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'other'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            // The selected profile's DOTNET_ROOT wins over run-api's default-profile value; DOTNET_ROOT_X64
            // (SDK-injected, defined by no profile) is preserved.
            assert.deepStrictEqual(debugConfig.env, { DOTNET_ROOT: '/selected/dotnet', DOTNET_ROOT_X64: '/usr/share/dotnet/x64' });
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('file-based .cs preserves a genuine SDK DOTNET_ROOT even when an unrelated profile defines that name', async () => {
        // `dotnet run-api` only ever applies the SDK default (first 'Project') profile, so only that profile —
        // and the profile the extension selected — can legitimately shadow a DOTNET_ROOT* value. A DOTNET_ROOT*
        // defined by some *other*, unselected profile must NOT cause run-api's genuine SDK-injected value of the
        // same name to be discarded. Here the default/selected profile 'app' defines no DOTNET_ROOT*, while an
        // unrelated profile 'other' defines DOTNET_ROOT_X64; run-api reports a real SDK DOTNET_ROOT_X64, which
        // must be preserved so the file-based app can locate the runtime.
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-runapi-root-unrelated-${process.pid}-${Date.now()}`);
        fs.mkdirSync(tempRoot, { recursive: true });

        try {
            const projectPath = path.join(tempRoot, 'app.cs');
            fs.writeFileSync(projectPath, '// file-based app');
            fs.writeFileSync(path.join(tempRoot, 'app.run.json'), JSON.stringify({
                profiles: {
                    // The default (first 'Project') profile the extension selects and run-api applies. It does
                    // not define any DOTNET_ROOT*.
                    app: {
                        commandName: 'Project'
                    },
                    // An unrelated profile that is never selected. Its DOTNET_ROOT_X64 must not affect the result.
                    other: {
                        commandName: 'Project',
                        environmentVariables: {
                            DOTNET_ROOT_X64: '/unrelated/dotnet/x64'
                        }
                    }
                }
            }));

            const executablePath = path.join(tempRoot, 'obj', 'Debug', 'net10.0', 'app');
            const { extension, dotNetService } = createDebuggerExtension('unused-build-output', null, true, true);
            dotNetService.runApiOutput = JSON.stringify({
                $type: 'RunCommand',
                Version: 1,
                ExecutablePath: executablePath,
                CommandLineArguments: '',
                WorkingDirectory: '',
                // The genuine SDK-injected host variable. Neither the selected nor the run-api default profile
                // defines it.
                EnvironmentVariables: { DOTNET_ROOT_X64: '/usr/share/dotnet/x64' }
            });

            // No launch_profile: the extension uses the default profile 'app', which run-api also applied.
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.program, executablePath);
            // The unrelated 'other' profile's DOTNET_ROOT_X64 must not poison the exclusion set, so run-api's
            // genuine SDK-injected DOTNET_ROOT_X64 survives.
            assert.deepStrictEqual(debugConfig.env, { DOTNET_ROOT_X64: '/usr/share/dotnet/x64' });
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('does not use dotnet run when ordinary project launch configuration requests NoDebug', async () => {
        const outputPath = '/tmp/bin/Debug/net10.0/Worker.dll';
        const { extension } = createDebuggerExtension(outputPath, null, true, true);

        const projectPath = '/tmp/Worker.csproj';
        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            mode: 'NoDebug',
            project_path: projectPath
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch',
            noDebug: true
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, [], [], { debug: false, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        assert.strictEqual(debugConfig.program, outputPath);
        assert.strictEqual(debugConfig.noDebug, true);
    });

    test('uses dotnet CLI when project runtimeconfig has no runnable framework', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Spaces');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), JSON.stringify({
                runtimeOptions: {
                    tfm: 'net10.0'
                }
            }));

            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, ['--message', 'hello world'], [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.type, 'coreclr');
            assert.strictEqual(debugConfig.program, 'dotnet');
            assert.deepStrictEqual(debugConfig.args, ['run', '--project', projectPath, '--no-launch-profile', '--', '--message', 'hello world']);
            assert.strictEqual(debugConfig.noDebug, true);
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('preserves launch profile argument string for dotnet CLI fallback when run session arguments are absent', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Profile');
        const propertiesDir = path.join(projectDir, 'Properties');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(propertiesDir, { recursive: true });
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), JSON.stringify({
                runtimeOptions: {
                    tfm: 'net10.0'
                }
            }));
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    Development: {
                        commandLineArgs: '--arg "value with spaces" --message "say \\"hi\\"" --path "C:\\Temp\\file.txt"'
                    }
                }
            }));

            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'Development'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(debugConfig.args, `run --project "${projectPath}" --no-launch-profile -- --arg "value with spaces" --message "say \\"hi\\"" --path "C:\\Temp\\file.txt"`);
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('ignores launch profile executable settings when using dotnet CLI fallback', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Executable Settings');
        const propertiesDir = path.join(projectDir, 'Properties');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(propertiesDir, { recursive: true });
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), JSON.stringify({
                runtimeOptions: {
                    tfm: 'net10.0'
                }
            }));
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify({
                profiles: {
                    Development: {
                        workingDirectory: 'custom',
                        executablePath: 'customExecutable'
                    }
                }
            }));

            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'Development'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            // The dotnet CLI fallback ignores the profile's executablePath (that setting only applies to
            // 'Executable' command profiles), but it still honors the profile's workingDirectory: it is resolved
            // into cwd up front and must survive the fallback so the app runs from the configured directory.
            assert.strictEqual(debugConfig.cwd, path.join(projectDir, 'custom'));
            assert.strictEqual(debugConfig.executablePath, undefined);
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('fails project launch when runtimeconfig cannot be parsed', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Invalid RuntimeConfig');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), '{');

            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig),
                /Failed to inspect runtimeconfig/);
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('notifies user when dotnet CLI fallback disables debugger attach', async () => {
        const fs = require('fs');
        const path = require('path');

        const tempRoot = path.join(process.cwd(), '.test-temp', `dotnet-debugger-${process.pid}-${Date.now()}`);
        const projectDir = path.join(tempRoot, 'Frontend With Notification');
        const outputDir = path.join(projectDir, 'bin', 'Debug', 'net10.0');
        fs.mkdirSync(outputDir, { recursive: true });

        try {
            const projectPath = path.join(projectDir, 'Frontend.csproj');
            const outputPath = path.join(outputDir, 'Frontend.dll');
            fs.writeFileSync(projectPath, '<Project></Project>');
            fs.writeFileSync(outputPath, '');
            fs.writeFileSync(path.join(outputDir, 'Frontend.runtimeconfig.json'), JSON.stringify({
                runtimeOptions: {
                    tfm: 'net10.0'
                }
            }));

            const showInformationMessageStub = sinon.stub(vscode.window, 'showInformationMessage').resolves(undefined);
            const { extension } = createDebuggerExtension(outputPath, null, true, true);
            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

            assert.strictEqual(showInformationMessageStub.calledOnce, true);
            assert.match(showInformationMessageStub.firstCall.args[0], /breakpoints/i);
        } finally {
            fs.rmSync(tempRoot, { recursive: true, force: true });
        }
    });

    test('applies launch profile settings to debug configuration', async () => {
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        const projectDir = path.join(tempDir, 'TestProject');
        const propertiesDir = path.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const projectPath = path.join(projectDir, 'TestProject.csproj');
        fs.writeFileSync(projectPath, '<Project></Project>');

        const launchSettings = {
            profiles: {
                'Development': {
                    commandLineArgs: '--arg "value" --flag',
                    environmentVariables: {
                        BASE: 'base'
                    },
                    workingDirectory: 'custom',
                    executablePath: 'exePath',
                    useSSL: true,
                    launchBrowser: true,
                    applicationUrl: 'https://localhost:5001'
                }
            }
        };

        fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

        const outputPath = path.join(projectDir, 'bin', 'Debug', 'net7.0', 'TestProject.dll');
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath,
            launch_profile: 'Development'
        };

        // Provide a run session env that overrides BASE and adds RUN
        const runEnv = [
            { name: 'BASE', value: 'overridden' },
            { name: 'RUN', value: 'run' }
        ];

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, runEnv, { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        // program should be set
        assert.strictEqual(debugConfig.program, outputPath);

        // cwd should resolve to projectDir/custom
        assert.strictEqual(debugConfig.cwd, path.join(projectDir, 'custom'));

        // args should be parsed from commandLineArgs
        assert.deepStrictEqual(debugConfig.args, '--arg "value" --flag');

        // env should include merged values with run session overriding base
        assert.strictEqual(debugConfig.env.BASE, 'overridden');
        assert.strictEqual(debugConfig.env.RUN, 'run');

        // executablePath and checkForDevCert
        assert.strictEqual(debugConfig.executablePath, 'exePath');
        assert.strictEqual(debugConfig.checkForDevCert, true);

        // serverReadyAction should be present with the applicationUrl
        assert.notStrictEqual(debugConfig.serverReadyAction, undefined);
        assert.strictEqual(debugConfig.serverReadyAction.uriFormat, 'https://localhost:5001');

        // cleanup
        fs.rmSync(tempDir, { recursive: true, force: true });
    });

    test('uses executable path for Executable command launch profiles instead of project output', async () => {
        // Bug #15647: Executable command profiles use the executablePath and
        // commandLineArgs to define how to run the class library project. The extension
        // should use executablePath as the program instead of the project's output DLL.
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        const projectDir = path.join(tempDir, 'MyClassLibFunction');
        const propertiesDir = path.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const projectPath = path.join(projectDir, 'MyClassLibFunction.csproj');
        fs.writeFileSync(projectPath, '<Project></Project>');

        const launchSettings = {
            profiles: {
                'Aspire_my-function': {
                    commandName: 'Executable',
                    executablePath: 'dotnet',
                    commandLineArgs: 'exec --depsfile ./MyClassLibFunction.deps.json --runtimeconfig ./MyClassLibFunction.runtimeconfig.json RuntimeSupport.dll MyClassLibFunction::MyClassLibFunction.Function::FunctionHandler',
                    workingDirectory: 'bin/Debug/net10.0/',
                    environmentVariables: {
                        FUNCTION_ENV: 'test'
                    }
                }
            }
        };

        fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

        // The output path would be a class library DLL - this should NOT be used as program
        const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'MyClassLibFunction.dll');
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath,
            launch_profile: 'Aspire_my-function'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        // program should be the executable path from the profile, NOT the project output DLL
        assert.strictEqual(debugConfig.program, 'dotnet');

        // args should come from the profile's commandLineArgs
        assert.strictEqual(debugConfig.args, 'exec --depsfile ./MyClassLibFunction.deps.json --runtimeconfig ./MyClassLibFunction.runtimeconfig.json RuntimeSupport.dll MyClassLibFunction::MyClassLibFunction.Function::FunctionHandler');

        // cwd should resolve to the profile's working directory
        assert.strictEqual(debugConfig.cwd, path.resolve(projectDir, 'bin/Debug/net10.0/'));

        // env should include the profile's environment variables
        assert.strictEqual(debugConfig.env.FUNCTION_ENV, 'test');

        // project should still be built (to compile the class library dependencies)
        assert.strictEqual(dotNetService.buildDotNetProjectStub.calledOnce, true);

        // cleanup
        fs.rmSync(tempDir, { recursive: true, force: true });
    });

    test('fails project launch when the selected Executable launch profile has no executablePath', async () => {
        // An Executable-command profile requires an executablePath. The .NET SDK's ExecutableProvider errors
        // when it is missing, so the extension must surface a configuration error rather than silently falling
        // through and launching the project output.
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        try {
            const projectDir = path.join(tempDir, 'MyClassLibFunction');
            const propertiesDir = path.join(projectDir, 'Properties');
            fs.mkdirSync(propertiesDir, { recursive: true });

            const projectPath = path.join(projectDir, 'MyClassLibFunction.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');

            const launchSettings = {
                profiles: {
                    'Aspire_my-function': {
                        commandName: 'Executable',
                        commandLineArgs: 'exec RuntimeSupport.dll'
                    }
                }
            };
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

            const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'MyClassLibFunction.dll');
            const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath,
                launch_profile: 'Aspire_my-function'
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig),
                /Launch profile 'Aspire_my-function' uses commandName 'Executable' but does not specify an executablePath/);

            // The invalid profile must be rejected before any build/launch work happens.
            assert.strictEqual(dotNetService.buildDotNetProjectStub.called, false);
        } finally {
            fs.rmSync(tempDir, { recursive: true, force: true });
        }
    });

    test('fails project launch when the default Executable launch profile has no executablePath', async () => {
        // With no explicit launch_profile, the extension resolves the SDK default (first supported) profile.
        // When that default is an Executable profile without an executablePath, `dotnet run` would error, so
        // the extension must surface the same configuration error instead of launching the project output.
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        try {
            const projectDir = path.join(tempDir, 'MyClassLibFunction');
            const propertiesDir = path.join(projectDir, 'Properties');
            fs.mkdirSync(propertiesDir, { recursive: true });

            const projectPath = path.join(projectDir, 'MyClassLibFunction.csproj');
            fs.writeFileSync(projectPath, '<Project></Project>');

            const launchSettings = {
                profiles: {
                    'RunExe': {
                        commandName: 'Executable',
                        commandLineArgs: 'exec RuntimeSupport.dll'
                    }
                }
            };
            fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

            const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'MyClassLibFunction.dll');
            const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

            const launchConfig: ProjectLaunchConfiguration = {
                type: 'project',
                project_path: projectPath
            };

            const debugConfig: AspireResourceExtendedDebugConfiguration = {
                runId: '1',
                debugSessionId: '1',
                type: 'coreclr',
                name: 'Test Debug Config',
                request: 'launch'
            };

            const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

            await assert.rejects(
                extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig),
                /Launch profile 'RunExe' uses commandName 'Executable' but does not specify an executablePath/);

            assert.strictEqual(dotNetService.buildDotNetProjectStub.called, false);
        } finally {
            fs.rmSync(tempDir, { recursive: true, force: true });
        }
    });

    test('expands environment variables in Executable profile executablePath and commandLineArgs', async () => {
        // Executable launch profiles may contain $(VAR) references (e.g. $(HOME)) that
        // VS expands natively but the coreclr debugger does not. The extension must expand
        // these before passing them to the debug configuration.
        const fs = require('fs');
        const os = require('os');
        const path = require('path');

        const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-test-'));
        const projectDir = path.join(tempDir, 'MyToolProject');
        const propertiesDir = path.join(projectDir, 'Properties');
        fs.mkdirSync(propertiesDir, { recursive: true });

        const projectPath = path.join(projectDir, 'MyToolProject.csproj');
        fs.writeFileSync(projectPath, '<Project></Project>');

        // Set up a test env var so expansion is deterministic
        const envVarName = 'ASPIRE_TEST_TOOL_ROOT';
        const envVarValue = '/opt/tools';
        process.env[envVarName] = envVarValue;

        const launchSettings = {
            profiles: {
                'Aspire_my-tool': {
                    commandName: 'Executable',
                    executablePath: '$(' + envVarName + ')/bin/dotnet',
                    commandLineArgs: 'exec --depsfile ./MyToolProject.deps.json $(' + envVarName + ')/lib/RuntimeSupport.dll MyToolProject::MyToolProject.Function::Handler',
                    workingDirectory: 'bin/Debug/net10.0/'
                }
            }
        };

        fs.writeFileSync(path.join(propertiesDir, 'launchSettings.json'), JSON.stringify(launchSettings, null, 2));

        const outputPath = path.join(projectDir, 'bin', 'Debug', 'net10.0', 'MyToolProject.dll');
        const { extension, dotNetService } = createDebuggerExtension(outputPath, null, true, true);

        const launchConfig: ProjectLaunchConfiguration = {
            type: 'project',
            project_path: projectPath,
            launch_profile: 'Aspire_my-tool'
        };

        const debugConfig: AspireResourceExtendedDebugConfiguration = {
            runId: '1',
            debugSessionId: '1',
            type: 'coreclr',
            name: 'Test Debug Config',
            request: 'launch'
        };

        const fakeAspireDebugSession = sinon.createStubInstance(AspireDebugSession);

        await extension.createDebugSessionConfigurationCallback!(launchConfig, undefined, [], { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession }, debugConfig);

        // executablePath should have $(VAR) expanded
        assert.strictEqual(debugConfig.program, `${envVarValue}/bin/dotnet`);

        // commandLineArgs should also have $(VAR) expanded
        assert.strictEqual(debugConfig.args, `exec --depsfile ./MyToolProject.deps.json ${envVarValue}/lib/RuntimeSupport.dll MyToolProject::MyToolProject.Function::Handler`);

        // cleanup
        delete process.env[envVarName];
        fs.rmSync(tempDir, { recursive: true, force: true });
    });
});
