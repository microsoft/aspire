import * as vscode from 'vscode';

import type { AspireExtensionContext } from '../AspireExtensionContext';
import type { AppHostLaunchService } from '../services/AppHostLaunchService';
import type { AspireExtensionStateSnapshot } from '../types/extensionApi';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import type { AspireAppHostTreeProvider } from '../views/AspireAppHostTreeProvider';
import type { AppHostDataRepository } from '../data/AppHostDataRepository';

/**
 * Build-time replacement for `e2eStateFileBridge.ts` in production bundles.
 *
 * The real bridge is a test control channel: it registers a wildcard debug adapter tracker, mirrors
 * extension state to a file, and executes commands read from a path supplied in an environment
 * variable. None of that belongs in a published extension, and a runtime flag is the wrong place to
 * enforce that - the code would still ship and would still be reachable by anyone who can set an
 * environment variable on the VS Code process.
 *
 * `webpack.config.js` swaps this module in for the shipping production VSIX. Local development
 * bundles built with `webpack --mode none` keep the real implementation, and the production-mode
 * E2E VSIX opts back into the bridge with `ASPIRE_EXTENSION_E2E_INCLUDE_BRIDGE=true` so tests can
 * exercise it without shipping it to users.
 *
 * The exported surface must stay in sync with the parts of `e2eStateFileBridge.ts` that
 * `extension.ts` imports, otherwise the production build breaks at bundle time rather than at
 * runtime - which is the intended failure mode.
 */
export function createE2eStateFileBridge(
  _context: vscode.ExtensionContext,
  _aspireContext: AspireExtensionContext,
  _dataRepository: AppHostDataRepository,
  _appHostLaunchService: AppHostLaunchService,
  _appHostTreeProvider: AspireAppHostTreeProvider,
  _terminalProvider: AspireTerminalProvider,
  _onDidChangeState: vscode.Event<AspireExtensionStateSnapshot>,
  _languageModelTools: ReadonlyMap<string, {
    readonly tool: vscode.LanguageModelTool<unknown>;
    readonly registered: boolean;
  }>,
): vscode.Disposable {
  return new vscode.Disposable(() => undefined);
}

export function isE2eBridgeEnabled(): boolean {
  return false;
}
