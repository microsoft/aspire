export function filterResourceCommandStatusOutput(output: string, resourceName: string, commandName: string): string {
    if (!output) {
        return '';
    }

    const filteredLines = output
        .split(/\r?\n/)
        .filter(line => !isResourceCommandStatusLine(line, resourceName, commandName));

    while (filteredLines.length > 0 && filteredLines[0].trim().length === 0) {
        filteredLines.shift();
    }

    while (filteredLines.length > 0 && filteredLines[filteredLines.length - 1].trim().length === 0) {
        filteredLines.pop();
    }

    return filteredLines.join('\n');
}

export function normalizeResourceCommandStatusLine(line: string): string {
    return line
        .replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '')
        .trim()
        .replace(/^[✅✔✓]\s*/, '');
}

function isResourceCommandStatusLine(line: string, resourceName: string, commandName: string): boolean {
    const normalized = normalizeResourceCommandStatusLine(line);

    return getResourceCommandStatusLines(resourceName, commandName).includes(normalized);
}

function getResourceCommandStatusLines(resourceName: string, commandName: string): string[] {
    // Older CLIs emitted resource command status to stdout before the command value, for example:
    //   Restarting resource 'cache'...
    //   Resource 'cache' restarted successfully.
    //   Executing command 'echo-arguments' on resource 'cache'...
    //   Command 'echo-arguments' executed successfully on resource 'cache'.
    // Keep this compatibility filter narrow so real command output is preserved.
    const lines = [
        `Validating and executing command '${commandName}' on resource '${resourceName}'...`,
        `Executing command '${commandName}' on resource '${resourceName}'...`,
        `Command '${commandName}' executed successfully on resource '${resourceName}'.`,
    ];

    const knownCommand = getKnownResourceCommandStatus(commandName);
    if (knownCommand) {
        lines.push(
            `${knownCommand.progressVerb} resource '${resourceName}'...`,
            `Resource '${resourceName}' ${knownCommand.pastTenseVerb} successfully.`);
    }

    return lines;
}

function getKnownResourceCommandStatus(commandName: string): { progressVerb: string; pastTenseVerb: string } | undefined {
    switch (commandName) {
        case 'start':
            return { progressVerb: 'Starting', pastTenseVerb: 'started' };
        case 'stop':
            return { progressVerb: 'Stopping', pastTenseVerb: 'stopped' };
        case 'restart':
            return { progressVerb: 'Restarting', pastTenseVerb: 'restarted' };
        case 'rebuild':
            return { progressVerb: 'Rebuilding', pastTenseVerb: 'rebuilt' };
        case 'set-parameter':
        case 'parameter-set':
            return { progressVerb: 'Setting parameter for', pastTenseVerb: 'set' };
        case 'delete-parameter':
        case 'parameter-delete':
            return { progressVerb: 'Deleting parameter for', pastTenseVerb: 'deleted' };
        default:
            return undefined;
    }
}
