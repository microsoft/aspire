import { AspireTerminalProvider } from "../utils/AspireTerminalProvider";

export async function initCommand(terminalProvider: AspireTerminalProvider) {
    await terminalProvider.sendAspireCommandToAspireTerminal('init');
};