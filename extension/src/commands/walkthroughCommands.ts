import * as vscode from 'vscode';
import { aspireTerminalName } from '../loc/strings';

function getOrCreateTerminal(): vscode.Terminal {
    const existing = vscode.window.terminals.find(t => t.name === aspireTerminalName);
    if (existing) {
        return existing;
    }
    return vscode.window.createTerminal({ name: aspireTerminalName });
}

function runInTerminal(command: string): void {
    const terminal = getOrCreateTerminal();
    terminal.show();
    terminal.sendText(command);
}

export async function installCliStableCommand(): Promise<void> {
    if (process.platform === 'win32') {
        runInTerminal('irm https://aspire.dev/install.ps1 | iex');
    } else {
        runInTerminal('curl -sSL https://aspire.dev/install.sh | bash');
    }
}

export async function installCliDailyCommand(): Promise<void> {
    if (process.platform === 'win32') {
        runInTerminal('iex "& { $(irm https://aspire.dev/install.ps1) } -Quality dev"');
    } else {
        runInTerminal('curl -sSL https://aspire.dev/install.sh | bash -s -- -q dev');
    }
}

export async function verifyCliInstalledCommand(): Promise<void> {
    runInTerminal('aspire --version');
}
