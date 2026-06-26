import * as vscode from 'vscode';
import { aspireTerminalName, installCliPlaceholder, installCliViewAllOptions, installCliViewAllOptionsDescription } from '../loc/strings';

// The Aspire CLI install guide lists the supported package managers and the
// exact commands used here: https://aspire.dev/get-started/install-cli/
const installGuideUrl = 'https://aspire.dev/get-started/install-cli/';

interface InstallOption extends vscode.QuickPickItem {
    // Shell-agnostic command run in the integrated terminal. Package-manager
    // commands (winget/brew/npm/dotnet/mise) are plain executables, so they
    // behave the same in cmd.exe, PowerShell, bash, and zsh. This is the key
    // reason we offer package managers instead of the install scripts: the
    // `irm ... | iex` / `curl ... | bash` scripts are shell-specific and
    // failed on Windows when the terminal inherited cmd.exe (issue #18459).
    command?: string;
    // When set, open this URL in the browser instead of running a command.
    // Used by the escape-hatch item so the shell-specific install script is
    // reached through the docs rather than piped into an unknown shell.
    docsUrl?: string;
    // process.platform values the option is offered on.
    platforms: NodeJS.Platform[];
}

const installOptions: InstallOption[] = [
    {
        label: 'WinGet',
        description: 'winget install Microsoft.Aspire',
        command: 'winget install Microsoft.Aspire',
        platforms: ['win32'],
    },
    {
        label: 'Homebrew',
        description: 'brew install --cask microsoft/aspire/aspire',
        command: 'brew install --cask microsoft/aspire/aspire',
        platforms: ['darwin'],
    },
    {
        label: 'npm',
        description: 'npm install -g @microsoft/aspire-cli',
        command: 'npm install -g @microsoft/aspire-cli',
        platforms: ['win32', 'darwin', 'linux'],
    },
    {
        label: '.NET tool',
        description: 'dotnet tool install -g Aspire.Cli',
        command: 'dotnet tool install -g Aspire.Cli',
        platforms: ['win32', 'darwin', 'linux'],
    },
    {
        label: 'mise',
        description: 'mise use -g aspire',
        command: 'mise use -g aspire',
        platforms: ['darwin', 'linux'],
    },
];

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

export async function installCliCommand(): Promise<void> {
    const items: InstallOption[] = installOptions.filter(option => option.platforms.includes(process.platform));

    // Always offer the full install guide as an escape hatch. It covers the
    // install script (the only shell-specific route, deliberately kept out of
    // the terminal-run options) and any package manager not surfaced above.
    items.push({
        label: installCliViewAllOptions,
        description: installCliViewAllOptionsDescription,
        docsUrl: installGuideUrl,
        platforms: ['win32', 'darwin', 'linux'],
    });

    const selected = await vscode.window.showQuickPick(items, {
        placeHolder: installCliPlaceholder,
    });

    if (!selected) {
        return;
    }

    if (selected.command) {
        runInTerminal(selected.command);
        return;
    }

    if (selected.docsUrl) {
        await vscode.env.openExternal(vscode.Uri.parse(selected.docsUrl));
    }
}
