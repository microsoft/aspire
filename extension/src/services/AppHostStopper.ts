import * as vscode from 'vscode';
import { spawnCliProcess, terminateCliProcess } from '../utils/process/cliProcess';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { getCliPathTargetForUri } from '../utils/cliPathVariables';
import { reportCliResolvedForOperation } from '../utils/cliOperationResolution';

const maxRetainedStderrLength = 16 * 1024;

export async function stopExternalAppHost(
    terminalProvider: AspireTerminalProvider,
    appHostPath: string,
    cancellationToken: vscode.CancellationToken,
): Promise<void> {

    const target = getCliPathTargetForUri(vscode.Uri.file(appHostPath));
    const cliPath = await terminalProvider.getAspireCliExecutablePath(target);
    if (cancellationToken.isCancellationRequested) {
        throw new vscode.CancellationError();
    }
    reportCliResolvedForOperation(target, cliPath);

    await new Promise<void>((resolve, reject) => {
        let settled = false;
        let cancellationRequested = false;
        let stderr = '';
        let cliProcess: ReturnType<typeof spawnCliProcess> | undefined;
        let termination: Promise<void> | undefined;
        let cancellationRegistration: vscode.Disposable | undefined;
        const settle = (callback: () => void) => {
            if (settled) {
                return;
            }

            settled = true;
            cancellationRegistration?.dispose();
            callback();
        };
        const settleCancellation = () => {
            settle(() => reject(new vscode.CancellationError()));
        };
        const terminateForCancellation = () => {
            if (!cliProcess) {
                return;
            }

            termination ??= terminateCliProcess(cliProcess, 'aspire stop');
            void termination.then(
                settleCancellation,
                error => settle(() => reject(error)));
        };

        cancellationRegistration = cancellationToken.onCancellationRequested(() => {
            if (cliProcess && (cliProcess.exitCode !== null || cliProcess.signalCode !== null)) {
                return;
            }

            cancellationRequested = true;
            terminateForCancellation();
        });

        try {
            cliProcess = spawnCliProcess(terminalProvider, cliPath, ['stop', '--apphost', appHostPath], {
                createProcessGroup: true,
                noExtensionVariables: true,
                stderrCallback: data => {
                    if (stderr.length < maxRetainedStderrLength) {
                        stderr += data.slice(0, maxRetainedStderrLength - stderr.length);
                    }
                },
                exitCallback: code => {
                    if (cancellationRequested) {
                        terminateForCancellation();
                        return;
                    }

                    if (code === 0) {
                        settle(resolve);
                        return;
                    }

                    const detail = stderr.trim();
                    settle(() => reject(new Error(
                        detail
                            ? `aspire stop exited with code ${code ?? 1}: ${detail}`
                            : `aspire stop exited with code ${code ?? 1}.`)));
                },
                errorCallback: error => {
                    if (cancellationRequested) {
                        terminateForCancellation();
                    } else {
                        settle(() => reject(error));
                    }
                },
            });
        }
        catch (error) {
            if (cancellationRequested) {
                settleCancellation();
            } else {
                settle(() => reject(error));
            }
            return;
        }
        if (cancellationRequested) {
            terminateForCancellation();
        }
    });
}