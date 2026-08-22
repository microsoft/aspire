import * as vscode from 'vscode';
import { ChildProcessWithoutNullStreams, spawn } from 'child_process';
import * as readline from 'readline';
import { getRustExtensionId } from "../../capabilities";
import { AspireResourceExtendedDebugConfiguration, EnvVar, ExecutableLaunchConfiguration, isRustLaunchConfiguration, RustLaunchConfiguration } from "../../dcp/types";
import { invalidLaunchConfiguration, rustBuildFailedWithError, rustBuildFailedWithExitCode, rustBuildOutputRedacted, rustBuildProducedMultipleExecutables, rustBuildProducedNoExecutable, rustBuildStderrTruncated, rustDisplayName, rustLabel, rustWindowsGnuDebuggerUnsupported } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";
import { AspireDebugSession, markDebugConfigurationEnvironmentSensitive } from "../AspireDebugSession";
import { mergeCliSpawnEnvironment } from "../../utils/process/cliProcess";
import { processGroupSpawnOptions, terminateProcessTree } from "../../utils/processTree";

const rustBuildStderrTailLimit = 8 * 1024;
const cargoHostProbeOutputLimit = 16 * 1024;
const cargoHostProbeShutdownGracePeriodMs = 5_000;
const cargoHostProbeTimeoutMs = 5_000;
const cargoExecutable = 'cargo';

interface CargoCompilerArtifactMessage {
    reason: 'compiler-artifact';
    target?: { name?: string; kind?: string[] };
    executable?: string | null;
}

interface CargoCompilerMessage {
    reason: 'compiler-message';
    message?: { rendered?: string; level?: string };
}

type CargoBuildMessage = CargoCompilerArtifactMessage | CargoCompilerMessage | { reason: string };

export interface IRustService {
    getCargoHostTarget(
        workingDirectory: string,
        env: EnvVar[]
    ): Promise<string | undefined>;

    build(
        workingDirectory: string,
        cargoArgs: string[],
        env: EnvVar[],
        executablePath: string | undefined
    ): Promise<string>;
}

export class RustService implements IRustService {
    private readonly _debugSession: AspireDebugSession;

    constructor(debugSession: AspireDebugSession) {
        this._debugSession = debugSession;
    }

    private writeToDebugConsole(message: string, category: 'stdout' | 'stderr'): void {
        this._debugSession.sendMessage(message, false, category);
    }

