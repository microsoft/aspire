import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { CliPathResolutionTarget } from '../utils/cliPathVariables';

export async function openTerminalCommand(
    terminalProvider: AspireTerminalProvider,
    target: CliPathResolutionTarget,
    cliPath: string,
): Promise<void> {
    // Ensure the Aspire terminal exists and show it
    const aspireTerminal = terminalProvider.getAspireTerminal(false, target, cliPath);
    aspireTerminal.terminal.show();
}
