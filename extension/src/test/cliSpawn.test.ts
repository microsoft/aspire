import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import { spawnSync } from 'child_process';
import { EventEmitter } from 'events';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { PassThrough } from 'stream';
import * as sinon from 'sinon';
import { getCliSpawnCommand, getCliSpawnDiagnostics, mergeCliSpawnEnvironment, spawnCliProcess, terminateCliProcess } from '../debugger/languages/cli';
import { terminalCommandArgumentControlCharacters } from '../loc/strings';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { getCmdShimSpawnCommandWithoutVerbatimArguments } from '../utils/cmdShim';
import { EnvironmentVariables } from '../utils/environment';

suite('spawnCliProcess tests', () => {
    test('creates POSIX process groups only for lifecycle-managed CLI processes', () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        const children = [createTestChildProcess(4101), createTestChildProcess(4102)];
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn');
        spawnStub.onFirstCall().returns(children[0]);
        spawnStub.onSecondCall().returns(children[1]);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['run']);
            spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['ls'], { createProcessGroup: true });

            assert.strictEqual(spawnStub.firstCall.args[2]?.detached, false);
            assert.strictEqual(spawnStub.secondCall.args[2]?.detached, true);
        }
        finally {
            spawnStub.restore();
            platformStub.restore();
        }
    });

    test('force kills a POSIX process group after the grace period while its leader is alive', async () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        const processKillStub = sinon.stub(process, 'kill').returns(true);
        const clock = sinon.useFakeTimers();
        const childProcess = createTestChildProcess(4242);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['ls'], { createProcessGroup: true });
            terminateCliProcess(child, 'test Aspire CLI');
            await clock.tickAsync(5000);

            assert.deepStrictEqual(processKillStub.args, [
                [-4242, 'SIGTERM'],
                [-4242, 'SIGKILL'],
            ]);
            assert.strictEqual(childProcess.kill.called, false);
        }
        finally {
            spawnStub.restore();
            clock.restore();
            processKillStub.restore();
            platformStub.restore();
        }
    });

    test('force kills surviving POSIX descendants immediately when their leader exits', async () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        const processKillStub = sinon.stub(process, 'kill').returns(true);
        const clock = sinon.useFakeTimers();
        const childProcess = createTestChildProcess(4343);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['ls'], { createProcessGroup: true });
            terminateCliProcess(child, 'test Aspire CLI');
            childProcess.emit('close', null);

            assert.deepStrictEqual(processKillStub.args, [
                [-4343, 'SIGTERM'],
                [-4343, 0],
                [-4343, 'SIGKILL'],
            ]);
            await clock.tickAsync(5000);
            assert.strictEqual(processKillStub.callCount, 3);
        }
        finally {
            spawnStub.restore();
            clock.restore();
            processKillStub.restore();
            platformStub.restore();
        }
    });

    test('does not signal a POSIX process group after it exits with its leader', async () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        const noSuchProcess = Object.assign(new Error('No such process'), { code: 'ESRCH' });
        const processKillStub = sinon.stub(process, 'kill');
        processKillStub.onFirstCall().returns(true);
        processKillStub.onSecondCall().throws(noSuchProcess);
        const clock = sinon.useFakeTimers();
        const childProcess = createTestChildProcess(4444);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['ls'], { createProcessGroup: true });
            terminateCliProcess(child, 'test Aspire CLI');
            childProcess.emit('close', null);
            await clock.tickAsync(5000);

            assert.deepStrictEqual(processKillStub.args, [
                [-4444, 'SIGTERM'],
                [-4444, 0],
            ]);
        }
        finally {
            spawnStub.restore();
            clock.restore();
            processKillStub.restore();
            platformStub.restore();
        }
    });

    test('force kills surviving POSIX descendants when termination starts after leader exit', () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        const processKillStub = sinon.stub(process, 'kill').returns(true);
        const childProcess = createTestChildProcess(4545, 0);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['ls'], { createProcessGroup: true });
            terminateCliProcess(child, 'test Aspire CLI');

            assert.deepStrictEqual(processKillStub.args, [
                [-4545, 0],
                [-4545, 'SIGKILL'],
            ]);
        }
        finally {
            spawnStub.restore();
            processKillStub.restore();
            platformStub.restore();
        }
    });

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
    test('terminates the process tree with taskkill on Windows rather than signalling the child', () => {
        // Regression coverage for the Windows CI break: `terminateCliProcess` deliberately never
        // calls `child.kill` on Windows, because killing the leader there orphans its descendants.
        // A test that asserts on `child.kill` therefore passes on POSIX and fails on Windows.
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const spawned: Array<{ command: string; args: readonly string[] }> = [];
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').callsFake(((command: string, args: readonly string[]) => {
            spawned.push({ command, args });
            return Object.assign(new EventEmitter(), { unref: () => { } }) as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        }) as unknown as typeof nodeChildProcess.spawn);
        const child = createTestChildProcess(4747);

        try {
            terminateCliProcess(child, 'test Aspire CLI');

            assert.deepStrictEqual(spawned, [{ command: 'taskkill.exe', args: ['/pid', '4747', '/t'] }]);
            assert.strictEqual(child.kill.callCount, 0);
        }
        finally {
            spawnStub.restore();
            platformStub.restore();
        }
    });

    test('force terminates a POSIX process group immediately without waiting for the grace period', async () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        const processKillStub = sinon.stub(process, 'kill').returns(true);
        const clock = sinon.useFakeTimers();
        const childProcess = createTestChildProcess(4646);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['run'], { createProcessGroup: true });
            terminateCliProcess(child, 'test Aspire CLI', { force: true });

            // No SIGTERM and no escalation timer: a caller that is itself shutting down cannot rely
            // on an `unref`'d timer still being there five seconds later.
            assert.deepStrictEqual(processKillStub.args, [[-4646, 'SIGKILL']]);

            await clock.tickAsync(5000);
            assert.strictEqual(processKillStub.callCount, 1);
        }
        finally {
            spawnStub.restore();
            clock.restore();
            processKillStub.restore();
            platformStub.restore();
        }
    });
});

function createTestChildProcess(pid: number, exitCode: number | null = null): nodeChildProcess.ChildProcessWithoutNullStreams & { kill: sinon.SinonStub } {
    const kill = sinon.stub().returns(true);
    return Object.assign(new EventEmitter(), {
        stdin: new PassThrough(),
        stdout: new PassThrough(),
        stderr: new PassThrough(),
        killed: false,
        exitCode,
        signalCode: null,
        pid,
        kill,
    }) as unknown as nodeChildProcess.ChildProcessWithoutNullStreams & { kill: sinon.SinonStub };
}
