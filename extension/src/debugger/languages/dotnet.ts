import * as vscode from 'vscode';
import { extensionLogOutputChannel } from '../../utils/logging';
import { noCsharpBuildTask, buildFailedWithExitCode, noOutputFromMsbuild, failedToGetTargetPath, invalidLaunchConfiguration, buildFailedForProjectWithError, processExitedWithCode, lookingForDevkitBuildTask, csharpDevKitNotInstalled, failedToInspectRuntimeConfig, dotNetRunFallbackDisablesDebugger, dotNetRunFileBasedExecutableProfileFallback, executableLaunchProfileMissingExecutablePath, explicitLaunchProfileNotResolved, launchProfileUnsupportedCommandName, launchProfileHasInvalidProperties } from '../../loc/strings';
import { ChildProcessWithoutNullStreams, execFile, spawn } from 'child_process';
import * as util from 'util';
import * as path from 'path';
import * as readline from 'readline';
import * as os from 'os';
import * as fs from 'fs';
import { csharpExtensionId } from '../../capabilities';
import { doesFileExist } from '../../utils/io';
import { AspireResourceExtendedDebugConfiguration, DebugConfigurationArguments, EnvVar, ExecutableLaunchConfiguration, isProjectLaunchConfiguration, LaunchOptions, ProjectLaunchConfiguration } from '../../dcp/types';
import { ResourceDebuggerExtension } from '../debuggerExtensions';
import {
    readLaunchSettings,
    determineBaseLaunchProfile,
    determineDefaultLaunchProfile,
    mergeEnvironmentVariables,
    determineArguments,
    determineWorkingDirectory,
    LaunchProfileCommandName,
    LaunchProfile,
    LaunchSettings,
    expandEnvironmentVariables,
    expandSdkEnvironmentVariables,
    hasSdkCompatibleLaunchProfileProperties
} from '../launchProfiles';
import { AspireDebugSession } from '../AspireDebugSession';
import { createResolvedAspireCliPathProcessEnvironment } from '../../utils/cliPathEnvironment';
import { resolveCliPath } from '../../utils/cliPath';
import { getCliPathTargetForUri } from '../../utils/cliPathVariables';
import { getHotReloadDiagnostics, logHotReloadDiagnostics, showHotReloadDisabledAdvisoryIfNeeded } from '../hotReload';
import { AppHostBuildFailureError } from '../appHostBuildFailureError';
import { deleteEnvironmentVariable, getEnvironmentForChildProcess, setEnvironmentVariable } from '../../utils/environment';
import { getAppHostLaunchProfileOptions } from '../../utils/launchProfile';

interface IDotNetService {
    getAndActivateDevKit(): Promise<boolean>
    buildDotNetProject(projectFile: string): Promise<void>;
    getDotNetProjectLaunchProperties(projectFile: string): Promise<DotNetProjectLaunchProperties>;
    getDotNetTargetPath(projectFile: string): Promise<string>;
    getDotNetRunApiOutput(projectFile: string, environment?: NodeJS.ProcessEnv): Promise<string>;
}

interface DotNetProjectLaunchProperties {
    targetPath: string;
    runCommand?: string;
    useAppHost: boolean;
    useWinUI: boolean;
    windowsPackageType?: string;
}

export class DotNetService implements IDotNetService {
    private _debugSession: AspireDebugSession;

    constructor(debugSession: AspireDebugSession) {
        this._debugSession = debugSession;
    }

    execFileAsync = util.promisify(execFile);

    writeToDebugConsole(message: string, category: 'stdout' | 'stderr', addNewLine: boolean = false): void {
        this._debugSession.sendMessage(message, addNewLine, category);
    }

    async getAndActivateDevKit(): Promise<boolean> {
        const csharpDevKit = vscode.extensions.getExtension('ms-dotnettools.csdevkit');
        if (!csharpDevKit) {
            // If c# dev kit is not installed, we will have already built this project on the command line using the Aspire CLI
            // thus we should just immediately return
            return Promise.resolve(false);
        }

        if (!csharpDevKit.isActive) {
            extensionLogOutputChannel.info('Activating C# Dev Kit extension...');
            await csharpDevKit.activate();
        }

        return Promise.resolve(true);
    }

    async buildDotNetProject(projectFile: string): Promise<void> {
        return new Promise<void>((resolve, reject) => {
            extensionLogOutputChannel.info(`Building .NET project: ${projectFile} using dotnet CLI`);

            const args = ['build', projectFile];

            void (async () => {
                const { cliPath } = await resolveCliPath(getCliPathTargetForUri(vscode.Uri.file(projectFile)));
                const buildProcess = spawn('dotnet', args, {
                    // The .NET SDK searches for global.json from the process working directory, not the
                    // project argument. Run from the project directory so extension and CLI builds select
                    // the same SDK and repository configuration.
                    cwd: path.dirname(projectFile),
                    env: createResolvedAspireCliPathProcessEnvironment(cliPath)
                });

                let stdoutOutput = '';
                let stderrOutput = '';

                // Stream stdout in real-time
                buildProcess.stdout?.on('data', (data: Buffer) => {
                    const output = data.toString();
                    stdoutOutput += output;
                    this.writeToDebugConsole(output, 'stdout');
                });

                // Stream stderr in real-time
                buildProcess.stderr?.on('data', (data: Buffer) => {
                    const output = data.toString();
                    stderrOutput += output;
                    this.writeToDebugConsole(output, 'stderr');
                });

                buildProcess.on('error', (err) => {
                    extensionLogOutputChannel.error(`dotnet build process error: ${err}`);
                    // A spawn failure produces no build transcript, so the message has not been
                    // streamed to the debug console yet and must still be surfaced by the caller.
                    reject(new AppHostBuildFailureError(
                        buildFailedForProjectWithError(projectFile, err.message),
                        false));
                });

                buildProcess.on('close', (code) => {
                    if (code === 0) {
                        // if build succeeds, simply return. otherwise throw to trigger error handling
                        if (stderrOutput) {
                            reject(new AppHostBuildFailureError(stderrOutput, true));
                        } else {
                            resolve();
                        }
                    } else {
                        reject(new AppHostBuildFailureError(
                            buildFailedForProjectWithError(projectFile, stdoutOutput || stderrOutput || `Exit code ${code}`),
                            true));
                    }
                });
            })().catch(reject);
        });
    }

