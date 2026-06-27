import * as vscode from 'vscode';
import { aspireTerminalName, installCliDailyBuild, installCliDailyBuildDescription, installCliPlaceholder, installCliViewAllOptions, installCliViewAllOptionsDescription } from '../loc/strings';

// The Aspire CLI install guide lists the supported package managers and the
// exact commands used here: https://aspire.dev/get-started/install-cli/
const installGuideUrl = 'https://aspire.dev/get-started/install-cli/';

// Daily ("dev") builds are not published to any package manager — they are only
// available through the install script with `--quality dev`. The script is
// shell-specific, so the command differs per platform and is run through an
// explicit shell host (see runInstallScript).
// https://aspire.dev/reference/cli/install-script/
const dailyInstallScriptCommand = process.platform === 'win32'
    ? `Invoke-Expression "& { $(Invoke-RestMethod 'https://aspire.dev/install.ps1') } -Quality 'dev'"`
    : 'curl -sSL https://aspire.dev/install.sh | bash -s -- --quality dev';

interface InstallOption extends vscode.QuickPickItem {
    // Package-manager command. These are plain executables, so they run the same
    // in any shell and are sent to the standard integrated terminal.
    command?: string;
    // Install-script command. The script is shell-specific (PowerShell on
    // Windows), so it is run through an explicit shell host rather than the
    // user's default terminal shell (see runInstallScript / issue #18459).
    scriptCommand?: string;
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
    {
        label: installCliDailyBuild,
        description: installCliDailyBuildDescription,
        scriptCommand: dailyInstallScriptCommand,
        platforms: ['win32', 'darwin', 'linux'],
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

// The install script is shell-specific: on Windows it is PowerShell
// (`irm ... | iex`), which cmd.exe cannot run. VS Code's default terminal
// inherits the user's configured shell — frequently cmd.exe on Windows — which
// is the root cause of issue #18459. Launch the script in an explicit shell
// host so it never depends on whatever shell the terminal happened to inherit.
function runInstallScript(command: string): void {
    if (process.platform === 'win32') {
        // `powershell.exe` (Windows PowerShell 5.1) ships with every supported
        // Windows version and is always on PATH, so it is a safe default host.
        const terminal = vscode.window.createTerminal({
            name: aspireTerminalName,
            shellPath: 'powershell.exe',
        });
        terminal.show();
        terminal.sendText(command);
        return;
    }

    // On macOS/Linux the script is piped explicitly to `bash`, so it runs
    // correctly regardless of the user's interactive shell (bash, zsh, fish).
    runInTerminal(command);
}

export async function installCliCommand(): Promise<void> {
    const items: InstallOption[] = installOptions.filter(option => option.platforms.includes(process.platform));

    // Always offer the full install guide as an escape hatch. It covers any
    // package manager or quality not surfaced above, plus the stable install
    // script.
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

    if (selected.scriptCommand) {
        runInstallScript(selected.scriptCommand);
        return;
    }

    if (selected.docsUrl) {
        await vscode.env.openExternal(vscode.Uri.parse(selected.docsUrl));
    }
}

export async function verifyCliInstalledCommand(): Promise<void> {
    // `aspire --version` is a plain executable invocation, so unlike the install
    // scripts it runs identically in cmd.exe, PowerShell, bash, and zsh. It was
    // never affected by the shell-inheritance bug in issue #18459.
    runInTerminal('aspire --version');
}
