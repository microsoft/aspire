import * as vscode from 'vscode';
import { AspireTerminalProvider } from './AspireTerminalProvider';
import { spawnCliProcess } from '../debugger/languages/cli';
import { extensionLogOutputChannel } from './logging';
import { ConfigInfo } from '../types/configInfo';
import * as strings from '../loc/strings';

/**
 * Gets configuration information from the Aspire CLI.
 */
export async function getConfigInfo(terminalProvider: AspireTerminalProvider): Promise<ConfigInfo | null> {
    const cliPath = await terminalProvider.getAspireCliExecutablePath();

    return new Promise<ConfigInfo | null>((resolve) => {
        const args = ['config', 'info', '--json'];
        let output = '';

        spawnCliProcess(terminalProvider, cliPath, args, {
            stdoutCallback: (data) => {
                output += data;
            },
            stderrCallback: (data) => {
                extensionLogOutputChannel.error(`aspire config info stderr: ${data}`);
            },
            exitCallback: (code) => {
                if (code !== 0) {
                    extensionLogOutputChannel.error(strings.failedToGetConfigInfo(code ?? -1));
                    vscode.window.showErrorMessage(strings.failedToGetConfigInfo(code ?? -1));
                    resolve(null);
                    return;
                }

                try {
                    const configInfo = JSON.parse(output.trim()) as ConfigInfo;
                    extensionLogOutputChannel.info(`Got config info: ${configInfo.AvailableFeatures.length} features available`);
                    resolve(configInfo);
                } catch (error) {
                    extensionLogOutputChannel.error(strings.failedToParseConfigInfo(error));
                    vscode.window.showErrorMessage(strings.failedToParseConfigInfo(error));
                    resolve(null);
                }
            },
            errorCallback: (error) => {
                extensionLogOutputChannel.error(strings.errorGettingConfigInfo(error));
                vscode.window.showErrorMessage(strings.errorGettingConfigInfo(error));
                resolve(null);
            },
            noExtensionVariables: true
        });
    });
}
