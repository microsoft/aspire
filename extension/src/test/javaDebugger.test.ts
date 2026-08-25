import * as path from 'path';
import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { getSupportedCapabilities } from '../capabilities';
import { AspireDebugSession, getLoggableDebugConfiguration } from '../debugger/AspireDebugSession';
import * as debuggerExtensionsModule from '../debugger/debuggerExtensions';
import { getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { javaDebuggerExtension, parseJavaAppHostCommand, resolveJavaClassPaths } from '../debugger/languages/java';
import { javaAppHostCommandNotRecognized, javaDebuggerExtensionNotInstalled } from '../loc/strings';
import { AspireResourceExtendedDebugConfiguration, GoLaunchConfiguration, JavaLaunchConfiguration } from '../dcp/types';

suite('Java Debugger Extension Tests', () => {
    const fakeAspireDebugSession = {} as AspireDebugSession;

    teardown(() => sinon.restore());

    test('advertises Java support when both Java extensions are installed', () => {
        stubInstalledExtensions(['redhat.java', 'vscjava.vscode-java-debug']);

        const capabilities = getSupportedCapabilities();
        assert.ok(capabilities.includes('java'));
        assert.ok(capabilities.includes('vscjava.vscode-java-debug'));
        assert.ok(getResourceDebuggerExtensions().some(extension => extension.resourceType === 'java'));
    });

    test('does not advertise Java support when only the debug adapter is installed', () => {
        stubInstalledExtensions(['vscjava.vscode-java-debug']);

        const capabilities = getSupportedCapabilities();
        assert.ok(!capabilities.includes('java'));
        assert.ok(!capabilities.includes('vscjava.vscode-java-debug'));
        assert.ok(!getResourceDebuggerExtensions().some(extension => extension.resourceType === 'java'));
    });

    test('does not advertise Java support when only the language server is installed', () => {
        stubInstalledExtensions(['redhat.java']);

        const capabilities = getSupportedCapabilities();
        assert.ok(!capabilities.includes('java'));
        assert.ok(!getResourceDebuggerExtensions().some(extension => extension.resourceType === 'java'));
    });

    test('configures the VS Code Java debugger from the launch configuration', async () => {
        const launchConfig: JavaLaunchConfiguration = {
            type: 'java',
            request: 'launch',
            working_directory: '/workspace/api',
            main_class: 'com.example.api.Application',
            build_tool: 'maven'
        };
        const debugConfig = createDebugConfig();
        stubInstalledExtensions([]);

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            launchConfig,
            ['--server.port=8080'],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.type, 'java');
        assert.strictEqual(debugConfig.request, 'launch');
        assert.strictEqual(debugConfig.cwd, '/workspace/api');
        assert.strictEqual(debugConfig.mainClass, 'com.example.api.Application');
        assert.deepStrictEqual(debugConfig.args, ['--server.port=8080']);
        assert.strictEqual(debugConfig.noDebug, false);
    });

    test('sets noDebug when launch option disables debugging', async () => {
        const debugConfig = createDebugConfig();

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig(),
            [],
            [],
            { debug: false, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.noDebug, true);
    });

    test('rejects an attach request instead of sending the adapter a configuration it cannot use', async () => {
        // An attach session needs a host and a port, and nothing in the wire schema carries them, so
        // an attach configuration built here would always be rejected by vscjava.vscode-java-debug
        // with a message that points at the adapter rather than at the app host. Failing here instead
        // names the real problem.
        const debugConfig = createDebugConfig();

        await assert.rejects(
            () => javaDebuggerExtension.createDebugSessionConfigurationCallback!(
                createJavaLaunchConfig({ request: 'attach' }),
                [],
                [],
                { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
                debugConfig),
            /attach/i);
    });

    test('defaults to a launch request when the launch configuration omits one', async () => {
        const debugConfig = createDebugConfig({ request: 'attach' });

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ request: undefined }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.request, 'launch');
    });

    test('puts a prebuilt JAR on the classpath rather than passing it as the main class', async () => {
        const debugConfig = createDebugConfig();

        // The adapter documents mainClass as a fully qualified class name or a .java path, so it
        // never opens an archive. The app host therefore reads Main-Class from the manifest itself
        // and sends the JAR as a classpath entry; passing the archive as mainClass left the adapter
        // unable to resolve an entry point at all.
        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({
                main_class: 'com.example.api.Application',
                class_paths: ['/workspace/api/target/api-1.0.0.jar']
            }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.mainClass, 'com.example.api.Application');
        assert.deepStrictEqual(debugConfig.classPaths, ['/workspace/api/target/api-1.0.0.jar']);
    });

    test('omits classPaths so the adapter resolves them from the project when none are reported', async () => {
        const debugConfig = createDebugConfig();

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ class_paths: undefined }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(!('classPaths' in debugConfig), 'classPaths must stay unset so the Java debugger resolves the project classpath.');
    });

    test('omits mainClass so the adapter resolves it when the app host does not report one', async () => {
        const debugConfig = createDebugConfig();

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ main_class: undefined }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(!('mainClass' in debugConfig), 'mainClass must stay unset so the Java debugger resolves it from the project.');
    });

    test('scopes entry point resolution to the reported project when no main class is known', async () => {
        // Without projectName the adapter searches every project in the workspace, so a solution with
        // several Java resources makes it prompt the user to pick a main class on every launch.
        const debugConfig = createDebugConfig();

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ main_class: undefined, project_name: 'catalog' }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.projectName, 'catalog');
        assert.ok(!('mainClass' in debugConfig), 'mainClass must stay unset so the adapter resolves it within the named project.');
    });

    test('scopes a known main class to the reported project so it cannot resolve ambiguously', async () => {
        // Regression: with mainClass alone the adapter searches every project in the workspace and
        // fails with "Main class 'com.example.catalog.CatalogApplication' isn't unique in the
        // workspace" as soon as the class is visible through two projects, which happens whenever a
        // directory is covered both by its own build file and by another project's source root.
        const debugConfig = createDebugConfig();

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ main_class: 'com.example.api.Application', project_name: 'catalog' }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.mainClass, 'com.example.api.Application');
        assert.strictEqual(debugConfig.projectName, 'catalog');
    });

    test('forwards application arguments and defaults them to an empty array', async () => {
        const withArgs = createDebugConfig();
        const withoutArgs = createDebugConfig();
        const launchOptions = { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession };

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig(), ['--spring.profiles.active=dev', 'extra'], [], launchOptions, withArgs);
        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig(), undefined, [], launchOptions, withoutArgs);

        assert.deepStrictEqual(withArgs.args, ['--spring.profiles.active=dev', 'extra']);
        assert.deepStrictEqual(withoutArgs.args, []);
    });

    test('preserves the merged environment instead of replacing it with the resource variables', async () => {
        // prepareDebugSession already merged the inherited process environment with the resource's
        // variables. Overwriting env here would drop PATH and JAVA_HOME, so the JVM could not start.
        const debugConfig = createDebugConfig({
            env: { PATH: '/usr/bin', JAVA_HOME: '/opt/jdk-21', OTEL_SERVICE_NAME: 'api' }
        });

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig(),
            [],
            [{ name: 'OTEL_SERVICE_NAME', value: 'api' }, { name: 'ASPIRE_RESOURCE', value: 'api' }],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.deepStrictEqual(debugConfig.env, { PATH: '/usr/bin', JAVA_HOME: '/opt/jdk-21', OTEL_SERVICE_NAME: 'api' });
    });

    test('launches when the Java language server is not installed', async () => {
        stubInstalledExtensions([]);
        const debugConfig = createDebugConfig();

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ build_tool: 'gradle' }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(debugConfig.type, 'java');
        assert.strictEqual(debugConfig.mainClass, 'com.example.api.Application');
    });

    test('launches when the project configuration refresh command rejects', async () => {
        stubJavaLanguageServer(true);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').rejects(new Error("command 'java.execute.workspaceCommand' not found"));
        const debugConfig = createDebugConfig();

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ build_tool: 'maven' }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.ok(executeCommand.called, 'The refresh should be attempted when the language server reports ready.');
        assert.strictEqual(debugConfig.type, 'java');
        assert.strictEqual(debugConfig.cwd, '/workspace/api');
    });

    test('skips the project configuration refresh when the language server is not ready', async () => {
        stubJavaLanguageServer(false);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const debugConfig = createDebugConfig();

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ build_tool: 'maven' }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(executeCommand.called, false);
        assert.strictEqual(debugConfig.type, 'java');
    });

    test('skips the project configuration refresh when the resource has no build tool', async () => {
        stubJavaLanguageServer(true);
        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const debugConfig = createDebugConfig();

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ build_tool: undefined }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        assert.strictEqual(executeCommand.called, false);
        assert.strictEqual(debugConfig.type, 'java');
    });

    test('stops waiting for the language server when the debug session is already stopping', async () => {
        // The readiness wait is the only slow step on the launch path. Without an abort it runs for the
        // full timeout, so a stop issued while a Java resource is starting is held up for ~30 seconds.
        sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
            if (extensionId !== 'redhat.java') {
                return undefined;
            }

            return {
                id: extensionId,
                isActive: true,
                exports: { serverMode: 'Standard', serverReady: () => new Promise<boolean>(() => { }) }
            } as unknown as vscode.Extension<unknown>;
        });

        const executeCommand = sinon.stub(vscode.commands, 'executeCommand').resolves(undefined);
        const debugConfig = createDebugConfig();
        const stoppingSession = { isStopAttemptInProgress: true } as AspireDebugSession;

        const startedAt = Date.now();
        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig({ build_tool: 'maven' }),
            [],
            [],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: stoppingSession },
            debugConfig);

        const elapsed = Date.now() - startedAt;
        assert.ok(elapsed < 5000, `expected the readiness wait to be abandoned promptly, but it took ${elapsed}ms`);
        assert.strictEqual(executeCommand.called, false);
        assert.strictEqual(debugConfig.type, 'java');
    });

    test('throws for a launch configuration that is not java', () => {
        const goLaunchConfig: GoLaunchConfiguration = { type: 'go', program: '/workspace/api' };

        assert.throws(
            () => javaDebuggerExtension.getProjectFile(goLaunchConfig),
            /Invalid launch configuration/);
    });

    test('returns the working directory as the project file', () => {
        assert.strictEqual(javaDebuggerExtension.getProjectFile(createJavaLaunchConfig()), '/workspace/api');
    });

    test('names the session after the main class when it is a class name', () => {
        assert.strictEqual(javaDebuggerExtension.getDisplayName(createJavaLaunchConfig()), 'Java: com.example.api.Application');
    });

    test('names the session with a workspace relative path rather than a file URI', () => {
        sinon.stub(vscode.workspace, 'asRelativePath').callsFake(() => 'api');

        const fromSourceFile = javaDebuggerExtension.getDisplayName(createJavaLaunchConfig({ main_class: '/workspace/api/src/main/java/Application.java' }));
        const withoutMainClass = javaDebuggerExtension.getDisplayName(createJavaLaunchConfig({ main_class: undefined }));

        assert.strictEqual(fromSourceFile, 'Java: api');
        assert.strictEqual(withoutMainClass, 'Java: api');
    });

    test('falls back to the Java label when there is nothing to name the session after', () => {
        const emptyLaunchConfig: JavaLaunchConfiguration = { type: 'java' };

        assert.strictEqual(javaDebuggerExtension.getDisplayName(emptyLaunchConfig), 'Java');
    });

    test('supports Java source files', () => {
        assert.deepStrictEqual(javaDebuggerExtension.getSupportedFileTypes(), ['.java']);
    });

    test('stops a Java AppHost launch with install guidance when the debug adapter is not installed', async () => {
        // startAppHost builds the debugger descriptor directly instead of going through
        // getResourceDebuggerExtensions, so without its own gate VS Code fails the session with the raw
        // "configured debug type is not supported" rather than naming the extension to install.
        stubInstalledExtensions([]);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration');

        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/AppHost.java',
                command: 'run',
            },
        };

        const debugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { });
        sinon.stub(debugSession, 'createDebugAdapterTrackerCore');

        await debugSession.startAppHost(
            '/workspace/AppHost.java',
            ['java', '-cp', 'target/classes', 'AppHost'],
            [],
            true,
            { forceBuild: false });

        sinon.assert.notCalled(createDebugSessionConfiguration);
        sinon.assert.calledOnce(showErrorMessage);
        assert.strictEqual(
            showErrorMessage.firstCall.args[0],
            javaDebuggerExtensionNotInstalled(javaDebuggerExtension.extensionId!));
    });

    test('stops a Java AppHost launch when the command cannot be parsed into a launch configuration', async () => {
        // Guessing here would start a JVM with the wrong arguments, so an unrecognised command has to
        // fail loudly rather than be approximated.
        stubInstalledExtensions(['redhat.java', 'vscjava.vscode-java-debug']);
        const showErrorMessage = sinon.stub(vscode.window, 'showErrorMessage');
        sinon.stub(vscode.debug, 'stopDebugging').resolves();
        const createDebugSessionConfiguration = sinon.stub(debuggerExtensionsModule, 'createDebugSessionConfiguration');

        const parentDebugSession = {
            id: 'aspire-session',
            type: 'aspire',
            name: 'Aspire',
            configuration: {
                type: 'aspire',
                request: 'launch',
                name: 'Aspire',
                program: '/workspace/AppHost.java',
                command: 'run',
            },
        };

        const debugSession = new AspireDebugSession(
            parentDebugSession as unknown as vscode.DebugSession,
            {} as any,
            {} as any,
            {} as any,
            () => { });
        sinon.stub(debugSession, 'createDebugAdapterTrackerCore');

        await debugSession.startAppHost('/workspace/AppHost.java', ['java', '-Xmx512m'], [], true, { forceBuild: false });

        sinon.assert.notCalled(createDebugSessionConfiguration);
        sinon.assert.calledOnce(showErrorMessage);
        assert.strictEqual(
            showErrorMessage.firstCall.args[0],
            javaAppHostCommandNotRecognized());
    });

    test('always redacts resolved Java environments from persistent configuration logs', async () => {
        const credential = 'resolved-environment-credential';
        const debugConfig = createDebugConfig();
        debugConfig.env = {
            PRIVATE_TOKEN: credential,
            OTEL_EXPORTER_OTLP_HEADERS: `x-otlp-api-key=${credential}`,
        };

        await javaDebuggerExtension.createDebugSessionConfigurationCallback!(
            createJavaLaunchConfig(),
            [],
            [{ name: 'PRIVATE_TOKEN', value: credential }],
            { debug: true, runId: '1', debugSessionId: '1', isApphost: false, debugSession: fakeAspireDebugSession },
            debugConfig);

        // `aspire.enableDebugConfigEnvironmentLogging` defaults to true, so `true` here is the default
        // path rather than an opt-in, and the resolved environment must still be withheld.
        const loggableConfig = getLoggableDebugConfiguration(debugConfig, true);
        assert.strictEqual(loggableConfig.env, '<redacted>');
        assert.ok(!JSON.stringify(loggableConfig).includes(credential));
    });
});

