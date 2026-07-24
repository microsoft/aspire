export type TelemetryAspireCommand = 'run' | 'deploy' | 'publish' | 'do' | 'other';

export function bucketAspireCommand(command: string): TelemetryAspireCommand {
    // Commands can originate in launch.json or extension-tree state. Both are user-editable
    // enough that a typo or future command should not become a new telemetry dimension.
    switch (command) {
        case 'run':
        case 'deploy':
        case 'publish':
        case 'do':
            return command;
        default:
            return 'other';
    }
}
