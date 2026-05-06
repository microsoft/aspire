import * as assert from 'assert';
import * as vscode from 'vscode';
import * as sinon from 'sinon';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliPathModule from '../utils/cliPath';

suite('AspireTerminalProvider tests', () => {
    let terminalProvider: AspireTerminalProvider;
    let resolveCliPathStub: sinon.SinonStub;
    let subscriptions: vscode.Disposable[];

    setup(() => {
        subscriptions = [];
        terminalProvider = new AspireTerminalProvider(subscriptions);
        resolveCliPathStub = sinon.stub(cliPathModule, 'resolveCliPath');
    });

    teardown(() => {
        resolveCliPathStub.restore();
        subscriptions.forEach(s => s.dispose());
    });

    suite('getAspireCliExecutablePath', () => {
        test('returns "aspire" when CLI is on PATH', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });

            const result = await terminalProvider.getAspireCliExecutablePath();
            assert.strictEqual(result, 'aspire');
        });

        test('returns resolved path when CLI found at default install location', async () => {
            resolveCliPathStub.resolves({ cliPath: '/home/user/.aspire/bin/aspire', available: true, source: 'default-install' });

            const result = await terminalProvider.getAspireCliExecutablePath();
            assert.strictEqual(result, '/home/user/.aspire/bin/aspire');
        });

        test('returns configured custom path', async () => {
            resolveCliPathStub.resolves({ cliPath: '/usr/local/bin/aspire', available: true, source: 'configured' });

            const result = await terminalProvider.getAspireCliExecutablePath();
            assert.strictEqual(result, '/usr/local/bin/aspire');
        });

        test('returns "aspire" when CLI is not found', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: false, source: 'not-found' });

            const result = await terminalProvider.getAspireCliExecutablePath();
            assert.strictEqual(result, 'aspire');
        });

        test('handles Windows-style paths', async () => {
            resolveCliPathStub.resolves({ cliPath: 'C:\\Program Files\\Aspire\\aspire.exe', available: true, source: 'configured' });

            const result = await terminalProvider.getAspireCliExecutablePath();
            assert.strictEqual(result, 'C:\\Program Files\\Aspire\\aspire.exe');
        });
    });

    suite('sendAspireCommandToAspireTerminal', () => {
        const expectedCommand = process.platform === 'win32' ? '& "aspire" logs' : 'aspire logs';

        test('uses shell integration to execute command when available', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            const sentTexts: string[] = [];
            let executedCommand: string | undefined;
            let shown = false;
            const terminal = {
                shellIntegration: {
                    executeCommand: (commandLine: string) => {
                        executedCommand = commandLine;
                        return {} as vscode.TerminalShellExecution;
                    }
                },
                sendText: (text: string) => {
                    sentTexts.push(text);
                },
                show: () => {
                    shown = true;
                }
            } as unknown as vscode.Terminal;
            const getAspireTerminalStub = sinon.stub(terminalProvider, 'getAspireTerminal').returns({
                terminal,
                dispose: () => { }
            });

            try {
                await terminalProvider.sendAspireCommandToAspireTerminal('logs');

                assert.strictEqual(executedCommand, expectedCommand);
                assert.deepStrictEqual(sentTexts, []);
                assert.strictEqual(shown, true);
            }
            finally {
                getAspireTerminalStub.restore();
            }
        });

        test('sends Ctrl+C before command when shell integration is unavailable', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            const sentTexts: { text: string; shouldExecute?: boolean }[] = [];
            const terminal = {
                sendText: (text: string, shouldExecute?: boolean) => {
                    sentTexts.push({ text, shouldExecute });
                },
                show: () => { }
            } as unknown as vscode.Terminal;
            const getAspireTerminalStub = sinon.stub(terminalProvider, 'getAspireTerminal').returns({
                terminal,
                dispose: () => { }
            });

            try {
                await terminalProvider.sendAspireCommandToAspireTerminal('logs');

                assert.deepStrictEqual(sentTexts, [
                    { text: '\x03', shouldExecute: false },
                    { text: expectedCommand, shouldExecute: undefined }
                ]);
            }
            finally {
                getAspireTerminalStub.restore();
            }
        });
    });
});