    async getDotNetTargetPath(projectFile: string): Promise<string> {
        return (await this.getDotNetProjectLaunchProperties(projectFile)).targetPath;
    }

    async getDotNetProjectLaunchProperties(projectFile: string): Promise<DotNetProjectLaunchProperties> {
        const args = [
            'msbuild',
            projectFile,
            '-nologo',
            '-getProperty:TargetPath,RunCommand,UseAppHost,UseWinUI,WindowsPackageType',
            '-v:q',
            '-property:GenerateFullPaths=true'
        ];
        try {
            const { cliPath } = await resolveCliPath(getCliPathTargetForUri(vscode.Uri.file(projectFile)));
            const { stdout } = await this.execFileAsync('dotnet', args, {
                cwd: path.dirname(projectFile),
                encoding: 'utf8',
                env: createResolvedAspireCliPathProcessEnvironment(cliPath)
            });
            const output = stdout.trim();
            if (!output) {
                throw new Error(noOutputFromMsbuild);
            }

            // Asking MSBuild for multiple properties produces:
            //   { "Properties": { "TargetPath": "...dll", "RunCommand": "...exe", "UseAppHost": "true", ... } }
            // Property values are strings even when the evaluated MSBuild value is boolean.
            const parsed = JSON.parse(output) as { Properties?: Record<string, unknown> };
            const properties = parsed.Properties;
            if (!properties || typeof properties.TargetPath !== 'string' || properties.TargetPath.length === 0) {
                throw new Error(noOutputFromMsbuild);
            }

            return {
                targetPath: properties.TargetPath,
                runCommand: typeof properties.RunCommand === 'string' && properties.RunCommand.length > 0
                    ? properties.RunCommand
                    : undefined,
                useAppHost: isTrueMsbuildProperty(properties.UseAppHost),
                useWinUI: isTrueMsbuildProperty(properties.UseWinUI),
                windowsPackageType: typeof properties.WindowsPackageType === 'string'
                    ? properties.WindowsPackageType
                    : undefined
            };
        } catch (err) {
            throw new Error(failedToGetTargetPath(String(err)));
        }
    }

    async getDotNetRunApiOutput(projectPath: string, environment?: NodeJS.ProcessEnv): Promise<string> {
        const { cliPath } = await resolveCliPath(getCliPathTargetForUri(vscode.Uri.file(projectPath)));
        let childProcess: ChildProcessWithoutNullStreams | undefined;

        return new Promise<string>((resolve, reject) => {
            const timeout = setTimeout(() => {
                childProcess?.kill();
                reject(new Error('Timeout while waiting for dotnet run-api response'));
            }, 10_000);

            try {
                extensionLogOutputChannel.info('dotnet run-api - starting process');

                childProcess = spawn('dotnet', ['run-api'], {
                    cwd: path.dirname(projectPath),
                    env: createResolvedAspireCliPathProcessEnvironment(cliPath, { ...process.env, ...environment }),
                    stdio: ['pipe', 'pipe', 'pipe']
                });

                childProcess.on('error', reject);
                childProcess.on('exit', (code, signal) => {
                    clearTimeout(timeout);
                    if (code !== 0) {
                        reject(new Error(processExitedWithCode(code?.toString() ?? "unknown")));
                    }
                });

                const rl = readline.createInterface(childProcess.stdout);
                rl.on('line', line => {
                    clearTimeout(timeout);
                    extensionLogOutputChannel.info(`dotnet run-api - received: ${line}`);
                    resolve(line);
                });

                const message = JSON.stringify({ ['$type']: 'GetRunCommand', ['EntryPointFileFullPath']: projectPath });
                extensionLogOutputChannel.info(`dotnet run-api - sending: ${message}`);
                childProcess.stdin.write(message + os.EOL);
                childProcess.stdin.end();
            } catch (e) {
                clearTimeout(timeout);
                reject(e);
            }
        }).finally(() => childProcess?.removeAllListeners());
    }
}

export function isFileBasedApp(projectPath: string): boolean {
    return path.extname(projectPath).toLowerCase().endsWith('.cs');
}

function isTrueMsbuildProperty(value: unknown): boolean {
    return typeof value === 'string' && value.trim().toLowerCase() === 'true';
}

function getProjectDebugProgram(properties: DotNetProjectLaunchProperties): string {
    const runCommand = properties.runCommand?.trim();
    if (process.platform === 'win32' &&
        properties.useAppHost &&
        properties.useWinUI &&
        properties.windowsPackageType?.trim().toLowerCase() === 'none' &&
        runCommand &&
        path.win32.isAbsolute(runCommand) &&
        path.win32.extname(runCommand).toLowerCase() === '.exe') {
        // An unpackaged WinUI app must retain the generated apphost executable as its process image.
        // Launching TargetPath instead makes vsdbg host the assembly in dotnet.exe, which crashes in
        // Microsoft.UI.Xaml.Application.Start with STATUS_STOWED_EXCEPTION.
        // See https://github.com/microsoft/aspire/issues/19091.
        return runCommand;
    }

    return properties.targetPath;
}

interface RunApiOutput {
    executablePath: string;
    commandLineArguments: string;
    env?: { [key: string]: string };
}

function getRunApiConfigFromOutput(runApiOutput: string): RunApiOutput {
    const parsed = JSON.parse(runApiOutput);
    if (parsed.$type === 'Error') {
        throw new Error(`dotnet run-api failed: ${parsed.Message}`);
    }
    else if (parsed.$type !== 'RunCommand') {
        throw new Error(`dotnet run-api failed: Unexpected response type '${parsed.$type}'`);
    }

    return {
        executablePath: parsed.ExecutablePath,
        commandLineArguments: parsed.CommandLineArguments,
        env: parsed.EnvironmentVariables
    };
}

