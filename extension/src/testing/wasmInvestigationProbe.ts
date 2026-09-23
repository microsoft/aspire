import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { randomUUID } from 'crypto';

// Investigation-only instrumentation for #20117; not part of the proposed fix.
export function installWasmInvestigationProbe(logDirectory: string): vscode.Disposable {
    fs.mkdirSync(logDirectory, { recursive: true });
    const logPath = path.join(logDirectory, `wasm-investigation-${randomUUID()}.jsonl`);
    const record = (event: string, session: vscode.DebugSession | undefined, detail?: unknown) => {
        fs.appendFileSync(logPath, `${JSON.stringify({
            observedAt: new Date().toISOString(),
            event,
            sessionId: session?.id,
            sessionType: session?.type,
            sessionName: session?.name,
            parentSessionId: session?.parentSession?.id,
            detail,
        }, (key, value) => /env|token|password|secret|authorization/i.test(key) ? '<redacted>' : value)}\n`);
    };
    const lifecycleCommands = new Set(['initialize', 'launch', 'attach', 'configurationDone', 'disconnect', 'terminate']);
    const lifecycleEvents = new Set(['initialized', 'process', 'exited', 'terminated', 'thread']);
    record('probe-installed', undefined, { platform: process.platform, architecture: process.arch, vscode: vscode.version });

    return vscode.Disposable.from(
        vscode.debug.registerDebugConfigurationProvider('*', {
            resolveDebugConfigurationWithSubstitutedVariables(_folder, configuration) {
                if (configuration.type === 'monovsdbg_wasm') {
                    configuration.logging = { ...configuration.logging, engineLogging: true };
                    record('native-logging-enabled', undefined, { type: configuration.type });
                }
                return configuration;
            },
        }),
        vscode.debug.onDidStartDebugSession(session => record('session-started', session)),
        vscode.debug.onDidTerminateDebugSession(session => record('session-terminated', session)),
        vscode.debug.registerDebugAdapterTrackerFactory('*', {
            createDebugAdapterTracker(session) {
                record('tracker-created', session);
                return {
                    onWillStartSession: () => record('adapter-will-start', session),
                    onWillStopSession: () => record('adapter-will-stop', session),
                    onError: error => record('adapter-error', session, { message: error.message, stack: error.stack }),
                    onExit: (code, signal) => record('adapter-exit', session, { code, signal }),
                    onWillReceiveMessage(message) {
                        if (message?.type === 'request' && lifecycleCommands.has(message.command)) {
                            record('client-to-adapter', session, {
                                sequence: message.seq,
                                command: message.command,
                                arguments: message.command === 'disconnect' || message.command === 'terminate'
                                    ? message.arguments
                                    : {
                                        program: message.arguments?.program,
                                        monoDebuggerOptions: message.arguments?.monoDebuggerOptions,
                                    },
                            });
                        }
                    },
                    onDidSendMessage(message) {
                        if ((message?.type === 'response' && lifecycleCommands.has(message.command))
                            || (message?.type === 'event' && lifecycleEvents.has(message.event))
                            || (session.type === 'monovsdbg_wasm' && message?.type === 'event' && message.event === 'output')) {
                            record('adapter-to-client', session, {
                                sequence: message.seq,
                                requestSequence: message.request_seq,
                                type: message.type,
                                command: message.command,
                                eventName: message.event,
                                success: message.success,
                                message: message.message,
                                body: message.body,
                            });
                        }
                    },
                };
            },
        }),
        new vscode.Disposable(() => record('probe-disposed', undefined)),
    );
}
