import * as vscode from 'vscode';
import { AspireDebugSession, type AppHostDebugSessionTracker } from './AspireDebugSession';
import AspireDcpServer from '../dcp/AspireDcpServer';
import AspireRpcServer from '../server/AspireRpcServer';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { stripAspireDebugConfigurationProviderInternalProperties } from './AspireDebugConfigurationProviderInternal';

export class AspireDebugAdapterDescriptorFactory implements vscode.DebugAdapterDescriptorFactory {
  private readonly _rpcServer: AspireRpcServer;
  private readonly _dcpServer: AspireDcpServer;
  private readonly _terminalProvider: AspireTerminalProvider;
  private readonly _addAspireDebugSession: (session: AspireDebugSession) => void;
  private readonly _removeAspireDebugSession: (session: AspireDebugSession) => void;
  private readonly _trackAppHostDebugSession: AppHostDebugSessionTracker;

  constructor(rpcServer: AspireRpcServer, dcpServer: AspireDcpServer, terminalProvider: AspireTerminalProvider, addAspireDebugSession: (session: AspireDebugSession) => void, removeAspireDebugSession: (session: AspireDebugSession) => void, trackAppHostDebugSession: AppHostDebugSessionTracker) {
    this._rpcServer = rpcServer;
    this._dcpServer = dcpServer;
    this._terminalProvider = terminalProvider;
    this._addAspireDebugSession = addAspireDebugSession;
    this._removeAspireDebugSession = removeAspireDebugSession;
    this._trackAppHostDebugSession = trackAppHostDebugSession;
  }

  async createDebugAdapterDescriptor(session: vscode.DebugSession,  executable: vscode.DebugAdapterExecutable | undefined): Promise<vscode.DebugAdapterDescriptor> {
    stripAspireDebugConfigurationProviderInternalProperties(session.configuration);
    const aspireDebugSession = new AspireDebugSession(session, this._rpcServer, this._dcpServer, this._terminalProvider, this._removeAspireDebugSession, this._trackAppHostDebugSession);
    this._addAspireDebugSession(aspireDebugSession);
    return new vscode.DebugAdapterInlineImplementation(aspireDebugSession);
  }
}