function isDotnetLauncher(executablePath: string): boolean {
    // If the command is "dotnet", but with a full path, it is not the SDK-injected dotnet launcher,
    // but a user program that just happens to be named "dotnet".
    if (path.dirname(executablePath) !== '.') {
        return false;
    }

    const executableName = path.basename(executablePath).toLowerCase();
    return executableName === 'dotnet' || executableName === 'dotnet.exe';
}

// DOTNET_ROOT and its architecture-specific variants (e.g. DOTNET_ROOT_X64, DOTNET_ROOT_ARM64) that the SDK
// injects so a launched program can locate the .NET runtime.
const dotnetRootEnvironmentVariablePattern = new RegExp(
    '^DOTNET_ROOT(_[A-Z0-9]+)?$',
    process.platform === 'win32' ? 'i' : undefined);

// Returns .NET host environment variables from the given environment, minus any in the excluded set.
function pickRuntimeHostEnvironment(
    env: { [key: string]: string } | undefined,
    excluded: Set<string>
): { [key: string]: string } | undefined {
    if (!env) {
        return undefined;
    }

    const runtimeHostEnv: { [key: string]: string } = {};
    for (const [name, value] of Object.entries(env)) {
        if (!dotnetRootEnvironmentVariablePattern.test(name)) {
            continue;
        }

        if (excluded.has(name.toUpperCase())) {
            continue;
        }

        runtimeHostEnv[name] = value;
    }

    return Object.keys(runtimeHostEnv).length > 0 ? runtimeHostEnv : undefined;
}

function collectProfileDotnetHostEnvVarNames(profile: LaunchProfile | null | undefined): Set<string> {
    const names = new Set<string>();
    for (const name of Object.keys(profile?.environmentVariables ?? {})) {
        if (dotnetRootEnvironmentVariablePattern.test(name)) {
            names.add(name.toUpperCase());
        }
    }

    return names;
}

function parseSdkSerializedArguments(argumentsText: string): string[] {
    // `dotnet run-api` exposes ProcessStartInfo.Arguments after the SDK serializes an argument array,
    // for example:
    //   exec "/workspace/output with spaces/apphost.dll"
    // Use the same CRT-compatible parser as src/Shared/CommandLineArgsParser.cs. That helper is copied
    // from System.Diagnostics.Process: Windows processes use these rules natively, while the .NET runtime
    // deliberately applies the same rules to ProcessStartInfo.Arguments before exec on Unix.
    // https://github.com/dotnet/runtime/blob/main/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs
    const parsedArguments: string[] = [];
    let index = 0;

    while (index < argumentsText.length) {
        while (index < argumentsText.length && (argumentsText[index] === ' ' || argumentsText[index] === '\t')) {
            index++;
        }

        if (index === argumentsText.length) {
            break;
        }

        let currentArgument = '';
        let inQuotes = false;

        while (index < argumentsText.length) {
            let backslashCount = 0;
            while (index < argumentsText.length && argumentsText[index] === '\\') {
                index++;
                backslashCount++;
            }

            if (backslashCount > 0) {
                if (index >= argumentsText.length || argumentsText[index] !== '"') {
                    currentArgument += '\\'.repeat(backslashCount);
                } else {
                    currentArgument += '\\'.repeat(Math.floor(backslashCount / 2));
                    if (backslashCount % 2 !== 0) {
                        currentArgument += '"';
                        index++;
                    }
                }

                continue;
            }

            const character = argumentsText[index];
            if (character === '"') {
                if (inQuotes && index < argumentsText.length - 1 && argumentsText[index + 1] === '"') {
                    currentArgument += '"';
                    index++;
                } else {
                    inQuotes = !inQuotes;
                }

                index++;
                continue;
            }

            if ((character === ' ' || character === '\t') && !inQuotes) {
                break;
            }

            currentArgument += character;
            index++;
        }

        parsedArguments.push(currentArgument);
    }

    return parsedArguments;
}

// Combine the SDK host arguments from `dotnet run-api` (the built app DLL that is passed to the `dotnet`
// launcher) with the user/launch-profile application arguments that were already resolved onto the debug
// configuration. `hostArguments` is present only when the program is the `dotnet` launcher; 
// for an apphost-executable build it is undefined and only the application arguments remain.
// The host arguments must come first because they identify what to run. Preserve the existing string form
// when application arguments are absent or launch-profile-authored text, but deserialize SDK host text when
// the application arguments are already tokens so the complete result can remain losslessly tokenized.
function combineRunApiArguments(hostArguments: string | undefined, applicationArguments: DebugConfigurationArguments | undefined): DebugConfigurationArguments | undefined {
    if (!hostArguments) {
        return applicationArguments;
    }

    if (applicationArguments === undefined) {
        return hostArguments;
    }

    if (Array.isArray(applicationArguments)) {
        return [...parseSdkSerializedArguments(hostArguments), ...applicationArguments];
    }

    return `${hostArguments} ${applicationArguments}`;
}

async function shouldLaunchProjectWithDotNetRun(outputPath: string): Promise<boolean> {
    if (path.extname(outputPath).toLowerCase() !== '.dll') {
        return false;
    }

    const runtimeConfigPath = outputPath.slice(0, -path.extname(outputPath).length) + '.runtimeconfig.json';
    try {
        const runtimeConfig = JSON.parse(await fs.promises.readFile(runtimeConfigPath, 'utf8'));
        const runtimeOptions = runtimeConfig?.runtimeOptions;

        // Blazor WebAssembly build output has a runtimeconfig.json without a
        // framework/frameworks entry, for example:
        //   { "runtimeOptions": { "tfm": "net10.0" } }
        // Launching that DLL directly makes the dotnet host treat it as a
        // self-contained app and fail before Aspire can observe the resource.
        return runtimeOptions !== undefined
            && runtimeOptions !== null
            && runtimeOptions.framework === undefined
            && runtimeOptions.frameworks === undefined;
    } catch (err) {
        if ((err as NodeJS.ErrnoException).code === 'ENOENT') {
            return false;
        }

        throw new Error(failedToInspectRuntimeConfig(outputPath, String(err)));
    }
}

