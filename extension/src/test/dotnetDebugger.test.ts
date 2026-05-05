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
        return Promise.resolve('');
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