function createJavaLaunchConfig(overrides: Partial<JavaLaunchConfiguration> = {}): JavaLaunchConfiguration {
    return {
        type: 'java',
        request: 'launch',
        working_directory: '/workspace/api',
        main_class: 'com.example.api.Application',
        build_tool: 'maven',
        ...overrides
    };
}

function createDebugConfig(overrides: Partial<AspireResourceExtendedDebugConfiguration> = {}): AspireResourceExtendedDebugConfiguration {
    return {
        runId: '1',
        debugSessionId: '1',
        type: 'java',
        name: 'Java',
        request: 'launch',
        program: '/workspace/api',
        args: [],
        ...overrides
    };
}

function stubInstalledExtensions(extensionIds: string[]): void {
    sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
        return extensionIds.includes(extensionId) ? { id: extensionId } as vscode.Extension<unknown> : undefined;
    });
}

// Stands in for the redhat.java extension API that java.ts waits on before refreshing the classpath.
function stubJavaLanguageServer(serverReady: boolean): void {
    sinon.stub(vscode.extensions, 'getExtension').callsFake((extensionId: string) => {
        if (extensionId !== 'redhat.java') {
            return undefined;
        }

        return {
            id: extensionId,
            isActive: true,
            exports: { serverMode: 'Standard', serverReady: async () => serverReady }
        } as unknown as vscode.Extension<unknown>;
    });
}

