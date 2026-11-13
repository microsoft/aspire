import * as fs from 'fs';
import { randomUUID } from 'crypto';
import * as vscode from 'vscode';
import type { AspireDebugSession } from '../debugger/AspireDebugSession';
import { getLoggableDebugConfiguration } from '../debugger/AspireDebugSession';
import { createDebugSessionConfiguration, getResourceDebuggerExtensions } from '../debugger/debuggerExtensions';
import { cleanupRun } from '../debugger/runCleanupRegistry';
import type { AspireExtendedDebugConfiguration, EnvVar, ProjectLaunchConfiguration } from '../dcp/types';
import { isSamePath } from '../extensionState';
import { delay } from '../utils/async';

export interface DotnetResourceLaunchProofOptions {
    launchConfig: ProjectLaunchConfiguration;
    sourcePath: string;
    breakpointLine: number;
    readyFilePath: string;
    debug: boolean;
    args?: readonly string[];
    env?: readonly EnvVar[];
    timeoutMs?: number;
}

/**
 * Exercises the resource callback and the installed coreclr adapter, rather than treating a
 * generated configuration (or a process-only fallback) as proof that a language can be debugged.
 */
export async function proveDotnetResourceLaunch(options: DotnetResourceLaunchProofOptions): Promise<unknown> {
    const debuggerExtension = getResourceDebuggerExtensions().find(extension =>
        extension.resourceType === options.launchConfig.type);
    if (!debuggerExtension) {
        throw new Error(`No .NET resource debugger is registered for '${options.launchConfig.type}'. Install the C# debugger VSIX.`);
    }

    const runId = `e2e-dotnet-resource-${randomUUID()}`;
    const sessions = new Map<string, vscode.DebugSession>();
    const terminatedSessions = new Set<string>();
    const launchRequests: Array<{ sessionId: string; type: string; noDebug?: boolean }> = [];
    const breakpointResponses: Array<{ success: boolean; breakpoints?: unknown }> = [];
    const stoppedEvents: Array<{ sessionId: string; reason?: string; threadId?: number }> = [];
    const exitEvents: Array<{ sessionId: string; exitCode?: number }> = [];
    const adapterErrors: string[] = [];
    const output: string[] = [];
    const failures: string[] = [];
    const timeoutMs = options.timeoutMs ?? 120000;
    const deadline = Date.now() + timeoutMs;
    const subscriptions: vscode.Disposable[] = [];
    let breakpoint: vscode.SourceBreakpoint | undefined;

    const diagnostics = () => JSON.stringify({
        launchRequests,
        breakpointResponses,
        stoppedEvents,
        exitEvents,
        adapterErrors,
        failures,
        output: output.slice(-20),
    });
    const waitFor = async <T>(description: string, getValue: () => T | undefined | Promise<T | undefined>): Promise<T> => {
        while (Date.now() < deadline) {
            if (failures.length > 0) {
                throw new Error(`${description} failed: ${diagnostics()}`);
            }
            const value = await getValue();
            if (value !== undefined) {
                return value;
            }
            await delay(50);
        }
        throw new Error(`Timed out after ${timeoutMs}ms waiting for ${description}. ${diagnostics()}`);
    };

    try {
        if (debuggerExtension.extensionId) {
            const extension = vscode.extensions.getExtension(debuggerExtension.extensionId);
            if (!extension) {
                throw new Error(`The debugger extension '${debuggerExtension.extensionId}' is no longer installed.`);
            }
            // The first activation installs debugger components. Await it before requesting a session.
            await extension.activate();
        }
        const parentConfiguration: AspireExtendedDebugConfiguration = {
            type: 'aspire',
            request: 'launch',
            name: 'E2E .NET resource',
            program: '',
        };
        const configuration = await createDebugSessionConfiguration(
            parentConfiguration,
            options.launchConfig,
            options.args ? [...options.args] : undefined,
            options.env ? [...options.env] : [],
            {
                debug: options.debug,
                forceBuild: false,
                runId,
                debugSessionId: runId,
                isApphost: false,
                debugSession: { configuration: parentConfiguration } as AspireDebugSession,
            },
            debuggerExtension);
        if (configuration.type !== 'coreclr' || configuration.noDebug !== !options.debug || configuration.isApphost !== false) {
            throw new Error(`The resource did not preserve the requested Run/Debug mode: ${JSON.stringify(getLoggableDebugConfiguration(configuration, false))}`);
        }
        if (fs.existsSync(options.readyFilePath)) {
            throw new Error(`Readiness file already exists before the resource launch: ${options.readyFilePath}`);
        }

        subscriptions.push(
            vscode.debug.onDidStartDebugSession(session => {
                if (session.configuration.runId === runId) {
                    sessions.set(session.id, session);
                }
            }),
            vscode.debug.onDidTerminateDebugSession(session => {
                if (sessions.has(session.id)) {
                    terminatedSessions.add(session.id);
                }
            }),
            vscode.debug.registerDebugAdapterTrackerFactory('coreclr', {
                createDebugAdapterTracker(session) {
                    if (session.configuration.runId !== runId) {
                        return;
                    }
                    return {
                        onWillReceiveMessage(message) {
                            if (message?.type === 'request' && message.command === 'launch') {
                                launchRequests.push({
                                    sessionId: session.id,
                                    type: session.type,
                                    noDebug: message.arguments?.noDebug,
                                });
                            }
                        },
                        onDidSendMessage(message) {
                            if (message?.type === 'response' && message.success === false) {
                                const error = `${message.command}: ${message.message ?? 'adapter request failed'}`;
                                // VS Code can inspect scopes after our immediate continue, when the
                                // process is already running. Only launch/setup failures invalidate
                                // the proof; our stackTrace and continue requests reject directly.
                                if (['launch', 'setBreakpoints', 'configurationDone'].includes(message.command)) {
                                    failures.push(error);
                                } else {
                                    adapterErrors.push(error);
                                }
                            }
                            if (message?.type === 'response' && message.command === 'setBreakpoints') {
                                breakpointResponses.push({
                                    success: message.success === true,
                                    breakpoints: message.body?.breakpoints,
                                });
                            }
                            // DAP emits {"type":"event","event":"stopped","body":{"reason":"breakpoint","threadId":1}}.
                            // Entry/exception pauses are not source-breakpoint proof; the stack must match too.
                            // https://microsoft.github.io/debug-adapter-protocol/specification#Events_Stopped
                            if (message?.type === 'event' && message.event === 'stopped') {
                                stoppedEvents.push({
                                    sessionId: session.id,
                                    reason: message.body?.reason,
                                    threadId: message.body?.threadId,
                                });
                            }
                            if (message?.type === 'event' && message.event === 'output') {
                                output.push(String(message.body?.output ?? ''));
                                if (output.length > 100) {
                                    output.shift();
                                }
                            }
                            if (message?.type === 'event' && message.event === 'exited') {
                                exitEvents.push({ sessionId: session.id, exitCode: message.body?.exitCode });
                            }
                        },
                        onError(error) {
                            // coreclr can report "read error" when its pipe closes after a successful
                            // NoDebug exit. Keep the error for diagnostics, but require exitCode=0,
                            // readiness, and session termination rather than failing on pipe teardown.
                            adapterErrors.push(error.message);
                        },
                    };
                },
            }));

        if (options.debug) {
            breakpoint = new vscode.SourceBreakpoint(new vscode.Location(
                vscode.Uri.file(options.sourcePath),
                new vscode.Position(options.breakpointLine, 0)));
            vscode.debug.addBreakpoints([breakpoint]);
        }

        const workspaceFolder = vscode.workspace.getWorkspaceFolder(vscode.Uri.file(options.launchConfig.project_path));
        if (!await vscode.debug.startDebugging(workspaceFolder, configuration)) {
            throw new Error(`VS Code refused to start the .NET resource. ${diagnostics()}`);
        }

        const session = await waitFor('the coreclr resource session', () => [...sessions.values()][0]);
        let breakpointHit: {
            reason: string;
            sourcePath: string;
            line: number;
        } | undefined;
        if (options.debug) {
            const hit = await waitFor('a resource source breakpoint', async () => {
                for (const event of stoppedEvents) {
                    if (event.sessionId !== session.id || event.reason !== 'breakpoint' || event.threadId === undefined) {
                        continue;
                    }
                    const stack = await session.customRequest('stackTrace', {
                        threadId: event.threadId,
                        startFrame: 0,
                        levels: 20,
                    }) as { stackFrames?: Array<{ source?: { path?: string }; line?: number }> };
                    const frame = stack.stackFrames?.find(candidate =>
                        candidate.source?.path !== undefined &&
                        isSamePath(candidate.source.path, options.sourcePath) &&
                        candidate.line === options.breakpointLine + 1);
                    if (frame) {
                        return { event, frame };
                    }
                }
                return undefined;
            });
            breakpointHit = {
                reason: hit.event.reason!,
                sourcePath: hit.frame.source!.path!,
                line: hit.frame.line!,
            };
            await session.customRequest('continue', { threadId: hit.event.threadId });
        }

        const readiness = await waitFor('resource readiness output', () => {
            if (!fs.existsSync(options.readyFilePath)) {
                return undefined;
            }
            const text = fs.readFileSync(options.readyFilePath, 'utf8');
            return text.length > 0 ? text : undefined;
        });
        const exit = await waitFor('resource exit', () => exitEvents.find(event => event.sessionId === session.id));
        if (exit.exitCode !== 0) {
            throw new Error(`The .NET resource exited with code ${exit.exitCode}. ${diagnostics()}`);
        }
        await waitFor('resource termination', () => terminatedSessions.has(session.id) ? true : undefined);

        return {
            proof: options.debug ? 'dotnet-resource-breakpoint-hit' : 'dotnet-resource-run-completed',
            configuration: getLoggableDebugConfiguration(configuration, false),
            launchRequests,
            breakpointResponses,
            breakpointHit,
            exitCode: exit.exitCode,
            readiness,
            output,
        };
    } finally {
        try {
            for (const session of sessions.values()) {
                if (!terminatedSessions.has(session.id)) {
                    await vscode.debug.stopDebugging(session);
                    const stopDeadline = Date.now() + 15000;
                    while (!terminatedSessions.has(session.id) && Date.now() < stopDeadline) {
                        await delay(50);
                    }
                    if (!terminatedSessions.has(session.id)) {
                        throw new Error(`The .NET resource session '${session.id}' did not terminate after stopping. ${diagnostics()}`);
                    }
                }
            }
        } finally {
            if (breakpoint) {
                vscode.debug.removeBreakpoints([breakpoint]);
            }
            for (const subscription of subscriptions) {
                subscription.dispose();
            }
            cleanupRun(runId);
        }
    }
}