    getCargoHostTarget(
        workingDirectory: string,
        env: EnvVar[]
    ): Promise<string | undefined> {
        return new Promise<string | undefined>((resolve, reject) => {
            extensionLogOutputChannel.info(`Probing Cargo host target in ${workingDirectory}.`);
            let probeProcess: ChildProcessWithoutNullStreams;
            try {
                probeProcess = spawn(cargoExecutable, ['-Vv'], {
                    cwd: workingDirectory,
                    env: createCargoEnvironment(env),
                    ...processGroupSpawnOptions()
                });
            } catch {
                extensionLogOutputChannel.error(`Cargo host target probe failed to start in ${workingDirectory} (unknown error).`);
                resolve(undefined);
                return;
            }

            let settled = false;
            let cancellation: vscode.Disposable | undefined;
            let forceKillTimer: ReturnType<typeof setTimeout> | undefined;
            let timeout: ReturnType<typeof setTimeout> | undefined;
            let stdout = '';

            const onStdout = (output: string): void => {
                if (stdout.length < cargoHostProbeOutputLimit) {
                    stdout += output.slice(0, cargoHostProbeOutputLimit - stdout.length);
                }
            };

            const settle = (complete: () => void): void => {
                if (settled) {
                    return;
                }

                settled = true;
                if (timeout) {
                    clearTimeout(timeout);
                    timeout = undefined;
                }
                cancellation?.dispose();
                probeProcess.stdout.off('data', onStdout);
                complete();
            };

            const stopTracking = (): void => {
                if (forceKillTimer) {
                    clearTimeout(forceKillTimer);
                    forceKillTimer = undefined;
                }
                probeProcess.off('error', onError);
                probeProcess.off('close', onClose);
            };

            const terminateProbe = (force: boolean): void => {
                try {
                    terminateProcessTree(probeProcess, force);
                } catch {
                    // Process-tree termination handles its platform fallbacks internally. If direct
                    // child signalling still throws, the probe must not block launch or cancellation.
                    extensionLogOutputChannel.warn('Cargo host target probe termination failed.');
                }
            };

            const terminateProbeAfterGracePeriod = (): void => {
                // Install the escalation timer before signalling the process so a synchronous close
                // can clear it through stopTracking instead of leaving a stale timer behind.
                forceKillTimer = setTimeout(() => {
                    forceKillTimer = undefined;
                    if (probeProcess.exitCode === null && probeProcess.signalCode === null) {
                        terminateProbe(true);
                    }
                }, cargoHostProbeShutdownGracePeriodMs);
                forceKillTimer.unref();
                terminateProbe(false);
            };

            const onError = (err: Error): void => {
                if (settled) {
                    // Process-tree termination can report an asynchronous kill failure after the probe
                    // promise has settled. Keep consuming errors until close so EventEmitter does not
                    // treat them as unhandled exceptions.
                    return;
                }

                stopTracking();
                settle(() => {
                    const errorCode = (err as NodeJS.ErrnoException).code ?? 'unknown error';
                    extensionLogOutputChannel.error(`Cargo host target probe failed to start in ${workingDirectory} (${errorCode}).`);
                    resolve(undefined);
                });
            };

            const onClose = (code: number | null, signal: NodeJS.Signals | null): void => {
                stopTracking();
                settle(() => {
                    if (code !== 0) {
                        const exitDescription = code !== null ? `${code}` : `${signal}`;
                        extensionLogOutputChannel.error(`Cargo host target probe failed in ${workingDirectory} (${exitDescription}).`);
                        resolve(undefined);
                        return;
                    }

                    const target = parseCargoHostTarget(stdout);
                    if (!target) {
                        extensionLogOutputChannel.error(`Cargo host target probe returned no host target in ${workingDirectory}.`);
                    }
                    resolve(target);
                });
            };

            probeProcess.stdout.setEncoding('utf8');
            probeProcess.stdout.on('data', onStdout);
            // A custom Cargo wrapper can write arbitrary diagnostics. Drain stderr so it cannot block
            // the process, but never retain or log it because it can echo resource environment values.
            probeProcess.stderr.resume();
            probeProcess.on('error', onError);
            probeProcess.on('close', onClose);

            const pendingStartCancellation = this._debugSession.registerPendingStartCancellation({
                dispose: () => {
                    if (probeProcess.exitCode === null && probeProcess.signalCode === null) {
                        settle(() => {
                            extensionLogOutputChannel.info(`Debug session ended; stopping Cargo host target probe in ${workingDirectory}.`);
                            terminateProbeAfterGracePeriod();
                            reject(new vscode.CancellationError());
                        });
                    }
                }
            });
            if (settled) {
                pendingStartCancellation.dispose();
                return;
            }

            cancellation = pendingStartCancellation;
            timeout = setTimeout(() => {
                settle(() => {
                    extensionLogOutputChannel.warn(`Cargo host target probe timed out after ${cargoHostProbeTimeoutMs}ms; continuing without automatic target detection.`);
                    terminateProbe(true);
                    resolve(undefined);
                });
            }, cargoHostProbeTimeoutMs);
            timeout.unref();
        });
    }