suite('Java AppHost Command Parsing Tests', () => {
    // The CLI launches every Java AppHost toolchain as a plain JVM invocation, so the extension can
    // recover the main class and classpath from the command line rather than re-deriving the layout.
    test('parses the javac toolchain command', () => {
        const parsed = parseJavaAppHostCommand(['java', '-cp', '.java-build', 'AppHost', '--operation', 'run']);

        assert.deepStrictEqual(parsed, {
            mainClass: 'AppHost',
            classPaths: ['.java-build'],
            vmArgs: [],
            appHostArgs: ['--operation', 'run']
        });
    });

    test('splits a multi-entry classpath on the platform delimiter', () => {
        const classPath = ['target/classes', 'target/aspire-deps/*'].join(path.delimiter);
        const parsed = parseJavaAppHostCommand(['java', '-cp', classPath, 'AppHost']);

        assert.deepStrictEqual(parsed?.classPaths, ['target/classes', 'target/aspire-deps/*']);
        assert.deepStrictEqual(parsed?.appHostArgs, []);
    });

    test('accepts the -classpath and --class-path aliases', () => {
        for (const option of ['-classpath', '--class-path']) {
            const parsed = parseJavaAppHostCommand(['java', option, 'build/classes/java/main', 'AppHost']);
            assert.deepStrictEqual(parsed?.classPaths, ['build/classes/java/main'], option);
        }
    });

    test('keeps JVM options separate from the AppHost arguments', () => {
        const parsed = parseJavaAppHostCommand(['java', '-Xmx512m', '-cp', 'out', 'AppHost', '-Dnot.a.vm.arg']);

        assert.deepStrictEqual(parsed?.vmArgs, ['-Xmx512m']);
        assert.deepStrictEqual(parsed?.appHostArgs, ['-Dnot.a.vm.arg']);
    });

    test('does not mistake a separated option value for the main class', () => {
        // These options take their value as the *next* argument. Treating the value as a bare token
        // makes it the main class, and the adapter then launches something the user never asked for
        // without reporting anything wrong.
        const cases: [string[], string][] = [
            [['java', '--add-opens', 'java.base/java.lang=ALL-UNNAMED', '-cp', 'out', 'AppHost'], '--add-opens'],
            [['java', '--add-exports', 'java.base/sun.nio.ch=ALL-UNNAMED', '-cp', 'out', 'AppHost'], '--add-exports'],
            [['java', '--patch-module', 'java.base=patches', '-cp', 'out', 'AppHost'], '--patch-module']
        ];

        for (const [args, option] of cases) {
            assert.strictEqual(parseJavaAppHostCommand(args)?.mainClass, 'AppHost', option);
        }
    });

    test('keeps a separated option and its value together in the JVM arguments', () => {
        const parsed = parseJavaAppHostCommand(['java', '--add-opens', 'java.base/java.lang=ALL-UNNAMED', '-cp', 'out', 'AppHost']);

        assert.deepStrictEqual(parsed?.vmArgs, ['--add-opens', 'java.base/java.lang=ALL-UNNAMED']);
    });

    test('returns null for a JAR launch, whose entry point lives in the archive manifest', () => {
        // `java -jar app.jar` resolves Main-Class from the manifest, so no class appears on the
        // command line. The adapter documents mainClass as a class name or .java path and never opens
        // an archive, so handing it "app.jar" fails the launch with ClassNotFoundException.
        assert.strictEqual(parseJavaAppHostCommand(['java', '-jar', 'app.jar', '--operation', 'run']), null);
        assert.strictEqual(parseJavaAppHostCommand(['java', '-cp', 'out', '-jar', 'app.jar']), null);
    });

    test('returns null when a separated option is missing its value', () => {
        // The option consumes the token that would otherwise be the main class, leaving nothing to
        // launch rather than a class named "--add-opens".
        assert.strictEqual(parseJavaAppHostCommand(['java', '--add-opens']), null);
    });

    test('returns null when the command is not a recognizable JVM launch', () => {
        assert.strictEqual(parseJavaAppHostCommand([]), null);
        assert.strictEqual(parseJavaAppHostCommand(['java']), null);
        // Only options, so there is no main class to attach the debugger to.
        assert.strictEqual(parseJavaAppHostCommand(['java', '-Xmx512m']), null);
        // A classpath option with no value would otherwise consume the main class.
        assert.strictEqual(parseJavaAppHostCommand(['java', '-cp']), null);
    });

    test('returns null for a build-tool wrapper invocation', () => {
        // The wrapper forks its own JVM, so "exec:java" is a Maven goal rather than a main class.
        assert.strictEqual(parseJavaAppHostCommand(['./mvnw', 'exec:java']), null);
        assert.strictEqual(parseJavaAppHostCommand(['./gradlew', 'run']), null);
        assert.strictEqual(parseJavaAppHostCommand(['cmd.exe', '/c', 'mvnw.cmd', 'exec:java']), null);
    });

    test('accepts a launcher referenced by an absolute path', () => {
        const parsed = parseJavaAppHostCommand([path.join('/opt', 'jdk', 'bin', 'java'), '-cp', 'out', 'AppHost']);

        assert.strictEqual(parsed?.mainClass, 'AppHost');
    });

    // The CLI writes the classpath relative to the AppHost directory it runs `java` from, and the
    // debug adapter resolves a relative entry somewhere else, so the JVM it starts has no AppHost
    // class on its classpath and dies with ClassNotFoundException before Aspire connects to it.
    test('resolves a relative AppHost classpath against the AppHost directory', () => {
        const appHostDirectory = absoluteTestPath('repo', 'JavaSpringBoot.AppHost.Java');

        assert.deepStrictEqual(
            resolveJavaClassPaths(['build/classes/java/main', 'build/aspire-deps/*'], appHostDirectory),
            [
                path.join(appHostDirectory, 'build', 'classes', 'java', 'main'),
                path.join(appHostDirectory, 'build', 'aspire-deps', '*')
            ]);
    });

    test('leaves an absolute AppHost classpath entry alone', () => {
        const appHostDirectory = absoluteTestPath('repo', 'JavaSpringBoot.AppHost.Java');
        const absoluteEntry = absoluteTestPath('elsewhere', 'classes');

        assert.deepStrictEqual(resolveJavaClassPaths([absoluteEntry], appHostDirectory), [absoluteEntry]);
    });
});

/**
 * Builds a fully qualified path for a test fixture.
 *
 * `path.join(path.sep, 'repo')` looks absolute and even satisfies `path.isAbsolute`, but on Windows it
 * produces the drive-relative `\repo` rather than a fully qualified path. `path.resolve` — which is what
 * the production code under test uses — completes that against the current drive and returns `D:\repo`,
 * so a fixture built with `join` and an expectation built from the same fixture disagree by a drive
 * letter and the test only fails on Windows. `path.resolve` fully qualifies on both platforms, which
 * keeps the fixture and the code under test in the same shape.
 */
function absoluteTestPath(...segments: string[]): string {
    return path.resolve(path.sep, ...segments);
}
