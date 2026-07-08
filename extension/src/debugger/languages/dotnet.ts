import * as vscode from 'vscode';
import { extensionLogOutputChannel } from '../../utils/logging';
import { noCsharpBuildTask, buildFailedWithExitCode, noOutputFromMsbuild, failedToGetTargetPath, invalidLaunchConfiguration, buildFailedForProjectWithError, processExitedWithCode, lookingForDevkitBuildTask, csharpDevKitNotInstalled, failedToInspectRuntimeConfig, dotNetRunFallbackDisablesDebugger, dotNetRunFileBasedExecutableProfileFallback, executableLaunchProfileMissingExecutablePath } from '../../loc/strings';
import { ChildProcessWithoutNullStreams, execFile, spawn } from 'child_process';
import * as util from 'util';
import * as path from 'path';
import * as readline from 'readline';
import * as os from 'os';
import * as fs from 'fs';
import { doesFileExist } from '../../utils/io';
import { AspireResourceExtendedDebugConfiguration, EnvVar, ExecutableLaunchConfiguration, isProjectLaunchConfiguration, ProjectLaunchConfiguration } from '../../dcp/types';
import { ResourceDebuggerExtension } from '../debuggerExtensions';
import {
    readLaunchSettings,
    determineBaseLaunchProfile,
    determineDefaultLaunchProfile,
    mergeEnvironmentVariables,
    determineArguments,
    determineWorkingDirectory,
    determineServerReadyAction,
    LaunchProfileCommandName,
    LaunchProfile,
    expandEnvironmentVariables
} from '../launchProfiles';
import { AspireDebugSession } from '../AspireDebugSession';
import { createAspireCliPathProcessEnvironment } from '../../utils/cliPathEnvironment';

interface IDotNetService {
    getAndActivateDevKit(): Promise<boolean>
    buildDotNetProject(projectFile: string): Promise<void>;
    getDotNetTargetPath(projectFile: string): Promise<string>;
    getDotNetRunApiOutput(projectFile: string): Promise<string>;
}

