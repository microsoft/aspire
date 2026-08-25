import * as vscode from 'vscode';

import { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import { AppHostDataRepository } from '../data/AppHostDataRepository';
import { registerInstrumentedCommand } from './instrumentedCommand';

type TreeElementCommandInvoker = (provider: AspireAppHostTreeProvider, element: any) => unknown;

const treeElementCommands: ReadonlyArray<readonly [commandId: string, invoke: TreeElementCommandInvoker]> = [
  ['aspire-vscode.openDashboard', (p, e) => p.openDashboard(e)],
  ['aspire-vscode.openDashboardToSide', (p, e) => p.openDashboardToSide(e)],
  ['aspire-vscode.openAppHostSource', (p, e) => p.openAppHostSource(e)],
  ['aspire-vscode.stopAppHost', (p, e) => p.stopAppHost(e)],
  ['aspire-vscode.deployAppHost', (p, e) => p.deployAppHost(e)],
  ['aspire-vscode.publishAppHost', (p, e) => p.publishAppHost(e)],
  ['aspire-vscode.runPipelineStepAppHost', (p, e) => p.runPipelineStepAppHost(e)],
  ['aspire-vscode.debugPipelineStepAppHost', (p, e) => p.debugPipelineStepAppHost(e)],
  ['aspire-vscode.stopResource', (p, e) => p.stopResource(e)],
  ['aspire-vscode.startResource', (p, e) => p.startResource(e)],
  ['aspire-vscode.restartResource', (p, e) => p.restartResource(e)],
  ['aspire-vscode.viewResourceLogs', (p, e) => p.viewResourceLogs(e)],
  ['aspire-vscode.openResourceTerminal', (p, e) => p.openResourceTerminal(e)],
  ['aspire-vscode.executeResourceCommand', (p, e) => p.executeResourceCommand(e)],
  ['aspire-vscode.executeResourceCommandItem', (p, e) => p.executeResourceCommandItem(e)],
  ['aspire-vscode.copyEndpointUrl', (p, e) => p.copyEndpointUrl(e)],
  ['aspire-vscode.openInExternalBrowser', (p, e) => p.openInExternalBrowser(e)],
  ['aspire-vscode.openInIntegratedBrowser', (p, e) => p.openInIntegratedBrowser(e)],
  ['aspire-vscode.copyResourceName', (p, e) => p.copyResourceName(e)],
  ['aspire-vscode.copyAppHostPath', (p, e) => p.copyAppHostPath(e)],
  ['aspire-vscode.viewAppHostSource', (p, e) => p.viewAppHostSource(e)],
  ['aspire-vscode.viewAppHostLogFile', (p, e) => p.viewAppHostLogFile(e)],
  ['aspire-vscode.copyLogFilePath', (p, e) => p.copyLogFilePath(e)],
  ['aspire-vscode.expandAll', (p, e) => p.expandAll(e)],
];

export function registerTreeViewCommands(
  appHostTreeProvider: AspireAppHostTreeProvider,
  dataRepository: AppHostDataRepository,
): vscode.Disposable[] {
  const refreshAppHosts = () => {
    appHostTreeProvider.refreshActionSupport();
    dataRepository.refresh();
  };

  return [
    registerInstrumentedCommand('aspire-vscode.globalRefreshAppHosts', 'tree', refreshAppHosts),
    registerInstrumentedCommand('aspire-vscode.refreshAppHosts', 'tree', refreshAppHosts),
    vscode.commands.registerCommand('aspire-vscode.refreshAppHostRuntimeState', () => dataRepository.refreshRuntimeState()),
    registerInstrumentedCommand('aspire-vscode.switchToGlobalView', 'tree', () => dataRepository.setViewMode('global')),
    registerInstrumentedCommand('aspire-vscode.switchToWorkspaceView', 'tree', () => dataRepository.setViewMode('workspace')),
    registerInstrumentedCommand('aspire-vscode.runAppHost', 'tree', (element) => appHostTreeProvider.runAppHost(element, true)),
    registerInstrumentedCommand('aspire-vscode.debugAppHost', 'tree', (element) => appHostTreeProvider.runAppHost(element, false)),
    ...treeElementCommands.map(([commandId, invoke]) => registerInstrumentedCommand(commandId, 'tree', (element) => invoke(appHostTreeProvider, element))),
  ];
}
