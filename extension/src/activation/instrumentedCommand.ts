import * as vscode from 'vscode';

import { isCommandCancellation, withCommandTelemetry } from '../utils/telemetry';

/**
 * Adapter around vscode.commands.registerCommand that routes the callback
 * through {@link withCommandTelemetry} so every command invocation gets
 * outcome / duration / error_kind telemetry without changing call sites
 * across the file. Use this for command implementations that bypass
 * tryExecuteCommand (e.g., tree-view commands, code lens commands,
 * walkthrough commands) — tryExecuteCommand already wraps its callers.
 *
 * `source` distinguishes invocation sites we can statically classify
 * (`tree`, `codelens`, `walkthrough`); palette is the default and is
 * already used by tryExecuteCommand-wrapped commands.
 */
export function registerInstrumentedCommand(
  commandName: string,
  source: 'tree' | 'codelens' | 'walkthrough' | 'editor',
  // The signature mirrors vscode.commands.registerCommand which accepts
  // `(...args: any[]) => any`. Using `any` here preserves the inline
  // lambda parameter inference at the call sites (otherwise a generic
  // would default to `unknown[]` and force callers to annotate every
  // parameter just to satisfy the wrapper).
  fn: (...args: any[]) => any,
): vscode.Disposable {
  return vscode.commands.registerCommand(commandName, async (...args) => {
    try {
      return await withCommandTelemetry(commandName, () => fn(...args), { source });
    }
    catch (error) {
      if (isCommandCancellation(error)) {
        return undefined;
      }

      throw error;
    }
  });
}