class DotNetService implements IDotNetService {
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
            const buildProcess = spawn('dotnet', args, { env: createAspireCliPathProcessEnvironment() });

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
                reject(new Error(buildFailedForProjectWithError(projectFile, err.message)));
            });

            buildProcess.on('close', (code) => {
                if (code === 0) {
                    // if build succeeds, simply return. otherwise throw to trigger error handling
                    if (stderrOutput) {
                        reject(createErrorWithStreamedDebugConsoleOutput(stderrOutput));
                    } else {
                        resolve();
                    }
                } else {
                    reject(createErrorWithStreamedDebugConsoleOutput(buildFailedForProjectWithError(projectFile, stdoutOutput || stderrOutput || `Exit code ${code}`)));
                }
            });
        });
    }

    async getDotNetTargetPath(projectFile: string): Promise<string> {
        const args = [
            'msbuild',
            projectFile,
            '-nologo',
            '-getProperty:TargetPath',
            '-v:q',
            '-property:GenerateFullPaths=true'
        ];
        try {
            const { stdout } = await this.execFileAsync('dotnet', args, { encoding: 'utf8', env: createAspireCliPathProcessEnvironment() });
            const output = stdout.trim();
            if (!output) {
                throw new Error(noOutputFromMsbuild);
            }

            return output;
        } catch (err) {
            throw new Error(failedToGetTargetPath(String(err)));
        }
    }

    async getDotNetRunApiOutput(projectPath: string): Promise<string> {
        let childProcess: ChildProcessWithoutNullStreams;

        return new Promise<string>(async (resolve, reject) => {
            try {
                const timeout = setTimeout(() => {
                    childProcess?.kill();
                    reject(new Error('Timeout while waiting for dotnet run-api response'));
                }, 10_000);

                extensionLogOutputChannel.info('dotnet run-api - starting process');

                childProcess = spawn('dotnet', ['run-api'], {
                    cwd: path.dirname(projectPath),
                    env: createAspireCliPathProcessEnvironment(),
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
                reject(e);
            }
        }).finally(() => childProcess.removeAllListeners());
    }
}

export function isFileBasedApp(projectPath: string): boolean {
    return path.extname(projectPath).toLowerCase().endsWith('.cs');
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

// Combine the SDK host arguments from `dotnet run-api` (the built app DLL that is passed to the `dotnet`
// launcher) with the user/launch-profile application arguments that were already resolved onto the debug
// configuration. `hostArguments` is present only when the program is the `dotnet` launcher; 
// for an apphost-executable build it is undefined and only the application arguments remain.
// The host arguments must come first because they identify what to run; the user application arguments
// follow and are passed to the app. The result is kept as a single command-line string so the quoting the
// SDK already applied to CommandLineArguments is preserved.
function combineRunApiArguments(hostArguments: string | undefined, applicationArguments: string | string[] | undefined): string | string[] | undefined {
    const applicationArgumentsText = Array.isArray(applicationArguments) ? applicationArguments.join(' ') : applicationArguments;
    const combined = [hostArguments, applicationArgumentsText]
        .filter((part): part is string => part !== undefined && part.length > 0)
        .join(' ');

    return combined.length > 0 ? combined : undefined;
}

function createErrorWithStreamedDebugConsoleOutput(message: string): Error {
    // Mark build errors whose output was already streamed to avoid replaying the transcript in AppHost startup handling.
    const error = new Error(message) as Error & { debugConsoleOutputAlreadyWritten?: boolean };
    error.debugConsoleOutputAlreadyWritten = true;

    return error;
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

function quoteCommandLineArgument(argument: string): string {
    return `"${argument.replace(/"/g, '\\"')}"`;
}

function createDotNetRunBaseArguments(projectPath: string, fileBased: boolean): string[] {
    // File-based apps (.cs) launch with `dotnet run --file <app.cs> --no-cache`; project files launch with
    // `dotnet run --project <proj>`. This mirrors how the hosting side builds the non-debug `dotnet run`
    // command line in DotnetProjectHostingExtensions.
    return fileBased
        ? ['run', '--file', projectPath, '--no-cache', '--no-launch-profile']
        : ['run', '--project', projectPath, '--no-launch-profile'];
}

function createDotNetRunArguments(projectPath: string, baseProfileArgs: string | undefined, runSessionArgs: string[] | undefined, fileBased: boolean = false): string[] | string {
    const dotnetRunArgs = createDotNetRunBaseArguments(projectPath, fileBased);
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
        const quotedRunArgs = createDotNetRunBaseArguments(quoteCommandLineArgument(projectPath), fileBased);
        return `${quotedRunArgs.join(' ')} -- ${baseProfileArgs}`;
    }

    return dotnetRunArgs;
}

function configureDotNetRunDebugConfiguration(
    debugConfiguration: AspireResourceExtendedDebugConfiguration,
    args: string[] | string,
    baseProfileEnvironmentVariables: { [key: string]: string } | undefined,
    runSessionEnvironmentVariables: EnvVar[]): void {
    debugConfiguration.program = 'dotnet';
    debugConfiguration.args = args;
    // Intentionally do NOT set cwd here. The caller already resolved debugConfiguration.cwd from the
    // selected launch profile via determineWorkingDirectory (which falls back to the project directory
    // when the profile sets no workingDirectory). Because this fallback launches with --no-launch-profile,
    // `dotnet run` will not re-apply the profile's workingDirectory itself, so overwriting cwd here would
    // silently discard a custom profile workingDirectory and launch the app from the wrong directory.
    debugConfiguration.executablePath = undefined;
    debugConfiguration.noDebug = true;
    debugConfiguration.env = Object.fromEntries(mergeEnvironmentVariables(
        baseProfileEnvironmentVariables,
        debugConfiguration.env,
        runSessionEnvironmentVariables
    ));
}

export function createProjectDebuggerExtension(dotNetServiceProducer: (debugSession: AspireDebugSession) => IDotNetService): ResourceDebuggerExtension {
    return {
        resourceType: 'project',
        debugAdapter: 'coreclr',
        extensionId: 'ms-dotnettools.csharp',
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

            extensionLogOutputChannel.info(`Reading launch settings for: ${projectPath}`);

            // Apply launch profile settings if available
            const launchSettings = await readLaunchSettings(projectPath);
            if (!isProjectLaunchConfiguration(launchConfig)) {
                extensionLogOutputChannel.info(`The resource type was not project for ${projectPath}`);
                throw new Error(invalidLaunchConfiguration(projectPath));
            }

            // For apphost, read launch profile settings from debugConfiguration (from launch.json)
            // For resources, read from launchConfig (from payload)
            const effectiveLaunchConfig: ProjectLaunchConfiguration = launchOptions.isApphost ? {
                ...launchConfig,
                disable_launch_profile: debugConfiguration.disableLaunchProfile,
                launch_profile: debugConfiguration.launchProfile
            } : launchConfig;

            const { profile: baseProfile, profileName } = determineBaseLaunchProfile(effectiveLaunchConfig, launchSettings);

            extensionLogOutputChannel.info(profileName
                ? `Using launch profile '${profileName}' for project: ${projectPath}`
                : `No launch profile selected for project: ${projectPath}`);

            // Configure debug session with launch profile settings
            debugConfiguration.cwd = determineWorkingDirectory(projectPath, baseProfile);
            debugConfiguration.args = determineArguments(baseProfile?.commandLineArgs, args);
            debugConfiguration.executablePath = baseProfile?.executablePath;
            debugConfiguration.checkForDevCert = baseProfile?.useSSL;

            // The apphost's application URL is the Aspire dashboard URL. We already get the dashboard login URL later on,
            // so we should just avoid setting up serverReadyAction and manually open the browser ourselves.
            if (!launchOptions.isApphost) {
                debugConfiguration.serverReadyAction = determineServerReadyAction(baseProfile?.launchBrowser, baseProfile?.applicationUrl, baseProfile?.launchUrl);
            }

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
                // Build the project to ensure dependencies are compiled, then launch
                // using the profile's executable path and command line arguments.
                // Expand environment variable references (e.g. $(HOME)) that VS handles natively
                // but aren't expanded by the coreclr debugger.
                await dotNetService.buildDotNetProject(projectPath);

                debugConfiguration.program = expandEnvironmentVariables(baseProfile.executablePath);
                if (debugConfiguration.args) {
                    debugConfiguration.args = expandEnvironmentVariables(debugConfiguration.args);
                } else if (baseProfile.commandLineArgs) {
                    // Fall back to launch profile args if run session args were empty
                    debugConfiguration.args = expandEnvironmentVariables(baseProfile.commandLineArgs);
                }
                debugConfiguration.env = Object.fromEntries(mergeEnvironmentVariables(
                    baseProfile?.environmentVariables,
                    debugConfiguration.env,
                    env
                ));
            }
            else if (!isFileBasedApp(projectPath)) {
                const dotNetService: IDotNetService = dotNetServiceProducer(launchOptions.debugSession);
                const outputPath = await dotNetService.getDotNetTargetPath(projectPath);
                if ((!(await doesFileExist(outputPath)) || launchOptions.forceBuild)) {
                    await dotNetService.buildDotNetProject(projectPath);
                }

                if (await shouldLaunchProjectWithDotNetRun(outputPath)) {
                    const fallbackMessage = dotNetRunFallbackDisablesDebugger(outputPath, projectPath);
                    extensionLogOutputChannel.warn(fallbackMessage);
                    if (launchOptions.debug) {
                        vscode.window.showInformationMessage(fallbackMessage);
                    }

                    configureDotNetRunDebugConfiguration(debugConfiguration, createDotNetRunArguments(projectPath, baseProfile?.commandLineArgs, args), baseProfile?.environmentVariables, env);
                } else {
                    debugConfiguration.program = outputPath;
                    debugConfiguration.env = Object.fromEntries(mergeEnvironmentVariables(
                        baseProfile?.environmentVariables,
                        debugConfiguration.env,
                        env
                    ));
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

                    // There may be an older cached version of the file-based app, so force a build.
                    await dotNetService.buildDotNetProject(projectPath);

                    configureDotNetRunDebugConfiguration(
                        debugConfiguration,
                        createDotNetRunArguments(projectPath, baseProfile?.commandLineArgs, args, /* fileBased */ true),
                        baseProfile?.environmentVariables,
                        env);
                }
                else {
                    // The default profile is a 'Project' profile (or there is none), so run-api's program is the
                    // file-based app itself and can be trusted.
                    const runApiOutput = await dotNetService.getDotNetRunApiOutput(projectPath);
                    const runApiConfig = getRunApiConfigFromOutput(runApiOutput);

                    // There may be an older cached version of the file-based app, so force a build.
                    await dotNetService.buildDotNetProject(projectPath);

                    debugConfiguration.program = runApiConfig.executablePath;

                    const hostArguments = isDotnetLauncher(runApiConfig.executablePath) ? runApiConfig.commandLineArguments : undefined;
                    debugConfiguration.args = combineRunApiArguments(hostArguments, debugConfiguration.args);

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

                    debugConfiguration.env = Object.fromEntries(mergeEnvironmentVariables(
                        baseProfile?.environmentVariables,
                        debugConfiguration.env,
                        env,
                        pickRuntimeHostEnvironment(runApiConfig.env, profileDefinedRuntimeHostNames)
                    ));
                }
            }

            // Set DOTNET_LAUNCH_PROFILE
            // The apphost uses DOTNET_LAUNCH_PROFILE to determine which launch profile to use for project resources. The dotnet CLI sets this environment
            // variable (see https://github.com/dotnet/sdk/pull/35029), we need to replicate the behavior by setting it ourselves.
            if (launchOptions.isApphost && profileName) {
                debugConfiguration.env['DOTNET_LAUNCH_PROFILE'] = profileName;
            }
        }
    };
}

export const projectDebuggerExtension: ResourceDebuggerExtension = createProjectDebuggerExtension(debugSession => new DotNetService(debugSession));
