import * as vscode from 'vscode';
import { AspireResourceExtendedDebugConfiguration, ExecutableLaunchConfiguration, isGoLaunchConfiguration } from "../../dcp/types";
import { goDisplayName, goLabel, invalidLaunchConfiguration } from "../../loc/strings";
import { extensionLogOutputChannel } from "../../utils/logging";
import { ResourceDebuggerExtension } from "../debuggerExtensions";

function getProjectFile(launchConfig: ExecutableLaunchConfiguration): string {
    if (isGoLaunchConfiguration(launchConfig)) {
        return launchConfig.program || launchConfig.working_directory || '';
    }

    throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
}

export const goDebuggerExtension: ResourceDebuggerExtension = {
    resourceType: 'go',
    debugAdapter: 'go',
    extensionId: 'golang.go',
    getDisplayName: (launchConfiguration: ExecutableLaunchConfiguration) => {
        if (isGoLaunchConfiguration(launchConfiguration)) {
            const displayPath = launchConfiguration.program || launchConfiguration.working_directory || '';
            return displayPath ? goDisplayName(vscode.workspace.asRelativePath(displayPath)) : goLabel;
        }

        return goLabel;
    },
    getSupportedFileTypes: () => ['.go'],
    getProjectFile: (launchConfig) => getProjectFile(launchConfig),
    createDebugSessionConfigurationCallback: async (launchConfig, args, _env, launchOptions, debugConfiguration: AspireResourceExtendedDebugConfiguration): Promise<void> => {
        if (!isGoLaunchConfiguration(launchConfig)) {
            extensionLogOutputChannel.info(`The resource type was not go for ${JSON.stringify(launchConfig)}`);
            throw new Error(invalidLaunchConfiguration(JSON.stringify(launchConfig)));
        }

        debugConfiguration.type = 'go';
        debugConfiguration.request = 'launch';
        debugConfiguration.mode = 'debug';
        debugConfiguration.debugAdapter = 'dlv-dap';
        debugConfiguration.noDebug = !launchOptions.debug;

        const program = launchConfig.program || launchConfig.working_directory;
        if (program) {
            debugConfiguration.program = program;
        }

        if (launchConfig.working_directory) {
            debugConfiguration.cwd = launchConfig.working_directory;
        }

        if (launchConfig.build_flags) {
            debugConfiguration.buildFlags = launchConfig.build_flags;
        }

        debugConfiguration.args = args ?? [];
    }
};

// Splits the Go AppHost launch command the CLI sends into the pieces the debug adapter needs.
//
// GoLanguageSupport starts the AppHost with `go run .` from the AppHost directory, and the CLI
// prepends the resolved executable, so the command arrives as:
//
//   ["C:\\Program Files\\Go\\bin\\go.exe", "run", "."]
//   ["/usr/local/go/bin/go", "run", ".", ...appHostArgs]
//
// The Go debug adapter builds and starts the package itself, so the executable and the `run` verb are
// dropped and only the package and the AppHost's own arguments are kept. The executable is checked
// rather than launched: the adapter finds the toolchain through the Go extension's own settings.
//
// Returns null for any other shape, including `go run` flags such as -tags or -race. A launch
// configuration has no place for them on this path, and forwarding one would hand a flag to the
// adapter as the program to debug.
export function parseGoAppHostCommand(args: string[]): { packagePath: string; appHostArgs: string[] } | null {
    if (args.length < 3) {
        return null;
    }

    // Split on both separators so the name is found whichever path flavour the CLI reported.
    const executable = args[0].split(/[\\/]/).pop()?.toLowerCase();
    if (executable !== 'go' && executable !== 'go.exe') {
        return null;
    }

    if (args[1] !== 'run' || args[2].startsWith('-')) {
        return null;
    }

    return {
        packagePath: args[2],
        appHostArgs: args.slice(3)
    };
}
