import * as vscode from 'vscode';
import { spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';
import {
    resourceCommandDynamicInputsFailed,
    resourceCommandLoadingDynamicInputs,
} from '../loc/strings';
import { ResourceCommandArgumentInputJson } from './AppHostDataRepository';
import {
    buildResourceCommandCliArgs,
    ResourceCommandArgumentLoader,
    ResourceCommandArgumentValue,
} from './ResourceCommandArguments';

export interface ResourceCommandArgumentLoaderContext {
    terminalProvider: AspireTerminalProvider;
    resourceName: string;
    commandName: string;
    appHostPath?: string;
}

// Builds a loader that shells out to `aspire resource <name> <command> --load-arguments` so that
// callers (tree view, code lens, etc.) share a single implementation of dynamic argument loading.
// Returns undefined when the CLI invocation fails so collectResourceCommandArguments can abort
// the prompt flow.
export function createResourceCommandArgumentLoader(context: ResourceCommandArgumentLoaderContext): ResourceCommandArgumentLoader {
    return values => loadResourceCommandArgumentInputs(context, values);
}

async function loadResourceCommandArgumentInputs(
    context: ResourceCommandArgumentLoaderContext,
    values: readonly ResourceCommandArgumentValue[]): Promise<ResourceCommandArgumentInputJson[] | undefined> {
    // Refuse to invoke `aspire resource ... --load-arguments` without an explicit --apphost.
    // Without it the CLI auto-discovers some AppHost, which can return dynamic inputs for a
    // different process than the one the user clicked on when multiple AppHosts are running.
    if (!context.appHostPath) {
        extensionLogOutputChannel.warn(`Failed to load resource command arguments for '${context.resourceName}' (${context.commandName}): no AppHost path could be resolved.`);
        await vscode.window.showWarningMessage(resourceCommandDynamicInputsFailed, { modal: true });
        return undefined;
    }

    return await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: resourceCommandLoadingDynamicInputs },
        async () => {
            try {
                const cliPath = await context.terminalProvider.getAspireCliExecutablePath();
                const args = ['resource', context.resourceName, context.commandName, '--load-arguments', '--apphost', context.appHostPath!];
                args.push(...buildResourceCommandCliArgs(values));

                const loadedInputs = await new Promise<ResourceCommandArgumentInputJson[] | undefined>((resolve) => {
                    let settled = false;
                    let parsedInputs: ResourceCommandArgumentInputJson[] | undefined;
                    let stderr = '';
                    const finish = (value: ResourceCommandArgumentInputJson[] | undefined) => {
                        if (!settled) {
                            settled = true;
                            resolve(value);
                        }
                    };

                    const child = spawnCliProcess(context.terminalProvider, cliPath, args, {
                        noExtensionVariables: true,
                        lineCallback: line => {
                            if (settled) {
                                return;
                            }

                            // `aspire resource ... --load-arguments` writes one JSON array line with
                            // ResourceCommandArgumentInputJson[] metadata, for example:
                            //   [{"name":"item","inputType":"Choice","options":{"banana":"Banana"}}]
                            // Other CLI text such as update notifications can appear on stdout too, so
                            // ignore non-JSON lines and only accept the parsed payload after exit code 0.
                            try {
                                const parsed = JSON.parse(line);
                                if (Array.isArray(parsed)) {
                                    parsedInputs = parsed as ResourceCommandArgumentInputJson[];
                                }
                            } catch {
                                // Other CLI output can appear before the machine-readable payload.
                            }
                        },
                        stderrCallback: data => {
                            stderr += data;
                        },
                        errorCallback: error => {
                            extensionLogOutputChannel.warn(`Failed to load resource command arguments: ${error.message}`);
                            finish(undefined);
                        },
                        exitCallback: code => {
                            if (code !== 0) {
                                extensionLogOutputChannel.warn(`aspire resource --load-arguments exited with code ${code}. ${stderr.trim()}`);
                            }
                            finish(code === 0 ? parsedInputs : undefined);
                        },
                    });

                    child.stdin.end();
                });

                if (!loadedInputs) {
                    await vscode.window.showWarningMessage(resourceCommandDynamicInputsFailed, { modal: true });
                }

                return loadedInputs;
            } catch (error) {
                extensionLogOutputChannel.warn(`Failed to load resource command arguments: ${error}`);
                await vscode.window.showWarningMessage(resourceCommandDynamicInputsFailed, { modal: true });
                return undefined;
            }
        });
}