    build(
        workingDirectory: string,
        cargoArgs: string[],
        env: EnvVar[],
        executablePath: string | undefined
    ): Promise<string> {
        return new Promise<string>((resolve, reject) => {
            extensionLogOutputChannel.info(`Building Rust application in ${workingDirectory} using cargo.`);

            // App hosts predating executable_path relied on the extension to inspect Cargo's artifact
            // messages. Keep that protocol fallback while newer hosts use their metadata-derived path.
            const discoverExecutable = !executablePath;
            const buildArgs = discoverExecutable
                ? withJsonMessageFormat(cargoArgs)
                : cargoArgs;

            // Build with the resource's environment so settings the app host injects (RUSTFLAGS,
            // CARGO_*, proxy variables, and anything set with WithEnvironment) apply to the debug
            // build exactly as they do when DCP runs `cargo run` itself.
            const buildEnv = createCargoEnvironment(env);

            const buildProcess = spawn(cargoExecutable, buildArgs, {
                cwd: workingDirectory,
                env: buildEnv,
                // Cargo fans out into rustc, the linker and any build scripts. Making it a process group
                // leader is what lets the cancellation below take those down with it.
                ...processGroupSpawnOptions()
            });

            let cancellationRequested = false;
            let settled = false;
            let cancellation: vscode.Disposable | undefined;
            let stderrTail = '';
            let stderrTruncated = false;
            const executablesByTarget = new Map<string, string>();
            const sensitiveValues = [...new Set([
                ...getSensitiveEnvironmentValues(buildEnv),
                ...getSensitiveCargoArgumentValues(cargoArgs),
            ].filter(value => value.length > 0))]
                .sort((left, right) => right.length - left.length);

            const settle = (complete: () => void): void => {
                if (settled) {
                    return;
                }

                settled = true;
                cancellation?.dispose();
                complete();
            };

            buildProcess.stdout.setEncoding('utf8');
            buildProcess.stderr.setEncoding('utf8');

            if (discoverExecutable) {
                // Older launch metadata does not contain executable_path. Cargo's JSON stream is:
                //   {"reason":"compiler-artifact","target":{"name":"api","kind":["bin"]},"executable":"/repo/target/debug/api"}
                // Compiler diagnostics are rendered separately and non-JSON shim output is passed through.
                const outputLines = readline.createInterface({ input: buildProcess.stdout });
                outputLines.on('line', line => {
                    let message: CargoBuildMessage;
                    try {
                        message = JSON.parse(line) as CargoBuildMessage;
                    } catch {
                        this.writeToDebugConsole(`${line}\n`, 'stdout');
                        return;
                    }

                    if (message.reason === 'compiler-message') {
                        const compilerMessage = message as CargoCompilerMessage;
                        const rendered = compilerMessage.message?.rendered;
                        if (rendered) {
                            this.writeToDebugConsole(rendered, compilerMessage.message?.level === 'error' ? 'stderr' : 'stdout');
                        }
                    } else if (message.reason === 'compiler-artifact') {
                        collectExecutableArtifact(executablesByTarget, message as CargoCompilerArtifactMessage);
                    }
                });
            } else {
                buildProcess.stdout.on('data', (output: string) => this.writeToDebugConsole(output, 'stdout'));
            }

            // cargo writes its progress and all compiler diagnostics to stderr, so this carries the output a
            // user needs to fix a broken build, not just failures.
            buildProcess.stderr.on('data', (output: string) => {
                const retained = appendBoundedTail(
                    stderrTail,
                    output,
                    rustBuildStderrTailLimit,
                    sensitiveValues);
                stderrTail = retained.value;
                stderrTruncated ||= retained.truncated;
                this.writeToDebugConsole(output, 'stderr');
            });

            buildProcess.on('error', err => {
                settle(() => {
                    if (cancellationRequested) {
                        reject(new vscode.CancellationError());
                        return;
                    }

                    const errorCode = (err as NodeJS.ErrnoException).code ?? err.name;
                    extensionLogOutputChannel.error(`cargo build process failed to start in ${workingDirectory} (${errorCode}).`);
                    reject(new Error(rustBuildFailedWithError(workingDirectory, err.message)));
                });
            });

            buildProcess.on('close', (code, signal) => {
                settle(() => {
                    if (cancellationRequested) {
                        reject(new vscode.CancellationError());
                        return;
                    }

                    if (code !== 0) {
                        // A build killed by a signal reports a null exit code, so name the signal instead of
                        // rendering "exit code null". stderr has already been streamed to the debug console,
                        // but repeating a bounded tail keeps the last diagnostics visible in the notification
                        // without retaining an arbitrarily large compiler transcript.
                        const exitDescription = code !== null ? `${code}` : `${signal}`;
                        const error = rustBuildFailedWithExitCode(workingDirectory, exitDescription);
                        const redactedStderrTail = redactSensitiveValues(stderrTail, sensitiveValues);
                        const stderrDetails = stderrTruncated
                            ? `${rustBuildStderrTruncated(rustBuildStderrTailLimit)}\n${redactedStderrTail}`
                            : redactedStderrTail;
                        reject(new Error(stderrDetails ? `${error}\n${stderrDetails}` : error));
                        return;
                    }

                    if (executablePath) {
                        resolve(executablePath);
                        return;
                    }

                    try {
                        resolve(selectExecutable(workingDirectory, executablesByTarget));
                    } catch (err) {
                        reject(err);
                    }
                });
            });

            // A build can outlive the session that asked for it (cargo waits on its own package lock,
            // and a cold build takes minutes). Register after the process listeners so a session that
            // is already stopping cannot terminate cargo before its close event can be observed.
            cancellation = this._debugSession.registerPendingStartCancellation({
                dispose: () => {
                    if (buildProcess.exitCode === null && buildProcess.signalCode === null) {
                        cancellationRequested = true;
                        extensionLogOutputChannel.info(`Debug session ended; stopping cargo build in ${workingDirectory}.`);
                        terminateProcessTree(buildProcess);
                    }
                }
            });
        });
    }
}

