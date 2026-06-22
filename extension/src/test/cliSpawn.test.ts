import * as assert from 'assert';
import * as sinon from 'sinon';
import { getCliSpawnCommand, getCliSpawnDiagnostics, mergeCliSpawnEnvironment } from '../debugger/languages/cli';
import { EnvironmentVariables } from '../utils/environment';

suite('spawnCliProcess tests', () => {
    test('runs Windows cmd wrappers through cmd.exe', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('C:\\Tools\\Aspire CLI\\aspire.cmd', ['config', 'info']);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.deepStrictEqual(result.args, ['/d', '/c', 'call', 'C:\\Tools\\Aspire CLI\\aspire.cmd', 'config', 'info']);
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