export function quoteCommandLineArgument(argument: string): string {
    // Backslashes before a quote must be doubled so the command-line parser does not consume the
    // quote itself. The closing quote follows the same rule when the argument ends in backslashes.
    // https://learn.microsoft.com/cpp/c-language/parsing-c-command-line-arguments
    const escapedArgument = argument
        .replace(/(\\*)"/g, '$1$1\\"')
        .replace(/(\\+)$/, '$1$1');
    return `"${escapedArgument}"`;
}

function createDotNetRunBaseArguments(projectPath: string, fileBased: boolean, skipBuild: boolean = false, runWorkingDirectory?: string, suppressCliRunHook: boolean = false): string[] {
    // File-based resources use --no-cache to avoid stale SDK cache entries. When the CLI already built a
    // file-based AppHost, use --no-build so this fallback launches that output without rebuilding it.
    // Project files launch with `dotnet run --project <proj>`.
    const dotnetRunArgs = fileBased
        ? ['run', '--file', projectPath, skipBuild ? '--no-build' : '--no-cache', '--no-launch-profile']
        : ['run', '--project', projectPath, '--no-launch-profile'];

    if (suppressCliRunHook) {
        dotnetRunArgs.push('--property:_AspireSuppressCliRunHook=true');
    }

    if (runWorkingDirectory) {
        dotnetRunArgs.push(`--property:RunWorkingDirectory=${runWorkingDirectory}`);
    }

    return dotnetRunArgs;
}

function createDotNetRunArguments(projectPath: string, baseProfileArgs: string | undefined, runSessionArgs: string[] | undefined, fileBased: boolean = false, skipBuild: boolean = false, runWorkingDirectory?: string, suppressCliRunHook: boolean = false): string[] | string {
    const dotnetRunArgs = createDotNetRunBaseArguments(projectPath, fileBased, skipBuild, runWorkingDirectory, suppressCliRunHook);
    if (runSessionArgs !== undefined) {
        if (runSessionArgs.length > 0) {
            dotnetRunArgs.push('--', ...runSessionArgs);
        }

        return dotnetRunArgs;
    }

    if (baseProfileArgs) {
        // launchSettings.json stores application arguments as a command-line string, for example:
        //   --path "value with spaces" --flag
        // Preserve that string instead of reparsing it here so debugger command-line parsing
        // handles escaping consistently with normal project launches. Only the path token needs quoting.
        const quotedRunArgs = createDotNetRunBaseArguments(
            quoteCommandLineArgument(projectPath),
            fileBased,
            skipBuild,
            runWorkingDirectory ? quoteCommandLineArgument(runWorkingDirectory) : undefined,
            suppressCliRunHook);
        return `${quotedRunArgs.join(' ')} -- ${baseProfileArgs}`;
    }

    return dotnetRunArgs;
}

function expandDebugConfigurationArguments(argumentsValue: DebugConfigurationArguments | undefined): DebugConfigurationArguments | undefined {
    if (argumentsValue === undefined) {
        return undefined;
    }

    if (Array.isArray(argumentsValue)) {
        // Run-session arguments are already serialized argv tokens. Expanding them here would
        // reinterpret literal `$(NAME)` and `%NAME%` values that the AppHost intended to receive.
        return [...argumentsValue];
    }

    // Launch-profile arguments are authored as one command-line string and Visual Studio expands
    // their environment-variable references before starting an Executable profile.
    return expandEnvironmentVariables(argumentsValue);
}

function configureDotNetRunDebugConfiguration(
    debugConfiguration: AspireResourceExtendedDebugConfiguration,
    args: DebugConfigurationArguments,
    environment: NodeJS.ProcessEnv,
    processWorkingDirectory?: string): void {
    debugConfiguration.program = 'dotnet';
    debugConfiguration.args = args;
    // Unless the caller provides a separate process directory, keep the cwd already resolved from the
    // selected launch profile via determineWorkingDirectory (which falls back to the project directory
    // when the profile sets no workingDirectory). Because this fallback launches with --no-launch-profile,
    // `dotnet run` will not re-apply the profile's workingDirectory itself, so overwriting cwd here would
    // silently discard a custom profile workingDirectory and launch the app from the wrong directory.
    debugConfiguration.executablePath = undefined;
    debugConfiguration.noDebug = true;
    debugConfiguration.cwd = processWorkingDirectory ?? debugConfiguration.cwd;
    debugConfiguration.env = environment;
}

function createProjectEnvironment(
    launchSettings: LaunchSettings | null,
    baseProfile: LaunchProfile | null,
    profileName: string | null,
    disableLaunchProfile: boolean,
    debugConfigurationEnvironment: { [key: string]: string } | undefined,
    runSessionEnvironment: EnvVar[],
    launchOptions: LaunchOptions,
    runApiEnvironment?: { [key: string]: string }
): NodeJS.ProcessEnv {
    if (!launchOptions.isApphost) {
        return Object.fromEntries(mergeEnvironmentVariables(
            baseProfile?.environmentVariables,
            debugConfigurationEnvironment,
            runSessionEnvironment,
            runApiEnvironment
        ));
    }

    const environment = createAppHostBaseEnvironment(launchSettings, runSessionEnvironment, runApiEnvironment);
    const profileExpansionEnvironment = { ...environment };

    if (disableLaunchProfile) {
        deleteEnvironmentVariable(environment, 'DOTNET_LAUNCH_PROFILE');
    }

    if (baseProfile?.applicationUrl) {
        setEnvironmentVariable(environment, 'ASPNETCORE_URLS', baseProfile.applicationUrl);
    }
    applyEnvironmentVariables(
        environment,
        baseProfile?.environmentVariables,
        undefined,
        undefined,
        baseProfile?.commandName === LaunchProfileCommandName.project
            ? value => expandSdkEnvironmentVariables(value, profileExpansionEnvironment)
            : undefined);
    applyEnvironmentVariables(environment, launchOptions.debugSession.configuration?.debuggers?.['project']?.env);

    // The AppHost uses DOTNET_LAUNCH_PROFILE to determine which launch profile to use for project resources.
    // The dotnet CLI sets it (see https://github.com/dotnet/sdk/pull/35029), so replicate that behavior before
    // applying the explicit AppHost environment, which is the final override layer.
    if (profileName) {
        setEnvironmentVariable(environment, 'DOTNET_LAUNCH_PROFILE', profileName);
    }

    applyEnvironmentVariables(environment, launchOptions.debugSession.configuration?.debuggers?.['apphost']?.env);

    return environment;
}

function createAppHostBaseEnvironment(
    launchSettings: LaunchSettings | null,
    runSessionEnvironment: EnvVar[],
    runApiEnvironment?: { [key: string]: string }
): NodeJS.ProcessEnv {
    const environment = getEnvironmentForChildProcess();
    const runPayloadEnvironment = { ...environment };
    applyEnvironmentVariables(runPayloadEnvironment, runApiEnvironment);
    for (const envVar of runSessionEnvironment) {
        setEnvironmentVariable(runPayloadEnvironment, envVar.name, envVar.value);
    }

    // Older CLIs send one flattened AppHost environment that can include the SDK default profile's
    // expanded values. Use the unfiltered payload as the expansion source while identifying those
    // entries, then omit them from the environment used by the profile selected in launch.json.
    // See https://github.com/microsoft/aspire/issues/19387.
    const { profile: defaultProfile, profileName: defaultProfileName } = determineDefaultLaunchProfile(launchSettings);
    const defaultProfileExpansionEnvironment = createDefaultProfileExpansionEnvironment(
        runPayloadEnvironment,
        environment,
        defaultProfile,
        defaultProfileName);
    applyEnvironmentVariables(
        environment,
        runApiEnvironment,
        defaultProfile,
        defaultProfileName,
        undefined,
        defaultProfileExpansionEnvironment);
    for (const envVar of runSessionEnvironment) {
        if (!isDefaultLaunchProfileEnvironmentVariable(
            envVar.name,
            envVar.value,
            defaultProfile,
            defaultProfileName,
            defaultProfileExpansionEnvironment)) {
            setEnvironmentVariable(environment, envVar.name, envVar.value);
        }
    }

    return environment;
}

function createDefaultProfileExpansionEnvironment(
    runPayloadEnvironment: NodeJS.ProcessEnv,
    inheritedEnvironment: NodeJS.ProcessEnv,
    defaultProfile: LaunchProfile | null,
    defaultProfileName: string | null
): NodeJS.ProcessEnv {
    if (!defaultProfile) {
        return { ...runPayloadEnvironment };
    }

    const expansionEnvironment = { ...inheritedEnvironment };
    const pendingNames = new Set([
        ...Object.keys(defaultProfile.environmentVariables ?? {}),
        'ASPNETCORE_URLS',
        'DOTNET_LAUNCH_PROFILE'
    ]);
    const namesEqual = (left: string, right: string) =>
        process.platform === 'win32' ? left.toLowerCase() === right.toLowerCase() : left === right;

    for (const [name, value] of Object.entries(runPayloadEnvironment)) {
        if (!Array.from(pendingNames).some(profileName => namesEqual(profileName, name))) {
            setEnvironmentVariable(expansionEnvironment, name, value);
        }
    }

    // Profile values are expanded against the environment inherited by the SDK process, not against
    // values introduced by the same profile. Resolve stale entries before treating unmatched values
    // as explicit CLI overrides, then repeat because those overrides can affect dependent values.
    let pendingValuesApplied = false;
    while (pendingNames.size > 0) {
        let removedStaleValue = false;
        for (const name of pendingNames) {
            const payloadValue = getEnvironmentVariable(runPayloadEnvironment, name);
            if (payloadValue === undefined) {
                pendingNames.delete(name);
            } else if (isDefaultLaunchProfileEnvironmentVariable(
                name,
                payloadValue,
                defaultProfile,
                defaultProfileName,
                expansionEnvironment)) {
                if (pendingValuesApplied) {
                    deleteEnvironmentVariable(expansionEnvironment, name);
                }

                const inheritedValue = getEnvironmentVariable(inheritedEnvironment, name);
                if (inheritedValue !== undefined) {
                    setEnvironmentVariable(expansionEnvironment, name, inheritedValue);
                }

                pendingNames.delete(name);
                removedStaleValue = true;
            }
        }

        if (removedStaleValue) {
            continue;
        }

        if (!pendingValuesApplied) {
            for (const name of pendingNames) {
                const payloadValue = getEnvironmentVariable(runPayloadEnvironment, name);
                if (payloadValue !== undefined) {
                    setEnvironmentVariable(expansionEnvironment, name, payloadValue);
                }
            }
            pendingValuesApplied = true;
            continue;
        }

        break;
    }

    return expansionEnvironment;
}

function applyEnvironmentVariables(
    environment: NodeJS.ProcessEnv,
    variables: { [key: string]: string } | undefined,
    defaultProfile?: LaunchProfile | null,
    defaultProfileName?: string | null,
    expandValue?: (value: string) => string,
    defaultProfileExpansionEnvironment?: NodeJS.ProcessEnv
): void {
    for (const [name, value] of Object.entries(variables ?? {})) {
        if (!isDefaultLaunchProfileEnvironmentVariable(
            name,
            value,
            defaultProfile,
            defaultProfileName,
            defaultProfileExpansionEnvironment)) {
            setEnvironmentVariable(environment, name, expandValue ? expandValue(value) : value);
        }
    }
}

function isDefaultLaunchProfileEnvironmentVariable(
    name: string,
    value: string | undefined,
    defaultProfile: LaunchProfile | null | undefined,
    defaultProfileName: string | null | undefined,
    expansionEnvironment: NodeJS.ProcessEnv = process.env
): boolean {
    if (!defaultProfile) {
        return false;
    }

    const namesEqual = (candidate: string) =>
        process.platform === 'win32' ? candidate.toLowerCase() === name.toLowerCase() : candidate === name;

    for (const [profileVariableName, profileVariableValue] of Object.entries(defaultProfile.environmentVariables ?? {})) {
        if (namesEqual(profileVariableName) &&
            typeof profileVariableValue === 'string' &&
            (profileVariableValue === value ||
                expandSdkEnvironmentVariables(profileVariableValue, expansionEnvironment) === value)) {
            return true;
        }
    }

    return (namesEqual('ASPNETCORE_URLS') && defaultProfile.applicationUrl === value)
        || (namesEqual('DOTNET_LAUNCH_PROFILE') && defaultProfileName === value);
}

function getEnvironmentVariable(environment: NodeJS.ProcessEnv, name: string): string | undefined {
    if (process.platform !== 'win32') {
        return environment[name];
    }

    const normalizedName = name.toLowerCase();
    const matchingName = Object.keys(environment).find(candidate => candidate.toLowerCase() === normalizedName);
    return matchingName ? environment[matchingName] : undefined;
}

export function createProjectDebuggerExtension(dotNetServiceProducer: (debugSession: AspireDebugSession) => IDotNetService): ResourceDebuggerExtension {
    return {
        resourceType: 'project',
        debugAdapter: 'coreclr',
        extensionId: csharpExtensionId,
        getDisplayName: (launchConfig: ExecutableLaunchConfiguration) => `C#: ${path.basename((launchConfig as ProjectLaunchConfiguration).project_path)}`,
        getSupportedFileTypes: () => ['.cs', '.csproj'],
        getProjectFile: (launchConfig) => {
            if (isProjectLaunchConfiguration(launchConfig)) {
                return launchConfig.project_path;
            }

            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        },
        createDebugSessionConfigurationCallback: async (launchConfig, args, env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
            if (!isProjectLaunchConfiguration(launchConfig)) {
                extensionLogOutputChannel.info(`The resource type was not project for ${JSON.stringify(launchConfig)}`);
                throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
            }

            const projectPath = launchConfig.project_path;
            const isFileBasedProject = isFileBasedApp(projectPath);
            // Newer CLIs build file-based AppHosts before asking the extension to launch them. Keep
            // extension-owned builds for file-based resources and older CLIs.
            const shouldBuildProject = !isFileBasedProject || !launchOptions.isApphost || launchOptions.forceBuild !== false;

            extensionLogOutputChannel.info(`Reading launch settings for: ${projectPath}`);

            // Apply launch profile settings if available
            const launchSettings = await readLaunchSettings(projectPath);
            if (!isProjectLaunchConfiguration(launchConfig)) {
                extensionLogOutputChannel.info(`The resource type was not project for ${projectPath}`);
                throw new Error(invalidLaunchConfiguration(projectPath));
            }

            // AppHost-specific launch profile settings override generic project settings. prepareDebugSession
            // applies resource-type settings last, so resolve these directly from launch.json instead.
            const appHostLaunchProfileOptions = getAppHostLaunchProfileOptions(
                launchOptions.debugSession.configuration,
                true);
            const effectiveLaunchConfig: ProjectLaunchConfiguration = launchOptions.isApphost ? {
                ...launchConfig,
                disable_launch_profile: appHostLaunchProfileOptions.disableLaunchProfile
                    ?? debugConfiguration.disableLaunchProfile,
                launch_profile: appHostLaunchProfileOptions.launchProfile
                    ?? debugConfiguration.launchProfile
                    ?? launchConfig.launch_profile
            } : launchConfig;

            const { profile: baseProfile, profileName, hasInvalidProperties } = determineBaseLaunchProfile(effectiveLaunchConfig, launchSettings);

            if (launchOptions.isApphost &&
                effectiveLaunchConfig.disable_launch_profile !== true &&
                effectiveLaunchConfig.launch_profile &&
                !baseProfile) {
                throw new Error(explicitLaunchProfileNotResolved(effectiveLaunchConfig.launch_profile));
            }

            if (launchOptions.isApphost &&
                baseProfile &&
                baseProfile.commandName !== LaunchProfileCommandName.project &&
                baseProfile.commandName !== LaunchProfileCommandName.executable) {
                throw new Error(launchProfileUnsupportedCommandName(profileName ?? ''));
            }

            if (launchOptions.isApphost &&
                baseProfile &&
                (hasInvalidProperties || !hasSdkCompatibleLaunchProfileProperties(baseProfile))) {
                throw new Error(launchProfileHasInvalidProperties(profileName ?? ''));
            }

            extensionLogOutputChannel.info(profileName
                ? `Using launch profile '${profileName}' for project: ${projectPath}`
                : `No launch profile selected for project: ${projectPath}`);

            // Configure debug session with launch profile settings
            // ProjectLaunchProfile does not consume workingDirectory or executablePath, and neither
            // SDK provider consumes useSSL. Ignore them here too so bypassing dotnet run preserves
            // the provider semantics. File-based apps are the exception: their fallback explicitly
            // disables the SDK profile and forwards a valid workingDirectory through MSBuild.
            const isAppHostProjectProfile = launchOptions.isApphost &&
                baseProfile?.commandName === LaunchProfileCommandName.project;
            const shouldApplyProfileWorkingDirectory = !isAppHostProjectProfile || isFileBasedProject;
            const workingDirectoryProfile = shouldApplyProfileWorkingDirectory &&
                typeof baseProfile?.workingDirectory === 'string' ? baseProfile : null;
            const launchSettingsDirectory = baseProfile?.commandName === LaunchProfileCommandName.executable
                ? launchSettings?.sourceDirectory
                : undefined;
            const appHostProfileExpansionEnvironment = launchOptions.isApphost
                ? createAppHostBaseEnvironment(launchSettings, env)
                : undefined;
            debugConfiguration.cwd = determineWorkingDirectory(
                projectPath,
                workingDirectoryProfile,
                launchSettingsDirectory);
            const profileCommandLineArgs = isAppHostProjectProfile && baseProfile.commandLineArgs
                ? expandSdkEnvironmentVariables(baseProfile.commandLineArgs, appHostProfileExpansionEnvironment)
                : baseProfile?.commandLineArgs;
            let resolvedArguments = determineArguments(profileCommandLineArgs, args);
            debugConfiguration.args = resolvedArguments;
            debugConfiguration.executablePath = launchOptions.isApphost
                ? baseProfile?.commandName === LaunchProfileCommandName.executable ? baseProfile.executablePath : undefined
                : baseProfile?.executablePath;
            debugConfiguration.checkForDevCert = launchOptions.isApphost ? undefined : baseProfile?.useSSL;

            // `launchBrowser` from launchSettings.json is deliberately not honoured here. Every project that
            // reaches this callback is started by the app host, and the app host owns its endpoints: it
            // assigns ports and can front the project with a proxy, so the `applicationUrl` on disk is
            // routinely not where the resource actually listens. The Aspire dashboard resource is the
            // extreme case, because the app host both replaces its URLs and puts a login token on the real
            // address, so honouring the profile opened a stale port and an unauthenticated page.
            //
            // The run-session payload carries no endpoint data, so the extension cannot correct the URL.
            // The CLI resolves this by ignoring the setting outright — `LaunchProfile.LaunchBrowser` is
            // parsed but never read anywhere in Aspire.Hosting or Aspire.Cli, so `aspire run` opens nothing
            // for a project resource and leaves URLs to the dashboard. Matching that keeps the two front
            // ends consistent instead of having VS Code open a URL the CLI never would.
            //
            // A serverReadyAction the user configured explicitly in launch.json is still respected; it is
            // read from `debugConfiguration` above and never overwritten here.

            // TODO: Remove this block — the dashboard no longer recognizes ASPIRE_DASHBOARD_AI_DISABLED.
            // See https://github.com/microsoft/aspire/issues/18751
            // Temporarily disable GH Copilot on the dashboard before the extension implementation is approved
            if (launchOptions.isApphost) {
                env.push({ name: "ASPIRE_DASHBOARD_AI_DISABLED", value: "true" });
            }

            // An Executable-command launch profile must specify an executablePath. The .NET SDK's
            // ExecutableProvider requires it, so `dotnet run` / `dotnet run-api` fail with a configuration
            // error when it is missing. Without this guard the extension would instead fall through the
            // `&& executablePath` check below and silently launch the project output (or file-based app),
            // running a different program than the SDK would. Surface the same configuration error instead.
            if (baseProfile?.commandName === LaunchProfileCommandName.executable && !baseProfile.executablePath) {
                throw new Error(executableLaunchProfileMissingExecutablePath(profileName ?? ''));
            }

            if (baseProfile?.commandName === LaunchProfileCommandName.executable && baseProfile.executablePath) {
                const dotNetService: IDotNetService = dotNetServiceProducer(launchOptions.debugSession);

                // For Executable command profiles (e.g., class library integrations), the launch profile
                // specifies an external executable to run instead of the project output.
                // Build the project to ensure dependencies are compiled unless the CLI already built this
                // file-based AppHost, then launch using the profile's executable path and command line arguments.
                // Expand environment variable references (e.g. $(HOME)) that VS handles natively
                // but aren't expanded by the coreclr debugger.
                if (shouldBuildProject) {
                    await dotNetService.buildDotNetProject(projectPath);
                }

                debugConfiguration.program = expandEnvironmentVariables(baseProfile.executablePath);
                resolvedArguments = expandDebugConfigurationArguments(resolvedArguments);
                debugConfiguration.args = resolvedArguments;
                debugConfiguration.env = createProjectEnvironment(
                    launchSettings,
                    baseProfile,
                    profileName,
                    effectiveLaunchConfig.disable_launch_profile === true,
                    debugConfiguration.env,
                    env,
                    launchOptions);
            }
            else if (!isFileBasedProject) {
                const dotNetService: IDotNetService = dotNetServiceProducer(launchOptions.debugSession);
                const projectLaunchProperties = await dotNetService.getDotNetProjectLaunchProperties(projectPath);
                const outputPath = projectLaunchProperties.targetPath;
                const debugProgram = getProjectDebugProgram(projectLaunchProperties);
                if (debugProgram !== outputPath) {
                    extensionLogOutputChannel.info(`Using generated apphost executable for unpackaged WinUI project: ${debugProgram}`);
                }
                const outputExists = await doesFileExist(outputPath);
                const debugProgramExists = debugProgram === outputPath
                    ? outputExists
                    : await doesFileExist(debugProgram);
                if (!outputExists || !debugProgramExists || launchOptions.forceBuild) {
                    await dotNetService.buildDotNetProject(projectPath);
                }

                if (await shouldLaunchProjectWithDotNetRun(outputPath)) {
                    const fallbackMessage = dotNetRunFallbackDisablesDebugger(outputPath, projectPath);
                    extensionLogOutputChannel.warn(fallbackMessage);
                    if (launchOptions.debug) {
                        vscode.window.showInformationMessage(fallbackMessage);
                    }

                    configureDotNetRunDebugConfiguration(
                        debugConfiguration,
                        createDotNetRunArguments(projectPath, profileCommandLineArgs, args),
                        createProjectEnvironment(launchSettings, baseProfile, profileName, effectiveLaunchConfig.disable_launch_profile === true, debugConfiguration.env, env, launchOptions));
                } else {
                    debugConfiguration.program = debugProgram;
                    debugConfiguration.env = createProjectEnvironment(
                        launchSettings,
                        baseProfile,
                        profileName,
                        effectiveLaunchConfig.disable_launch_profile === true,
                        debugConfiguration.env,
                        env,
                        launchOptions);
                }
            }
            else {
                const dotNetService: IDotNetService = dotNetServiceProducer(launchOptions.debugSession);

                // `dotnet run-api` always applies the SDK *default* (first supported) launch profile and offers
                // no way to request a specific profile or --no-launch-profile. When that default profile is an
                // 'Executable' profile, run-api reports THAT profile's external command (e.g. `dotnet --version`)
                // instead of the file-based app, so its ExecutablePath / CommandLineArguments / environment
                // describe the wrong program. This branch is only reached when the selected base profile is not an
                // Executable profile (profiles disabled, or a later 'Project' profile explicitly selected), so
                // blindly trusting run-api's program here would launch the wrong thing.
                const { profile: runApiDefaultProfile, profileName: runApiDefaultProfileName } = determineDefaultLaunchProfile(launchSettings);

                if (runApiDefaultProfile?.commandName === LaunchProfileCommandName.executable) {
                    // Do not trust run-api's program. Launch the file-based app ourselves with
                    // `dotnet run --file <app.cs> --no-launch-profile` (no debugger attach), applying the selected
                    // profile's arguments and environment. This mirrors the dotnet-run fallback used when project
                    // build output is not directly runnable.
                    const fallbackMessage = dotNetRunFileBasedExecutableProfileFallback(runApiDefaultProfileName ?? '', projectPath);
                    extensionLogOutputChannel.warn(fallbackMessage);
                    if (launchOptions.debug) {
                        vscode.window.showInformationMessage(fallbackMessage);
                    }

                    if (shouldBuildProject) {
                        // There may be an older cached version of the file-based app, so force a build.
                        await dotNetService.buildDotNetProject(projectPath);
                    }

                    const projectDirectory = path.dirname(projectPath);
                    const runWorkingDirectory = debugConfiguration.cwd === projectDirectory ? undefined : debugConfiguration.cwd;
                    configureDotNetRunDebugConfiguration(
                        debugConfiguration,
                        createDotNetRunArguments(
                            projectPath,
                            profileCommandLineArgs,
                            args,
                            /* fileBased */ true,
                            /* skipBuild */ !shouldBuildProject,
                            runWorkingDirectory,
                            /* suppressCliRunHook */ launchOptions.isApphost),
                        createProjectEnvironment(launchSettings, baseProfile, profileName, effectiveLaunchConfig.disable_launch_profile === true, debugConfiguration.env, env, launchOptions),
                        projectDirectory);
                }
                else {
                    // The default profile is a 'Project' profile (or there is none), so run-api's program is the
                    // file-based app itself and can be trusted.
                    // The Aspire SDK run hook would rewrite an AppHost RunCommand to `aspire run`, but the CLI
                    // already owns this launch. Suppress the hook so run-api returns the generated executable.
                    const runApiEnvironment = launchOptions.isApphost ? { ASPIRE_SUPPRESS_CLI_RUN_HOOK: 'true' } : undefined;
                    const runApiOutput = await dotNetService.getDotNetRunApiOutput(projectPath, runApiEnvironment);
                    const runApiConfig = getRunApiConfigFromOutput(runApiOutput);

                    if (shouldBuildProject) {
                        // There may be an older cached version of the file-based app, so force a build.
                        await dotNetService.buildDotNetProject(projectPath);
                    }

                    debugConfiguration.program = runApiConfig.executablePath;

                    const hostArguments = isDotnetLauncher(runApiConfig.executablePath) ? runApiConfig.commandLineArguments : undefined;
                    resolvedArguments = combineRunApiArguments(hostArguments, resolvedArguments);
                    debugConfiguration.args = resolvedArguments;

                    // Intentionally do NOT consume run-api's WorkingDirectory: it carries the SDK default profile's
                    // working directory, whereas cwd was already resolved from the (possibly different) selected
                    // launch profile via determineWorkingDirectory.
                    //
                    // From run-api's environment we keep ONLY the SDK-injected runtime host-resolution variables
                    // (DOTNET_ROOT*) so the launched program can locate the .NET runtime.
                    // BUT
                    // If the default launch profile, or the selected launch profile sets any of these variables,
                    // we must not override them with the run-api values.
                    const profileDefinedRuntimeHostNames = new Set<string>([
                        ...collectProfileDotnetHostEnvVarNames(runApiDefaultProfile),
                        ...collectProfileDotnetHostEnvVarNames(baseProfile)
                    ]);

                    debugConfiguration.env = createProjectEnvironment(
                        launchSettings,
                        baseProfile,
                        profileName,
                        effectiveLaunchConfig.disable_launch_profile === true,
                        debugConfiguration.env,
                        env,
                        launchOptions,
                        pickRuntimeHostEnvironment(runApiConfig.env, profileDefinedRuntimeHostNames));
                }
            }

            if (!launchOptions.isApphost && debugConfiguration.noDebug !== true) {
                // C# Dev Kit and vsdbg provide Hot Reload for the ordinary coreclr launch. Aspire only
                // reports their effective settings and points users at the setting when it is disabled.
                try {
                    const hotReloadDiagnostics = getHotReloadDiagnostics();
                    logHotReloadDiagnostics(`${projectPath} (run ${debugConfiguration.runId})`, hotReloadDiagnostics);

                    // A notification stays open until the user answers it, so it must not block launch.
                    void showHotReloadDisabledAdvisoryIfNeeded(hotReloadDiagnostics);
                }
                catch (err) {
                    extensionLogOutputChannel.warn(`Could not read C# Dev Kit Hot Reload settings; continuing without diagnostics: ${err instanceof Error ? err.message : String(err)}`);
                }
            }
        }
    };
}

export const projectDebuggerExtension: ResourceDebuggerExtension = createProjectDebuggerExtension(debugSession => new DotNetService(debugSession));