const runnableTargetKinds = ['bin', 'example'];

function createCargoEnvironment(env: EnvVar[]): Record<string, string | undefined> {
    const cargoEnvironment: Record<string, string | undefined> = { ...process.env };
    mergeCliSpawnEnvironment(cargoEnvironment, env);
    return cargoEnvironment;
}

function parseCargoHostTarget(output: string): string | undefined {
    // `cargo -Vv` emits one field per line, for example:
    //   cargo 1.89.0 (c24e10642 2025-06-23)
    //   release: 1.89.0
    //   host: x86_64-pc-windows-msvc
    return output.match(/^host:\s*(\S+)\s*$/im)?.[1];
}

function withJsonMessageFormat(cargoArgs: string[]): string[] {
    const separatorIndex = cargoArgs.indexOf('--');
    const cargoArgumentCount = separatorIndex >= 0 ? separatorIndex : cargoArgs.length;
    const normalizedArgs: string[] = [];
    let messageFormatIndex: number | undefined;

    for (let index = 0; index < cargoArgumentCount; index++) {
        const argument = cargoArgs[index];
        if (argument === '--message-format') {
            messageFormatIndex ??= normalizedArgs.length;
            if (index + 1 < cargoArgumentCount && !cargoArgs[index + 1].startsWith('-')) {
                index++;
            }
        } else if (argument.startsWith('--message-format=')) {
            messageFormatIndex ??= normalizedArgs.length;
        } else {
            normalizedArgs.push(argument);
        }
    }

    normalizedArgs.splice(messageFormatIndex ?? normalizedArgs.length, 0, '--message-format=json');
    if (separatorIndex >= 0) {
        normalizedArgs.push(...cargoArgs.slice(separatorIndex));
    }

    return normalizedArgs;
}

function collectExecutableArtifact(
    executablesByTarget: Map<string, string>,
    message: CargoCompilerArtifactMessage
): void {
    const targetName = message.target?.name;
    const targetKind = message.target?.kind?.find(kind => runnableTargetKinds.includes(kind));
    if (message.executable && targetName && targetKind) {
        executablesByTarget.set(`${targetKind}/${targetName}`, message.executable);
    }
}

function selectExecutable(workingDirectory: string, executablesByTarget: Map<string, string>): string {
    if (executablesByTarget.size === 0) {
        throw new Error(rustBuildProducedNoExecutable(workingDirectory));
    }

    if (executablesByTarget.size > 1) {
        throw new Error(rustBuildProducedMultipleExecutables(
            workingDirectory,
            [...executablesByTarget.keys()].sort().join(', ')));
    }

    return [...executablesByTarget.values()][0];
}

function getSensitiveCargoArgumentValues(cargoArgs: string[]): string[] {
    const values: string[] = [];
    for (let index = 0; index < cargoArgs.length; index++) {
        const argument = cargoArgs[index];
        if (argument === '--config' && cargoArgs[index + 1]) {
            addSensitiveCargoConfigurationValues(values, cargoArgs[++index]);
            continue;
        }

        if (argument.startsWith('--config=')) {
            addSensitiveCargoConfigurationValues(values, argument.substring('--config='.length));
            continue;
        }

        const equalsIndex = argument.indexOf('=');
        if (equalsIndex >= 0 && isSensitiveArgumentName(argument.slice(0, equalsIndex))) {
            addSensitiveArgumentValue(values, argument.slice(equalsIndex + 1));
            continue;
        }

        if (isSensitiveArgumentName(argument) && cargoArgs[index + 1]) {
            addSensitiveArgumentValue(values, cargoArgs[++index]);
        }
    }

    return values;
}

