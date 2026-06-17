import * as assert from 'assert';
import * as vscode from 'vscode';
import * as sinon from 'sinon';
import { AspireTerminalProvider, quoteShellArg, shellArg } from '../utils/AspireTerminalProvider';
import * as cliPathModule from '../utils/cliPath';
import { EnvironmentVariables } from '../utils/environment';
import { extensionLogOutputChannel } from '../utils/logging';
import { terminalCommandArgumentControlCharacters, terminalCommandUnsafeLiteral } from '../loc/strings';

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

    function restoreEnvironmentVariable(name: string, value: string | undefined): void {
        if (value === undefined) {
            delete process.env[name];
            return;
        }

        process.env[name] = value;
    }

    suite('getAspireTerminal', () => {
        test('creates Windows terminal with PowerShell 7 when it is available', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            let probeCount = 0;
            terminalProvider = new AspireTerminalProvider(subscriptions, () => {
                probeCount++;
                return true;
            });
            const createEnvironmentStub = sinon.stub(terminalProvider, 'createEnvironment').returns({});
            const terminal = {
                dispose: () => { }
            } as unknown as vscode.Terminal;
            const createTerminalStub = sinon.stub(vscode.window, 'createTerminal').returns(terminal);

            try {
                const result = terminalProvider.getAspireTerminal(true);

                assert.strictEqual(result.terminal, terminal);
                assert.strictEqual(createTerminalStub.calledOnce, true);
                assert.strictEqual((createTerminalStub.firstCall.args[0] as vscode.TerminalOptions).shellPath, 'pwsh.exe');
                assert.strictEqual(probeCount, 1);
            }
            finally {
                terminalProvider.dispose();
                createTerminalStub.restore();
                createEnvironmentStub.restore();
                platformStub.restore();
            }
        });

        test('falls back to Windows PowerShell when PowerShell 7 is unavailable', () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            let probeCount = 0;
            terminalProvider = new AspireTerminalProvider(subscriptions, () => {
                probeCount++;
                return false;
            });
            const createEnvironmentStub = sinon.stub(terminalProvider, 'createEnvironment').returns({});
            const terminal = {
                dispose: () => { }
            } as unknown as vscode.Terminal;
            const createTerminalStub = sinon.stub(vscode.window, 'createTerminal').returns(terminal);

            try {
                terminalProvider.getAspireTerminal(true);
                assert.strictEqual(createTerminalStub.calledOnce, true);
                assert.strictEqual((createTerminalStub.firstCall.args[0] as vscode.TerminalOptions).shellPath, 'powershell.exe');
                assert.strictEqual(probeCount, 1);
            }
            finally {
                terminalProvider.dispose();
                createTerminalStub.restore();
                createEnvironmentStub.restore();
                platformStub.restore();
            }
        });

        test('uses default terminal profile on non-Windows hosts', () => {
            const platformStub = sinon.stub(process, 'platform').value('darwin');
            const createEnvironmentStub = sinon.stub(terminalProvider, 'createEnvironment').returns({});
            const terminal = {
                dispose: () => { }
            } as unknown as vscode.Terminal;
            const createTerminalStub = sinon.stub(vscode.window, 'createTerminal').returns(terminal);

            try {
                terminalProvider.getAspireTerminal(true);

                assert.strictEqual(createTerminalStub.calledOnce, true);
                assert.strictEqual((createTerminalStub.firstCall.args[0] as vscode.TerminalOptions).shellPath, undefined);
            }
            finally {
                terminalProvider.dispose();
                createTerminalStub.restore();
                createEnvironmentStub.restore();
                platformStub.restore();
            }
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
            const terminalEvents: unknown[] = [];
            let executedCommand: string | undefined;
            let shown = false;
            const eventSubscription = terminalProvider.onDidSendAspireCommand(event => terminalEvents.push(event));
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
                assert.deepStrictEqual(terminalEvents.map(event => (event as { executionMode: string }).executionMode), ['shellIntegration']);
            }
            finally {
                eventSubscription.dispose();
                getAspireTerminalStub.restore();
            }
        });

        test('quotes Windows CLI path with PowerShell escaping', async () => {
            const platformStub = sinon.stub(process, 'platform').value('win32');
            const cliPath = 'C:\\Tools\\$(bad)\\`bin\\aspire.exe';
            resolveCliPathStub.resolves({ cliPath, available: true, source: 'configured' });
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
                await terminalProvider.sendAspireCommandToAspireTerminal('logs');

                assert.strictEqual(executedCommand, `& ${quoteShellArg(cliPath, 'win32')} logs`);
            }
            finally {
                platformStub.restore();
                getAspireTerminalStub.restore();
            }
        });

        test('quotes structured subcommand arguments with shell metacharacters', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            const appHostPath = `/workspace/'; touch /tmp/aspire-rce #/$(whoami)/"bad"/AppHost.csproj`;
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
                await terminalProvider.sendAspireCommandToAspireTerminal(['stop', '--apphost', shellArg(appHostPath)]);

                const expectedSubcommand = `stop --apphost ${quoteShellArg(appHostPath)}`;
                const expectedCommand = process.platform === 'win32'
                    ? `& "aspire" ${expectedSubcommand}`
                    : `aspire ${expectedSubcommand}`;
                assert.strictEqual(executedCommand, expectedCommand);
                assert.strictEqual(executedCommand.includes(`--apphost ${appHostPath}`), false);
            }
            finally {
                getAspireTerminalStub.restore();
            }
        });

        test('rejects terminal control characters before falling back to sendText', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            const getAspireTerminalStub = sinon.stub(terminalProvider, 'getAspireTerminal').throws(new Error('Terminal should not be created'));

            try {
                await assert.rejects(
                    () => terminalProvider.sendAspireCommandToAspireTerminal(['stop', '--apphost', shellArg('/workspace/AppHost.csproj\x03\necho pwned')]),
                    { message: terminalCommandArgumentControlCharacters });
                await assert.rejects(
                    () => terminalProvider.sendAspireCommandToAspireTerminal('resource "web" "restart"', true, ['--', 'safe\r\necho pwned']),
                    { message: terminalCommandArgumentControlCharacters });
                assert.strictEqual(getAspireTerminalStub.called, false);
            }
            finally {
                getAspireTerminalStub.restore();
            }
        });

        test('allows tabs inside quoted structured arguments', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            const appHostPath = '/workspace/apps/Store\tTabbed/AppHost.csproj';
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
                await terminalProvider.sendAspireCommandToAspireTerminal(['stop', '--apphost', shellArg(appHostPath)]);

                const expectedSubcommand = `stop --apphost ${quoteShellArg(appHostPath)}`;
                const expectedCommand = process.platform === 'win32'
                    ? `& "aspire" ${expectedSubcommand}`
                    : `aspire ${expectedSubcommand}`;
                assert.strictEqual(executedCommand, expectedCommand);
            }
            finally {
                getAspireTerminalStub.restore();
            }
        });

        test('rejects unsafe bare structured subcommand literals before creating a terminal', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            const getAspireTerminalStub = sinon.stub(terminalProvider, 'getAspireTerminal').throws(new Error('Terminal should not be created'));
            const unsafeLiterals = ['resource; touch /tmp/pwned', '$(whoami)', '`whoami`', '--apphost=/tmp/AppHost.csproj', 'logs --follow'];

            try {
                for (const unsafeLiteral of unsafeLiterals) {
                    await assert.rejects(
                        () => terminalProvider.sendAspireCommandToAspireTerminal(['resource', unsafeLiteral, shellArg('web')]),
                        { message: terminalCommandUnsafeLiteral });
                }

                assert.strictEqual(getAspireTerminalStub.called, false);
            }
            finally {
                getAspireTerminalStub.restore();
            }
        });

        test('sends Ctrl+C before command when shell integration is unavailable', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            const sentTexts: { text: string; shouldExecute?: boolean }[] = [];
            const terminalEvents: unknown[] = [];
            const eventSubscription = terminalProvider.onDidSendAspireCommand(event => terminalEvents.push(event));
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
                assert.deepStrictEqual(terminalEvents.map(event => (event as { executionMode: string }).executionMode), ['sendText']);
            }
            finally {
                eventSubscription.dispose();
                getAspireTerminalStub.restore();
            }
        });

        test('records suppressed execution mode without creating a terminal when E2E suppression is enabled', async () => {
            resolveCliPathStub.resolves({ cliPath: 'aspire', available: true, source: 'path' });
            const originalEnableBridge = process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE;
            const originalStateFile = process.env.ASPIRE_EXTENSION_E2E_STATE_FILE;
            const originalControlFile = process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE;
            const originalSuppressTerminalCommandExecution = process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_TERMINAL_COMMAND_EXECUTION;
            const terminalEvents: unknown[] = [];
            const eventSubscription = terminalProvider.onDidSendAspireCommand(event => terminalEvents.push(event));
            let executedCommand: string | undefined;
            const sentTexts: string[] = [];
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
                show: () => { }
            } as unknown as vscode.Terminal;
            const getAspireTerminalStub = sinon.stub(terminalProvider, 'getAspireTerminal').returns({
                terminal,
                dispose: () => { }
            });

            try {
                process.env.ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE = 'true';
                process.env.ASPIRE_EXTENSION_E2E_STATE_FILE = '/tmp/aspire-extension-state.json';
                process.env.ASPIRE_EXTENSION_E2E_CONTROL_FILE = '/tmp/aspire-extension-control.json';
                process.env.ASPIRE_EXTENSION_E2E_SUPPRESS_TERMINAL_COMMAND_EXECUTION = 'true';

                await terminalProvider.sendAspireCommandToAspireTerminal('logs');

                assert.strictEqual(executedCommand, undefined);
                assert.deepStrictEqual(sentTexts, []);
                assert.strictEqual(getAspireTerminalStub.called, false);
                assert.deepStrictEqual(terminalEvents.map(event => (event as { executionMode: string }).executionMode), ['suppressed']);
                assert.deepStrictEqual(terminalEvents.map(event => (event as { executionSuppressed: boolean }).executionSuppressed), [true]);
            }
            finally {
                restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_ENABLE_BRIDGE', originalEnableBridge);
                restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_STATE_FILE', originalStateFile);
                restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_CONTROL_FILE', originalControlFile);
                restoreEnvironmentVariable('ASPIRE_EXTENSION_E2E_SUPPRESS_TERMINAL_COMMAND_EXECUTION', originalSuppressTerminalCommandExecution);
                eventSubscription.dispose();
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

    suite('createEnvironment', () => {
        setup(() => {
            terminalProvider.rpcServerConnectionInfo = {
                address: 'http://localhost:1234',
                token: 'rpc-token',
                cert: 'rpc-cert',
            };
            terminalProvider.dcpServerConnectionInfo = {
                address: 'http://localhost:5678',
                token: 'dcp-token',
                certificate: 'dcp-cert',
            };
        });

        test('marks extension-managed debug sessions as non-interactive without disabling extension prompts', () => {
            const env = terminalProvider.createEnvironment('debug-session-id', false);

            assert.strictEqual(env.ASPIRE_EXTENSION_DEBUG_SESSION_ID, 'debug-session-id');
            assert.strictEqual(env.ASPIRE_EXTENSION_PROMPT_ENABLED, 'true');
            assert.strictEqual(env.ASPIRE_NON_INTERACTIVE, 'true');
        });

        test('does not mark user terminal commands as non-interactive', () => {
            const env = terminalProvider.createEnvironment();

            assert.strictEqual(env.ASPIRE_EXTENSION_DEBUG_SESSION_ID, undefined);
            assert.strictEqual(env.ASPIRE_EXTENSION_PROMPT_ENABLED, 'true');
            assert.strictEqual(env.ASPIRE_NON_INTERACTIVE, undefined);
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
                { name: 'tab', input: 'hello\tworld', expected: '"hello\tworld"' },
                { name: 'mixed dollar quote backtick', input: '`$x"y"`', expected: '"```$x`"y`"``"' },
                { name: 'attempted PowerShell break-out', input: '"; Remove-Item C:\\ -Recurse #', expected: '"`"; Remove-Item C:\\ -Recurse #"' },
                { name: 'attempted smart double quote break-out', input: 'safe\u201C; Remove-Item C:\\ -Recurse #', expected: '"safe`\u201C; Remove-Item C:\\ -Recurse #"' },
                { name: 'attempted smart single quote break-out', input: 'safe\u2018; Remove-Item C:\\ -Recurse #', expected: '"safe`\u2018; Remove-Item C:\\ -Recurse #"' },
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
                { name: 'tab', input: 'hello\tworld', expected: `'hello\tworld'` },
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

        test('rejects terminal control characters on every platform', () => {
            const cases = ['line1\nline2', 'line1\rline2', 'prefix\x03suffix', 'prefix\x1bsuffix', 'prefix\x7fsuffix'];

            for (const value of cases) {
                assert.throws(() => quoteShellArg(value, 'linux'), { message: terminalCommandArgumentControlCharacters });
                assert.throws(() => quoteShellArg(value, 'win32'), { message: terminalCommandArgumentControlCharacters });
            }
        });
    });
});
