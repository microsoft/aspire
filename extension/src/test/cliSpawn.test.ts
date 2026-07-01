import * as assert from 'assert';
import * as sinon from 'sinon';
import { getCliSpawnCommand, getCliSpawnDiagnostics, mergeCliSpawnEnvironment, spawnCliProcess } from '../debugger/languages/cli';
import { terminalCommandArgumentControlCharacters } from '../loc/strings';
import { EnvironmentVariables } from '../utils/environment';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { extensionLogOutputChannel } from '../utils/logging';

suite('spawnCliProcess tests', () => {
    test('uses direct execution for unresolved bare Windows command', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('aspire', ['config', 'info']);

            assert.strictEqual(result.command, 'aspire');
            assert.deepStrictEqual(result.args, ['config', 'info']);
            // Bare commands (no .cmd/.bat) must not be routed through the cmd.exe wrapper so that
            // an unresolvable shim does not get re-launched through cmd.exe and accidentally pick
            // up a workspace-local aspire.cmd or aspire.bat on the current directory.
            assert.ok(!result.windowsVerbatimArguments);
        }
        finally {
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

    test('runs Windows cmd wrappers through cmd.exe', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('C:\\Tools\\Aspire CLI\\aspire.cmd', ['config', 'info']);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.deepStrictEqual(result.args, ['/d', '/v:off', '/s', '/c', 'call "C:\\Tools\\Aspire CLI\\aspire.cmd" "config" "info"']);
            assert.strictEqual(result.windowsVerbatimArguments, true);
        }
        finally {
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

    test('quotes hostile arguments when running Windows cmd wrappers', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('C:\\Tools\\Aspire CLI\\aspire.cmd', [
                'resource',
                'api&whoami',
                'echo',
                '--',
                '--message=hello & del C:\\important',
                '--path=%PATH%',
                '--literal="quoted"',
            ]);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.deepStrictEqual(result.args, [
                '/d',
                '/v:off',
                '/s',
                '/c',
                'call "C:\\Tools\\Aspire CLI\\aspire.cmd" "resource" "api&whoami" "echo" "--" "--message=hello & del C:\\important" "--path="^%"PATH"^% "--literal=""quoted"""'
            ]);
            assert.strictEqual(result.windowsVerbatimArguments, true);
        }
        finally {
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

    test('rejects control characters when running Windows cmd wrappers', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const cases = [
                {
                    name: 'command path',
                    command: 'C:\\Tools\\Aspire\nCLI\\aspire.cmd',
                    args: ['resource', 'api', 'restart'],
                },
                {
                    name: 'resource name',
                    command: 'C:\\Tools\\Aspire CLI\\aspire.cmd',
                    args: ['resource', 'api\r\nwhoami', 'restart'],
                },
                {
                    name: 'command name',
                    command: 'C:\\Tools\\Aspire CLI\\aspire.bat',
                    args: ['resource', 'api', 'restart\x1b[31m'],
                },
                {
                    name: 'resource command argument',
                    command: 'C:\\Tools\\Aspire CLI\\aspire.cmd',
                    args: ['resource', 'api', 'echo-arguments', '--', '--message=hello\x03world'],
                },
            ];

            for (const { name, command, args } of cases) {
                assert.throws(
                    () => getCliSpawnCommand(command, args),
                    { message: terminalCommandArgumentControlCharacters },
                    name);
            }
        }
        finally {
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

    test('doubles trailing backslashes when quoting Windows cmd wrapper arguments', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('C:\\Tools\\Aspire CLI\\aspire.cmd', [
                '--path=C:\\temp\\',
                'next',
            ]);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.deepStrictEqual(result.args, [
                '/d',
                '/v:off',
                '/s',
                '/c',
                String.raw`call "C:\Tools\Aspire CLI\aspire.cmd" "--path=C:\temp\\" "next"`
            ]);
            assert.strictEqual(result.windowsVerbatimArguments, true);
        }
        finally {
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

    test('doubles backslashes before embedded quotes when quoting Windows cmd wrapper arguments', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('C:\\Tools\\Aspire CLI\\aspire.cmd', [
                String.raw`--literal=C:\temp\"quoted"`,
            ]);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.deepStrictEqual(result.args, [
                '/d',
                '/v:off',
                '/s',
                '/c',
                String.raw`call "C:\Tools\Aspire CLI\aspire.cmd" "--literal=C:\temp\\""quoted"""`
            ]);
            assert.strictEqual(result.windowsVerbatimArguments, true);
        }
        finally {
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

    test('formats final startup timeout when spawning CLI process', () => {
        const message = getCliSpawnDiagnostics(
            '/usr/local/bin/aspire',
            ['run', '--apphost', '/workspace/AppHost.csproj'],
            '/workspace',
            false,
            'debug-session-id',
            {
                [EnvironmentVariables.ASPIRE_CLI_START_TIMEOUT]: '86400',
                ASPIRE_EXTENSION_TOKEN: 'secret-token',
            });

        assert.strictEqual(
            message,
            'Spawning Aspire CLI process: /usr/local/bin/aspire run --apphost /workspace/AppHost.csproj; cwd=/workspace; noDebug=false; debugSessionId=debug-session-id; ASPIRE_CLI_START_TIMEOUT=86400');
        assert.strictEqual(message.includes('secret-token'), false);
    });

    test('redacts command arguments after delimiter from spawn diagnostics', () => {
        const message = getCliSpawnDiagnostics(
            '/usr/local/bin/aspire',
            ['resource', 'database', 'reset-password', '--load-arguments', '--', '--password=s3cr3t'],
            '/workspace',
            undefined,
            undefined,
            {});

        assert.strictEqual(
            message,
            'Spawning Aspire CLI process: /usr/local/bin/aspire resource database reset-password --load-arguments -- <redacted>; cwd=/workspace; noDebug=undefined; debugSessionId=undefined; ASPIRE_CLI_START_TIMEOUT=undefined');
        assert.strictEqual(message.includes('s3cr3t'), false);
    });

    test('merges caller env case-insensitively on Windows', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const env: Record<string, string | undefined> = {
            [EnvironmentVariables.ASPIRE_CLI_START_TIMEOUT]: '86400',
        };

        try {
            mergeCliSpawnEnvironment(env, [{ name: 'aspire_cli_start_timeout', value: '300' }]);

            assert.strictEqual(env.ASPIRE_CLI_START_TIMEOUT, undefined);
            assert.strictEqual(env.aspire_cli_start_timeout, '300');
        }
        finally {
            platformStub.restore();
        }
    });

    test('formats startup timeout diagnostics case-insensitively on Windows', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');

        try {
            const message = getCliSpawnDiagnostics(
                'C:\\Tools\\aspire.exe',
                ['run'],
                'C:\\workspace',
                false,
                'debug-session-id',
                {
                    aspire_cli_start_timeout: '300',
                });

            assert.strictEqual(
                message,
                'Spawning Aspire CLI process: C:\\Tools\\aspire.exe run; cwd=C:\\workspace; noDebug=false; debugSessionId=debug-session-id; ASPIRE_CLI_START_TIMEOUT=300');
        }
        finally {
            platformStub.restore();
        }
    });

    test('redacts original command arguments from Windows cmd wrapper spawn diagnostics', () => {
        // spawnCliProcess logs the original CLI command and argv instead of the cmd.exe wrapper
        // command line, where resource command secrets (e.g. password args after `--`) can be
        // embedded verbatim before the normal redaction rule gets a chance to run.
        const actualPlatform = process.platform;
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = actualPlatform === 'win32' ? originalComSpec ?? 'C:\\Windows\\System32\\cmd.exe' : '/bin/echo';

        const infoStub = sinon.stub(extensionLogOutputChannel, 'info');
        const terminalProvider = {
            createEnvironment: () => ({}),
        } as AspireTerminalProvider;

        try {
            spawnCliProcess(
                terminalProvider,
                'C:\\Tools\\aspire.cmd',
                ['resource', 'database', 'reset-password', '--load-arguments', '--', '--password=s3cr3t'],
                { workingDirectory: 'C:\\workspace' });

            const message = infoStub.firstCall.args[0];
            assert.strictEqual(
                message,
                'Spawning Aspire CLI process: C:\\Tools\\aspire.cmd resource database reset-password --load-arguments -- <redacted>; cwd=C:\\workspace; noDebug=undefined; debugSessionId=undefined; ASPIRE_CLI_START_TIMEOUT=undefined');
            assert.strictEqual(message.includes('s3cr3t'), false);
        }
        finally {
            infoStub.restore();
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });
});