function addSensitiveCargoConfigurationValues(values: string[], configuration: string): void {
    const equalsIndex = configuration.indexOf('=');
    if (equalsIndex < 0 || !containsSensitiveCargoConfigurationKey(configuration)) {
        return;
    }

    addSensitiveArgumentValue(values, configuration.slice(equalsIndex + 1));
}

function containsSensitiveCargoConfigurationKey(configuration: string): boolean {
    let keyStart = 0;
    let quote: '"' | "'" | undefined;
    let escaped = false;

    // Cargo normally accepts dotted keys such as `registries.private.token = "..."`, but it
    // echoes rejected payloads such as `env = { PGPASSWORD = "..." }`. Inspect each assignment
    // key so secrets nested in an invalid inline table are still removed from the retained error.
    for (let index = 0; index < configuration.length; index++) {
        const character = configuration[index];
        if (quote !== undefined) {
            if (quote === '"' && character === '\\' && !escaped) {
                escaped = true;
                continue;
            }

            if (escaped) {
                escaped = false;
                continue;
            }

            if (character === quote) {
                quote = undefined;
            }
            continue;
        }

        if (character === '"' || character === "'") {
            quote = character;
        } else if (character === '{' || character === ',') {
            keyStart = index + 1;
        } else if (character === '=' && isSensitiveCargoConfigurationKey(configuration.slice(keyStart, index))) {
            return true;
        }
    }

    return false;
}

function isSensitiveCargoConfigurationKey(key: string): boolean {
    return isSensitiveArgumentName(key.replace(/[^A-Za-z0-9_-]+/g, '.'));
}

function addSensitiveArgumentValue(values: string[], value: string): void {
    const trimmedValue = value.trim();
    if (!trimmedValue) {
        return;
    }

    values.push(trimmedValue);
    for (const match of trimmedValue.matchAll(/"(?:\\.|[^"\\])*"|'[^']*'/g)) {
        const quotedValue = match[0];
        values.push(quotedValue);
        const decodedValue = decodeTomlQuotedString(quotedValue);
        if (decodedValue) {
            values.push(decodedValue);
        }
    }
}

function decodeTomlQuotedString(value: string): string {
    if (value.startsWith("'")) {
        return value.slice(1, -1);
    }

    try {
        // TOML basic strings largely share JSON's escapes. Normalize TOML's additional
        // eight-digit Unicode form before asking JSON to decode it.
        // See https://toml.io/en/v1.0.0#string.
        const jsonCompatible = value
            .replace(/\\U([0-9a-fA-F]{8})/g, (_, hex: string) => JSON.stringify(String.fromCodePoint(Number.parseInt(hex, 16))).slice(1, -1));
        return JSON.parse(jsonCompatible) as string;
    } catch {
        return value.slice(1, -1);
    }
}

function getSensitiveEnvironmentValues(environment: Record<string, string | undefined>): string[] {
    return Object.entries(environment)
        .filter(([name, value]) => value && isSensitiveArgumentName(name))
        .map(([, value]) => value!);
}

function isSensitiveArgumentName(argument: string): boolean {
    const normalizedArgument = argument.trim().replace(/^-+/, '');
    return /(?:^|[._-])(?:PGPASSWORD|MYSQL_PWD)(?:$|[._-])/i.test(normalizedArgument)
        || /(?:^|[._-])(?:url|uri)(?:$|[._-])/i.test(normalizedArgument)
        || /(?:^|[._-])(?:token|password|passwd|secret|credential|api[_-]?key|access[_-]?key|private[_-]?key|client[_-]?secret|connection[_-]?strings?)(?:$|[._-])/i
            .test(normalizedArgument);
}

function appendBoundedTail(
    current: string,
    output: string,
    limit: number,
    sensitiveValues: string[]
): { truncated: boolean; value: string } {
    const combined = current + output;
    if (combined.length <= limit) {
        return { truncated: false, value: combined };
    }

    let start = advancePastSensitiveValueBoundary(combined, combined.length - limit, sensitiveValues);
    // Avoid retaining only the low surrogate when the tail boundary lands in the middle of a
    // supplementary Unicode character such as an emoji.
    if (start > 0
        && isLowSurrogate(combined.charCodeAt(start))
        && isHighSurrogate(combined.charCodeAt(start - 1))) {
        start++;
    }

    return { truncated: true, value: combined.slice(start) };
}

