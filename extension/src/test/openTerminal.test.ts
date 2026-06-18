import * as assert from 'assert';
import * as vscode from 'vscode';
import { openTerminalCommand } from '../commands/openTerminal';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';

suite('openTerminalCommand tests', () => {
    test('creates terminal using resolved CLI path', async () => {
        let terminalCliPath: string | undefined;
        let shown = false;
        const terminalProvider = {
            getAspireCliExecutablePath: async () => '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire',
            getAspireTerminal: (_forceCreate?: boolean, aspireCliPath?: string) => {
                terminalCliPath = aspireCliPath;

                return {
                    terminal: {
                        show: () => { shown = true; }
                    } as unknown as vscode.Terminal,
                    dispose: () => { }
                };
            }
        } as unknown as AspireTerminalProvider;

        await openTerminalCommand(terminalProvider);

        assert.strictEqual(terminalCliPath, '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire');
        assert.strictEqual(shown, true);
    });
});
