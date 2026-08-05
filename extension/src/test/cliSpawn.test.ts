import * as assert from 'assert';
import { spawnSync } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import { getCliSpawnCommand, getCliSpawnDiagnostics, mergeCliSpawnEnvironment } from '../debugger/languages/cli';
import { terminalCommandArgumentControlCharacters } from '../loc/strings';
import { getCmdShimSpawnCommandWithoutVerbatimArguments } from '../utils/cmdShim';
import { EnvironmentVariables } from '../utils/environment';

suite('spawnCliProcess tests', () => {
    test('runs Windows cmd wrappers through cmd.exe', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('C:\\Tools\\Aspire CLI\\aspire.cmd', ['config', 'info']);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.deepStrictEqual(result.args, ['/d', '/v:off', '/s', '/c', '""C:\\Tools\\Aspire CLI\\aspire.cmd" "config" "info""']);
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
                '--literal="quoted"',
            ]);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.deepStrictEqual(result.args, [
                '/d',
                '/v:off',
                '/s',
                '/c',
                '""C:\\Tools\\Aspire CLI\\aspire.cmd" "resource" "api&whoami" "echo" "--" "--message=hello & del C:\\important" "--literal=""quoted""""'
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

    test('does not rewrite percent sequences that cmd command lines cannot escape', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');

        try {
            const result = getCliSpawnCommand(
                'C:\\Tools\\Aspire CLI\\aspire.cmd',
                ['resource', 'api', 'echo', '--', '--path=%PATH%'],
            );

            assert.strictEqual(
                result.args[4],
                '""C:\\Tools\\Aspire CLI\\aspire.cmd" "resource" "api" "echo" "--" "--path=%PATH%""');
        }
        finally {
            platformStub.restore();
        }
    });

    test('does not rewrite percent sequences in non-verbatim cmd wrappers', () => {
        const result = getCmdShimSpawnCommandWithoutVerbatimArguments(
            'C:\\tools\\%ASPIRE_HOME%\\aspire.cmd',
            ['--path=%PATH%'],
        );

        assert.deepStrictEqual(result.args, [
            '/d',
            '/v:off',
            '/c',
            'C:\\tools\\%ASPIRE_HOME%\\aspire.cmd',
            '--path^=%PATH%',
        ]);
    });

    test('rejects non-verbatim cmd wrappers with multiple tokens requiring libuv quotes', () => {
        assert.throws(
            () => getCmdShimSpawnCommandWithoutVerbatimArguments(
                'C:\\Program Files\\Aspire\\aspire.cmd',
                ['--message=hello world'],
            ),
            /cannot safely quote arguments containing whitespace or quotes/);
    });

    test('rejects empty arguments in non-verbatim cmd wrappers', () => {
        assert.throws(
            () => getCmdShimSpawnCommandWithoutVerbatimArguments(
                'C:\\Program Files\\Aspire\\aspire.cmd',
                ['agent', ''],
            ),
            /cannot safely quote arguments containing whitespace or quotes/);
    });

    test('runs non-verbatim cmd wrappers from paths combining spaces and metacharacters', function () {
        if (process.platform !== 'win32') {
            this.skip();
        }

        const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire mcp&a^b(x),c;d-[e]-'));

        try {
            const wrapperPath = path.join(tempDirectory, 'aspire.cmd');
            fs.writeFileSync(wrapperPath, [
                '@echo off',
                'if "%~1"=="echo-argument" (',
                '  echo(%~2',
                '  exit /b 0',
                ')',
                'exit /b 1',
                '',
            ].join('\r\n'));

            const { command, args } = getCmdShimSpawnCommandWithoutVerbatimArguments(
                wrapperPath,
                ['echo-argument', 'mcp-started'],
            );
            const result = spawnSync(command, args, { encoding: 'utf8' });

            assert.strictEqual(result.status, 0, result.stderr);
            assert.strictEqual(result.stdout.trim(), 'mcp-started');
        }
        finally {
            fs.rmSync(tempDirectory, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
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
                String.raw`""C:\Tools\Aspire CLI\aspire.cmd" "--path=C:\temp\\" "next""`
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
                String.raw`""C:\Tools\Aspire CLI\aspire.cmd" "--literal=C:\temp\\""quoted""""`
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
});