function advancePastSensitiveValueBoundary(
    value: string,
    initialStart: number,
    sensitiveValues: string[]
): number {
    let start = initialStart;
    while (start > 0) {
        let adjustedStart = start;
        for (const sensitiveValue of sensitiveValues) {
            const occurrence = value.lastIndexOf(sensitiveValue, start - 1);
            if (occurrence >= 0 && occurrence < start && occurrence + sensitiveValue.length > start) {
                adjustedStart = Math.max(adjustedStart, occurrence + sensitiveValue.length);
            }
        }

        if (adjustedStart === start) {
            break;
        }

        start = adjustedStart;
    }

    return start;
}

function redactSensitiveValues(value: string, sensitiveValues: string[]): string {
    let redacted = value;
    for (const sensitiveValue of sensitiveValues) {
        redacted = redacted.split(sensitiveValue).join(rustBuildOutputRedacted);
    }

    return redacted;
}

function isHighSurrogate(value: number): boolean {
    return value >= 0xD800 && value <= 0xDBFF;
}

function isLowSurrogate(value: number): boolean {
    return value >= 0xDC00 && value <= 0xDFFF;
}

function asRustConfig(launchConfig: ExecutableLaunchConfiguration): RustLaunchConfiguration {
    if (isRustLaunchConfiguration(launchConfig)) {
        return launchConfig;
    }

    const message = invalidLaunchConfiguration(rustLabel);
    extensionLogOutputChannel.info(message);
    throw new Error(message);
}

function getProjectFile(launchConfig: ExecutableLaunchConfiguration): string {
    const config = asRustConfig(launchConfig);
    return config.working_directory || '';
}

export function createRustDebuggerExtension(
    rustServiceProducer: (debugSession: AspireDebugSession) => IRustService,
    platform: NodeJS.Platform = process.platform,
    isExtensionInstalled: (extensionId: string) => boolean = extensionId => !!vscode.extensions.getExtension(extensionId)
): ResourceDebuggerExtension {
    // Rust has no cross-platform native debugger extension: the Microsoft C++ extension's Windows-only
    // cppvsdbg engine understands the CodeView/PDB output produced by the MSVC Rust toolchain, while
    // CodeLLDB is the extension VS Code's own docs recommend for macOS/Linux.
    const rustExtensionId = getRustExtensionId(platform, isExtensionInstalled);
    const rustDebugAdapter = rustExtensionId === 'ms-vscode.cpptools' ? 'cppvsdbg' : 'lldb';

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
            // The build uses resolved resource environment values, which can include secrets. Keep the
            // debugger configuration available to the adapter without allowing the diagnostic setting
            // that logs other launch environments to persist this one.
            markDebugConfigurationEnvironmentSensitive(debugConfiguration);
            const config = asRustConfig(launchConfig);
            const workingDirectory = config.working_directory || '';
            const cargoArgs = config.cargo?.args ?? ['build'];
            let cargoTarget = getCargoTarget(
                cargoArgs,
                env ?? [],
                process.env,
                config.cargo?.executable_path);
            const rustService = rustServiceProducer(launchOptions.debugSession);
            const configuredAdapter = debugConfiguration.type;
            const selectedAdapter = !configuredAdapter || configuredAdapter === launchConfig.type
                ? rustDebugAdapter
                : configuredAdapter;
            // Cargo can resolve through a different rustup proxy or wrapper for each resource
            // environment, so probing per launch is safer than caching a process-wide host triple.
            if (launchOptions.debug
                && platform === 'win32'
                && selectedAdapter === 'cppvsdbg'
                && !cargoTarget) {
                cargoTarget = await rustService.getCargoHostTarget(workingDirectory, env ?? []);
            }

            const isGnuTarget = isGnuWindowsTarget(cargoTarget);

            // GNU Windows Rust targets emit DWARF debug information, while cppvsdbg is the Visual Studio
            // Windows debugger and expects the native Windows CodeView/PDB path. CodeLLDB can consume the
            // GNU target's symbols when installed; otherwise fail before spending time on a build that the
            // selected adapter cannot debug.
            // See:
            // - https://github.com/rust-lang/rust/blob/master/compiler/rustc_target/src/spec/base/windows_gnu.rs
            // - https://code.visualstudio.com/docs/cpp/cpp-debug#_windows-debugging-with-gdb
            // NoDebug still launches through an adapter, but it does not ask that adapter to consume
            // the binary's debug symbols, so the CodeView-versus-DWARF restriction does not apply.
            const hasGnuCompatibleAdapter = configuredAdapter === 'cppdbg' || configuredAdapter === 'lldb';
            const needsGnuDebugger = launchOptions.debug
                && platform === 'win32'
                && isGnuTarget
                && !hasGnuCompatibleAdapter;
            const useCodeLldb = needsGnuDebugger
                && isExtensionInstalled('vadimcn.vscode-lldb');
            if (needsGnuDebugger && !useCodeLldb) {
                throw new Error(rustWindowsGnuDebuggerUnsupported(cargoTarget ?? 'windows-gnu'));
            }

            const executablePath = await rustService.build(
                workingDirectory,
                cargoArgs,
                env ?? [],
                config.cargo?.executable_path);

            debugConfiguration.program = executablePath;
            debugConfiguration.cwd = workingDirectory;
            debugConfiguration.args = args ?? [];

            if (useCodeLldb) {
                // A user override cannot make cppvsdbg understand DWARF, so the compatible fallback
                // deliberately wins for this target. Otherwise preserve the configured adapter.
                debugConfiguration.type = 'lldb';
            } else if (!debugConfiguration.type || debugConfiguration.type === launchConfig.type) {
                debugConfiguration.type = rustDebugAdapter;
            }

            const effectiveDebugAdapter = debugConfiguration.type;
            if (effectiveDebugAdapter === 'cppvsdbg' || effectiveDebugAdapter === 'cppdbg') {
                debugConfiguration.console = 'internalConsole';

                // cppvsdbg (and cppdbg) read environment variables from "environment" as a name/value
                // array; they ignore the "env" object that createDebugSessionConfiguration populates for
                // every other debug adapter, so translate it here.
                const env = debugConfiguration.env as Record<string, string | undefined> | undefined;
                debugConfiguration.environment = Object.entries(env ?? {}).map(([name, value]) => ({ name, value: value ?? '' }));
            } else if (effectiveDebugAdapter === 'lldb') {
                // CodeLLDB already understands the "env" object populated by createDebugSessionConfiguration.
                debugConfiguration.sourceLanguages = ['rust'];
            }
        }
    };
}

