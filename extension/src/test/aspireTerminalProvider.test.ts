import * as assert from 'assert';
import * as vscode from 'vscode';
import * as sinon from 'sinon';
import { AspireTerminalProvider, quoteShellArg } from '../utils/AspireTerminalProvider';
import * as cliPathModule from '../utils/cliPath';
import { EnvironmentVariables } from '../utils/environment';
import { extensionLogOutputChannel } from '../utils/logging';

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
        let originalStopOnEntry: string | undefined;
        let isCliDebugLoggingEnabledStub: sinon.SinonStub;

        setup(() => {
            originalStopOnEntry = process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY];
            delete process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY];
            isCliDebugLoggingEnabledStub = sinon.stub(terminalProvider, 'isCliDebugLoggingEnabled').returns(false);
        });

        teardown(() => {
            isCliDebugLoggingEnabledStub.restore();

            if (originalStopOnEntry === undefined) {
                delete process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY];
            }
            else {
                process.env[EnvironmentVariables.ASPIRE_CLI_STOP_ON_ENTRY] = originalStopOnEntry;
            }
        });

        test('uses shell integration to execute command when available', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            const events: string[] = [];
            const sentTexts: string[] = [];
            let executedCommand: string | undefined;
            let shown = false;
            const terminal = {
                shellIntegration: {
                    executeCommand: (commandLine: string) => {
                        events.push('execute');
                        executedCommand = commandLine;
                        return {} as vscode.TerminalShellExecution;
                    }
                },
                sendText: (text: string) => {
                    sentTexts.push(text);
                },
                show: () => {
                    events.push('show');
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
                assert.deepStrictEqual(events, ['show', 'execute']);
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

        test('puts extension-added CLI flags before additional pass-through arguments', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            isCliDebugLoggingEnabledStub.returns(true);
            let executedCommand: string | undefined;
            const terminal = {
                shellIntegration: {
                    executeCommand: (commandLine: string) => {
                        executedCommand = commandLine;
                        return {} as vscode.TerminalShellExecution;
                    }
                },
                sendText: () => { },
                show: () => { }
            } as unknown as vscode.Terminal;
            const getAspireTerminalStub = sinon.stub(terminalProvider, 'getAspireTerminal').returns({
                terminal,
                dispose: () => { }
            });

            try {
                await terminalProvider.sendAspireCommandToAspireTerminal('resource "web" "configure"', true, ['--', '--message', 'hello world']);

                const expected = process.platform === 'win32'
                    ? '& "aspire" resource "web" "configure" "--debug" "--" "--message" "hello world"'
                    : 'aspire resource "web" "configure" \'--debug\' \'--\' \'--message\' \'hello world\'';
                assert.strictEqual(executedCommand, expected);
            }
            finally {
                getAspireTerminalStub.restore();
            }
        });

        test('quotes additional arguments with spaces and shell metacharacters', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            let executedCommand: string | undefined;
            const terminal = {
                shellIntegration: {
                    executeCommand: (commandLine: string) => {
                        executedCommand = commandLine;
                        return {} as vscode.TerminalShellExecution;
                    }
                },
                sendText: () => { },
                show: () => { }
            } as unknown as vscode.Terminal;
            const getAspireTerminalStub = sinon.stub(terminalProvider, 'getAspireTerminal').returns({
                terminal,
                dispose: () => { }
            });

            try {
                await terminalProvider.sendAspireCommandToAspireTerminal('resource "web" "configure"', true, [
                    '--',
                    '--message',
                    'hello world "quoted" $PATH ; & | < >',
                    '--path',
                    "it's fine",
                ]);

                const expected = process.platform === 'win32'
                    ? '& "aspire" resource "web" "configure" "--" "--message" "hello world `"quoted`" `$PATH ; & | < >" "--path" "it\'s fine"'
                    : 'aspire resource "web" "configure" \'--\' \'--message\' \'hello world "quoted" $PATH ; & | < >\' \'--path\' \'it\'"\'"\'s fine\'';
                assert.strictEqual(executedCommand, expected);
            }
            finally {
                getAspireTerminalStub.restore();
            }
        });

        test('redacts additional arguments from logs when requested', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            let executedCommand: string | undefined;
            const infoStub = sinon.stub(extensionLogOutputChannel, 'info');
            const terminal = {
                shellIntegration: {
                    executeCommand: (commandLine: string) => {
                        executedCommand = commandLine;
                        return {} as vscode.TerminalShellExecution;
                    }
                },
                sendText: () => { },
                show: () => { }
            } as unknown as vscode.Terminal;
            const getAspireTerminalStub = sinon.stub(terminalProvider, 'getAspireTerminal').returns({
                terminal,
                dispose: () => { }
            });

            try {
                await terminalProvider.sendAspireCommandToAspireTerminal('resource "web" "configure"', true, [
                    '--',
                    '--secret',
                    'super-secret',
                ], { redactAdditionalArgs: true });

                assert.ok(executedCommand?.includes('super-secret'));
                assert.strictEqual(infoStub.calledOnce, true);
                const logMessage = infoStub.firstCall.args[0];
                assert.ok(logMessage.includes('[redacted command arguments]'));
                assert.ok(!logMessage.includes('super-secret'));
            }
            finally {
                getAspireTerminalStub.restore();
                infoStub.restore();
            }
        });
    });

    // The Windows quoting form targets PowerShell (powershell.exe / pwsh.exe),
    // which is VS Code's default integrated terminal on Windows. The Unix
    // form uses POSIX single-quote quoting, which is interpreted identically
    // by bash, zsh, dash, sh, and fish. These tests run on every host OS so
    // we get coverage of both branches regardless of where the test executes.
    suite('quoteShellArg (cross-platform)', () => {
        suite('win32 (PowerShell / pwsh)', () => {
            const cases: { name: string; input: string; expected: string }[] = [
                { name: 'plain value', input: 'hello', expected: '"hello"' },
                { name: 'value with spaces', input: 'hello world', expected: '"hello world"' },
                { name: 'embedded double quote', input: 'say "hi"', expected: '"say `"hi`""' },
                { name: 'embedded single quote', input: "it's fine", expected: `"it's fine"` },
                { name: 'variable expansion $env', input: '$env:USERPROFILE', expected: '"`$env:USERPROFILE"' },
                { name: 'variable expansion $var', input: '$PATH', expected: '"`$PATH"' },
                { name: 'subshell expansion $(...)', input: '$(whoami)', expected: '"`$(whoami)"' },
                { name: 'backtick subshell', input: '`whoami`', expected: '"``whoami``"' },
                { name: 'backslash sequences', input: 'C:\\Program Files\\Aspire', expected: '"C:\\Program Files\\Aspire"' },
                { name: 'backslash followed by quote', input: 'a\\"b', expected: '"a\\`"b"' },
                { name: 'pipe and chaining', input: 'a | b; c && d', expected: '"a | b; c && d"' },
                { name: 'redirection', input: '> out.txt < in.txt', expected: '"> out.txt < in.txt"' },
                { name: 'newline', input: 'line1\nline2', expected: '"line1\nline2"' },
                { name: 'mixed dollar quote backtick', input: '`$x"y"`', expected: '"```$x`"y`"``"' },
                { name: 'attempted PowerShell break-out', input: '"; Remove-Item C:\\ -Recurse #', expected: '"`"; Remove-Item C:\\ -Recurse #"' },
                { name: 'subshell with backticks and dollar', input: '`echo $(rm -rf /)`', expected: '"``echo `$(rm -rf /)``"' },
                { name: 'empty string', input: '', expected: '""' },
                { name: 'only special characters', input: '`$"', expected: '"```$`""' },
            ];

            for (const { name, input, expected } of cases) {
                test(name, () => {
                    assert.strictEqual(quoteShellArg(input, 'win32'), expected);
                });
            }
        });

        suite('posix (bash / zsh / sh / fish)', () => {
            const cases: { name: string; input: string; expected: string }[] = [
                { name: 'plain value', input: 'hello', expected: `'hello'` },
                { name: 'value with spaces', input: 'hello world', expected: `'hello world'` },
                { name: 'embedded double quote', input: 'say "hi"', expected: `'say "hi"'` },
                { name: 'embedded single quote', input: "it's fine", expected: `'it'"'"'s fine'` },
                { name: 'multiple single quotes', input: "''", expected: `''"'"''"'"''` },
                { name: 'variable expansion', input: '$HOME', expected: `'$HOME'` },
                { name: 'subshell expansion $(...)', input: '$(whoami)', expected: `'$(whoami)'` },
                { name: 'backtick subshell', input: '`whoami`', expected: `'\`whoami\`'` },
                { name: 'glob characters', input: '* ? [a-z]', expected: `'* ? [a-z]'` },
                { name: 'pipe and chaining', input: 'a | b; c && d', expected: `'a | b; c && d'` },
                { name: 'redirection', input: '> out.txt < in.txt', expected: `'> out.txt < in.txt'` },
                { name: 'newline', input: 'line1\nline2', expected: `'line1\nline2'` },
                { name: 'attempted bash break-out', input: `'; rm -rf / #`, expected: `''"'"'; rm -rf / #'` },
                { name: 'backslash', input: 'a\\b', expected: `'a\\b'` },
                { name: 'empty string', input: '', expected: `''` },
            ];

            for (const { name, input, expected } of cases) {
                test(name, () => {
                    assert.strictEqual(quoteShellArg(input, 'linux'), expected);
                });
            }

            test('darwin uses identical posix quoting', () => {
                assert.strictEqual(quoteShellArg(`it's fine`, 'darwin'), `'it'"'"'s fine'`);
            });
        });
    });
});
