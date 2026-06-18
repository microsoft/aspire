import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';

export async function openTerminalCommand(terminalProvider: AspireTerminalProvider): Promise<void> {
    const cliPath = await terminalProvider.getAspireCliExecutablePath();
    // Ensure the Aspire terminal exists and show it
    const aspireTerminal = terminalProvider.getAspireTerminal(false, cliPath);
    aspireTerminal.terminal.show();
}
