import { AspireTerminalProvider } from "../utils/AspireTerminalProvider";
import { CliPathResolutionTarget } from '../utils/cliPathVariables';

export async function newCommand(terminalProvider: AspireTerminalProvider, target: CliPathResolutionTarget, cliPath: string) {
    await terminalProvider.sendAspireCommandToAspireTerminal('new', true, undefined, { target, cliPath });
};
