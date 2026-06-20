import * as assert from 'assert';
import * as sinon from 'sinon';
import { getCliSpawnCommand, getCliSpawnDiagnostics } from '../debugger/languages/cli';
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
});
