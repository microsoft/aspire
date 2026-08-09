import * as vscode from 'vscode';
import { AspireDebugSession } from './debugger/AspireDebugSession';
import { AspireDebugConfigurationProvider } from './debugger/AspireDebugConfigurationProvider';
import { debugSessionAlreadyExists, extensionContextNotInitialized } from './loc/strings';
import AspireRpcServer from './server/AspireRpcServer';
import AspireDcpServer from './dcp/AspireDcpServer';
import { AspireTerminalProvider } from './utils/AspireTerminalProvider';
import { AspireEditorCommandProvider } from './editor/AspireEditorCommandProvider';
import type { AspireDebugConsoleOutputEvent } from './types/extensionApi';

export class AspireExtensionContext implements vscode.Disposable {
    private _rpcServer?: AspireRpcServer;
    private _dcpServer?: AspireDcpServer;
    private _extensionContext?: vscode.ExtensionContext;
    private _debugConfigProvider?: AspireDebugConfigurationProvider;
    private _terminalProvider?: AspireTerminalProvider;
    private _editorCommandProvider?: AspireEditorCommandProvider;

    private _aspireDebugSessions: AspireDebugSession[] = [];
    private readonly _debugSessionStateSubscriptions = new Map<string, vscode.Disposable>();
    private readonly _debugSessionOutputSubscriptions = new Map<string, vscode.Disposable>();
    private readonly _onDidChangeDebugSessions = new vscode.EventEmitter<void>();
    private readonly _onDidReceiveDebugConsoleOutput = new vscode.EventEmitter<AspireDebugConsoleOutputEvent>();
    readonly onDidChangeDebugSessions = this._onDidChangeDebugSessions.event;
    readonly onDidReceiveDebugConsoleOutput = this._onDidReceiveDebugConsoleOutput.event;

    initialize(rpcServer: AspireRpcServer, extensionContext: vscode.ExtensionContext, debugConfigProvider: AspireDebugConfigurationProvider, dcpServer: AspireDcpServer, terminalProvider: AspireTerminalProvider, editorCommandProvider: AspireEditorCommandProvider): void {
        this._rpcServer = rpcServer;
        this._extensionContext = extensionContext;
        this._debugConfigProvider = debugConfigProvider;
        this._dcpServer = dcpServer;
        this._terminalProvider = terminalProvider;
        this._editorCommandProvider = editorCommandProvider;
    }

    get rpcServer(): AspireRpcServer {
        if (!this._rpcServer) {
            throw new Error(extensionContextNotInitialized);
        }
        return this._rpcServer;
    }

    get dcpServer(): AspireDcpServer {
        if (!this._dcpServer) {
            throw new Error(extensionContextNotInitialized);
        }
        return this._dcpServer;
    }

    get extensionContext(): vscode.ExtensionContext {
        if (!this._extensionContext) {
            throw new Error(extensionContextNotInitialized);
        }
        return this._extensionContext;
    }

    getAspireDebugSession(debugSessionId: string | null): AspireDebugSession | null {
        if (!debugSessionId) {
            return null;
        }

        return this._aspireDebugSessions.find(session => session.debugSessionId === debugSessionId) || null;
    }

    get aspireDebugSessions(): readonly AspireDebugSession[] {
        return [...this._aspireDebugSessions];
    }

    addAspireDebugSession(debugSession: AspireDebugSession) {
        if (this._aspireDebugSessions.find(session => session.debugSessionId === debugSession.debugSessionId)) {
            throw new Error(debugSessionAlreadyExists(debugSession.debugSessionId));
        }

        this._aspireDebugSessions.push(debugSession);
        this._debugSessionStateSubscriptions.set(debugSession.debugSessionId, debugSession.onDidChangeState(() => this._onDidChangeDebugSessions.fire()));
        this._debugSessionOutputSubscriptions.set(debugSession.debugSessionId, debugSession.onDidSendDebugConsoleOutput(event => this._onDidReceiveDebugConsoleOutput.fire(event)));
        this._onDidChangeDebugSessions.fire();
    }

    removeAspireDebugSession(debugSession: AspireDebugSession) {
        this._aspireDebugSessions = this._aspireDebugSessions.filter(session => session.debugSessionId !== debugSession.debugSessionId);
        this._debugSessionStateSubscriptions.get(debugSession.debugSessionId)?.dispose();
        this._debugSessionStateSubscriptions.delete(debugSession.debugSessionId);
        this._debugSessionOutputSubscriptions.get(debugSession.debugSessionId)?.dispose();
        this._debugSessionOutputSubscriptions.delete(debugSession.debugSessionId);
        this._onDidChangeDebugSessions.fire();
    }

    get debugConfigProvider(): AspireDebugConfigurationProvider | undefined {
        if (!this._debugConfigProvider) {
            throw new Error(extensionContextNotInitialized);
        }

        return this._debugConfigProvider;
    }

    dispose() {
        this._rpcServer?.dispose();
        this._dcpServer?.dispose();
        this._debugSessionStateSubscriptions.forEach(disposable => disposable.dispose());
        this._debugSessionStateSubscriptions.clear();
        this._debugSessionOutputSubscriptions.forEach(disposable => disposable.dispose());
        this._debugSessionOutputSubscriptions.clear();
        this._aspireDebugSessions.forEach(session => session.dispose());
        this._terminalProvider?.dispose();
        this._editorCommandProvider?.dispose();
        this._onDidChangeDebugSessions.dispose();
        this._onDidReceiveDebugConsoleOutput.dispose();
    }
}
