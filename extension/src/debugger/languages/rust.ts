import * as vscode from 'vscode';
import { spawn } from 'child_process';
import { getRustExtensionId } from "../../capabilities";
import { AspireResourceExtendedDebugConfiguration, EnvVar, ExecutableLaunchConfiguration, isRustLaunchConfiguration, RustLaunchConfiguration } from "../../dcp/types";
import { invalidLaunchConfiguration, rustBuildFailedWithError, rustBuildFailedWithExitCode, rustLaunchConfigurationMissingExecutable, rustDisplayName, rustLabel } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { AspireDebugSession } from "../AspireDebugSession";
import { mergeCliSpawnEnvironment } from "./cli";
import { processGroupSpawnOptions, terminateProcessTree } from "../../utils/processTree";

export interface IRustService {
    build(workingDirectory: string, cargoArgs: string[], env: EnvVar[]): Promise<void>;
}

export class RustService implements IRustService {
    private readonly _debugSession: AspireDebugSession;

    constructor(debugSession: AspireDebugSession) {
        this._debugSession = debugSession;
    }

    private writeToDebugConsole(message: string, category: 'stdout' | 'stderr'): void {
        this._debugSession.sendMessage(message, false, category);
    }

    build(workingDirectory: string, cargoArgs: string[], env: EnvVar[]): Promise<void> {
        return new Promise<void>((resolve, reject) => {
            extensionLogOutputChannel.info(`Building Rust application in ${workingDirectory} using: cargo ${cargoArgs.join(' ')}`);

            // Build with the resource's environment so settings the app host injects (RUSTFLAGS,
            // CARGO_*, proxy variables, and anything set with WithEnvironment) apply to the debug
            // build exactly as they do when DCP runs `cargo run` itself.
            const buildEnv: Record<string, string | undefined> = { ...process.env };
            mergeCliSpawnEnvironment(buildEnv, env);

            const buildProcess = spawn('cargo', cargoArgs, {
                cwd: workingDirectory,
                env: buildEnv,
                // Cargo fans out into rustc, the linker and any build scripts. Making it a process group
                // leader is what lets the cancellation below take those down with it.
                ...processGroupSpawnOptions()
            });

            // A build can outlive the session that asked for it (cargo waits on its own package lock,
            // and a cold build takes minutes), so stop it when the debug session goes away rather than
            // leaving an orphaned toolchain process holding the target directory lock.
            const cancellation = this._debugSession.registerDisposable({
                dispose: () => {
                    if (buildProcess.exitCode === null && buildProcess.signalCode === null) {
                        extensionLogOutputChannel.info(`Debug session ended; stopping cargo build in ${workingDirectory}.`);
                        terminateProcessTree(buildProcess);
                    }
                }
            });

            let stderrOutput = '';

            buildProcess.stdout.on('data', (data: Buffer) => this.writeToDebugConsole(data.toString(), 'stdout'));

            // cargo writes its progress and all compiler diagnostics to stderr, so this carries the output a
            // user needs to fix a broken build, not just failures.
            buildProcess.stderr.on('data', (data: Buffer) => {
                const output = data.toString();
                stderrOutput += output;
                this.writeToDebugConsole(output, 'stderr');
            });

            buildProcess.on('error', err => {
                cancellation.dispose();
                extensionLogOutputChannel.error(`cargo build process error: ${err}`);
                reject(new Error(rustBuildFailedWithError(workingDirectory, err.message)));
            });

            buildProcess.on('close', (code, signal) => {
                cancellation.dispose();

                if (code !== 0) {
                    // A build killed by a signal reports a null exit code, so name the signal instead of
                    // rendering "exit code null". stderr has already been streamed to the debug console,
                    // but repeating it keeps the reason visible in the error notification.
                    const exitDescription = code !== null ? `${code}` : `${signal}`;
                    const error = rustBuildFailedWithExitCode(workingDirectory, exitDescription);
                    reject(new Error(stderrOutput ? `${error}\n${stderrOutput}` : error));
                    return;
                }

                resolve();
            });
        });
    }
}

function asRustConfig(launchConfig: ExecutableLaunchConfiguration): RustLaunchConfiguration {
    if (isRustLaunchConfiguration(launchConfig)) {
        return launchConfig;
    }

    extensionLogOutputChannel.info(`The resource type was not rust for ${JSON.stringify(launchConfig)}`);
    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

function getProjectFile(launchConfig: ExecutableLaunchConfiguration): string {
    const config = asRustConfig(launchConfig);
    return config.working_directory || '';
}

// Rust has no cross-platform native debugger extension: the Microsoft C++ extension's Windows-only
// cppvsdbg engine understands the PDBs produced by the MSVC-based Rust toolchain, while CodeLLDB is the
// extension VS Code's own docs recommend for macOS/Linux. See:
// https://code.visualstudio.com/docs/languages/rust#_install-debugging-support
const rustDebugAdapter = process.platform === 'win32' ? 'cppvsdbg' : 'lldb';
const rustExtensionId = getRustExtensionId();

export function createRustDebuggerExtension(rustServiceProducer: (debugSession: AspireDebugSession) => IRustService): ResourceDebuggerExtension {
    return {
        resourceType: 'rust',
        debugAdapter: rustDebugAdapter,
        extensionId: rustExtensionId,
        getDisplayName: (launchConfiguration: ExecutableLaunchConfiguration) => {
            if (isRustLaunchConfiguration(launchConfiguration)) {
                const displayPath = launchConfiguration.working_directory || '';
                return displayPath ? rustDisplayName(vscode.workspace.asRelativePath(displayPath)) : rustLabel;
            }

            return rustLabel;
        },
        getSupportedFileTypes: () => ['.rs'],
        getProjectFile: (launchConfig) => getProjectFile(launchConfig),
        createDebugSessionConfigurationCallback: async (launchConfig, args, env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
            const config = asRustConfig(launchConfig);
            const workingDirectory = config.working_directory || '';
            const cargoArgs = config.cargo?.args ?? ['build'];

            // The app host works out which file this build produces from `cargo metadata`, so there is
            // nothing to discover here. That answer is better than anything the build could report:
            // `cargo build` ignores `default-run` and so produces every binary in the package, while
            // metadata reports `default-run` and therefore matches what `cargo run` launches, and what
            // `aspire publish` puts in the container.
            const executablePath = config.cargo?.executable_path;
            if (!executablePath) {
                throw new Error(rustLaunchConfigurationMissingExecutable(workingDirectory));
            }

            const rustService = rustServiceProducer(launchOptions.debugSession);
            await rustService.build(workingDirectory, cargoArgs, env ?? []);

            debugConfiguration.program = executablePath;
            debugConfiguration.cwd = workingDirectory;
            debugConfiguration.args = args ?? [];

            if (rustDebugAdapter === 'cppvsdbg') {
                debugConfiguration.console = 'internalConsole';

                // cppvsdbg (and cppdbg) read environment variables from "environment" as a name/value
                // array; they ignore the "env" object that createDebugSessionConfiguration populates for
                // every other debug adapter, so translate it here.
                const env = debugConfiguration.env as Record<string, string | undefined> | undefined;
                debugConfiguration.environment = Object.entries(env ?? {}).map(([name, value]) => ({ name, value: value ?? '' }));
            } else {
                // CodeLLDB already understands the "env" object populated by createDebugSessionConfiguration.
                debugConfiguration.sourceLanguages = ['rust'];
            }
        }
    };
}

export const rustDebuggerExtension: ResourceDebuggerExtension = createRustDebuggerExtension(debugSession => new RustService(debugSession));