function getCargoTarget(
    cargoArgs: string[],
    env: EnvVar[],
    ambientEnvironment: NodeJS.ProcessEnv,
    executablePath: string | undefined
): string | undefined {
    let target: string | undefined;
    for (let index = 0; index < cargoArgs.length; index++) {
        const argument = cargoArgs[index];
        if (argument === '--target') {
            const value = cargoArgs[index + 1];
            if (value) {
                target = value;
                index++;
            }
        } else if (argument.startsWith('--target=')) {
            const value = argument.substring('--target='.length);
            if (value) {
                target = value;
            }
        }
    }

    if (target) {
        return target;
    }

    const configuredTarget = env.find(variable => variable.name.toUpperCase() === 'CARGO_BUILD_TARGET');
    if (configuredTarget) {
        return configuredTarget.value || undefined;
    }

    const ambientTarget = Object.entries(ambientEnvironment)
        .find(([name]) => name.toUpperCase() === 'CARGO_BUILD_TARGET')?.[1];
    if (ambientTarget) {
        return ambientTarget;
    }

    // Cross-target Cargo outputs include the target triple as a path segment, for example:
    //   target\x86_64-pc-windows-gnullvm\debug\api.exe
    return executablePath?.match(/(?:^|[\\/])([^\\/]*-windows-(?:gnu|gnullvm|msvc))(?:[\\/]|$)/i)?.[1];
}

function isGnuWindowsTarget(target: string | undefined): boolean {
    const normalized = target?.trim().toLowerCase();
    return normalized?.endsWith('-windows-gnu') === true
        || normalized?.endsWith('-windows-gnullvm') === true;
}

export function createDefaultRustDebuggerExtension(
    platform: NodeJS.Platform = process.platform
): ResourceDebuggerExtension {
    return createRustDebuggerExtension(debugSession => new RustService(debugSession), platform);
}
