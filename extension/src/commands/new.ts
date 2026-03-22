import { AspireTerminalProvider } from "../utils/AspireTerminalProvider";

export async function newCommand(terminalProvider: AspireTerminalProvider) {
    await terminalProvider.sendAspireCommandToAspireTerminal('new');
};
