import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import { spawnSync } from 'child_process';
import { EventEmitter } from 'events';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { PassThrough } from 'stream';
import * as sinon from 'sinon';
import { getCliSpawnCommand, getCliSpawnDiagnostics, mergeCliSpawnEnvironment, spawnCliProcess, terminateCliProcess } from '../utils/process/cliProcess';
import { terminalCommandArgumentControlCharacters } from '../loc/strings';
import type { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { getCmdShimSpawnCommandWithoutVerbatimArguments } from '../utils/cmdShim';
import { EnvironmentVariables } from '../utils/environment';

import { removeDirectorySafely } from './testHelpers';
suite('spawnCliProcess tests', () => {
    test('builds the child environment from the exact CLI command being launched', () => {
        const childProcess = createTestChildProcess(4801);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const createEnvironmentStub = sinon.stub().returns({});
        const terminalProvider = { createEnvironment: createEnvironmentStub } as unknown as AspireTerminalProvider;

        try {
            spawnCliProcess(terminalProvider, '/repo/a/bin/aspire', ['config', 'info']);

            assert.ok(createEnvironmentStub.calledOnceWith(undefined, undefined, undefined, '/repo/a/bin/aspire'));
        }
        finally {
            spawnStub.restore();
        }
    });

    test('passes the original cmd shim path to createEnvironment on Windows, not cmd.exe', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';
        const childProcess = createTestChildProcess(4802);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const createEnvironmentStub = sinon.stub().returns({});
        const terminalProvider = { createEnvironment: createEnvironmentStub } as unknown as AspireTerminalProvider;

        try {
            spawnCliProcess(terminalProvider, 'C:\\repo\\a\\aspire.cmd', ['config', 'info']);

            // createEnvironment must receive the original shim path so the terminal provider
            // can compute correct environment variables relative to the CLI install location.
            assert.ok(createEnvironmentStub.calledOnceWith(undefined, undefined, undefined, 'C:\\repo\\a\\aspire.cmd'));
            // The actual spawn must go through cmd.exe, not the shim directly.
            assert.strictEqual(spawnStub.firstCall.args[0], 'C:\\Windows\\System32\\cmd.exe');
            assert.notStrictEqual(spawnStub.firstCall.args[0], 'C:\\repo\\a\\aspire.cmd');
        }
        finally {
            spawnStub.restore();
            platformStub.restore();

            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

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
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const childProcess = createTestChildProcess(4242);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['ls'], { createProcessGroup: true });
            const termination = terminateCliProcess(child, 'test Aspire CLI');
            await clock.tickAsync(5000);

            assert.deepStrictEqual(processKillStub.args, [
                [-4242, 'SIGTERM'],
                [-4242, 'SIGKILL'],
            ]);
            assert.strictEqual(childProcess.kill.called, false);

            processKillStub.callsFake((_pid, signal) => {
                if (signal === 0) {
                    throw Object.assign(new Error('No such process'), { code: 'ESRCH' });
                }

                return true;
            });
            await clock.tickAsync(50);
            await termination;
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
        let processGroupAlive = true;
        const noSuchProcess = Object.assign(new Error('No such process'), { code: 'ESRCH' });
        const processKillStub = sinon.stub(process, 'kill').callsFake((_pid, signal) => {
            if (signal === 0 && !processGroupAlive) {
                throw noSuchProcess;
            }

            return true;
        });
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const childProcess = createTestChildProcess(4343);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['ls'], { createProcessGroup: true });
            const closeListenerCount = childProcess.listenerCount('close');
            const exitListenerCount = childProcess.listenerCount('exit');
            let settled = false;
            const termination = terminateCliProcess(child, 'test Aspire CLI').then(() => { settled = true; });
            childProcess.emit('close', null);
            await clock.tickAsync(0);

            assert.deepStrictEqual(processKillStub.args, [
                [-4343, 'SIGTERM'],
                [-4343, 0],
                [-4343, 'SIGKILL'],
                [-4343, 0],
            ]);
            assert.strictEqual(settled, false, 'Expected termination to wait until the surviving process group exits.');

            processGroupAlive = false;
            await clock.tickAsync(50);
            await termination;
            assert.strictEqual(settled, true);
            assert.strictEqual(childProcess.listenerCount('close'), closeListenerCount);
            assert.strictEqual(childProcess.listenerCount('exit'), exitListenerCount);
            assert.strictEqual(clock.countTimers(), 0);
        }
        finally {
            spawnStub.restore();
            clock.restore();
            processKillStub.restore();
            platformStub.restore();
        }
    });

    test('rejects when a POSIX process group remains alive after forced termination', async () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        const processKillStub = sinon.stub(process, 'kill').returns(true);
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const childProcess = createTestChildProcess(4595);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['run'], { createProcessGroup: true });
            const closeListenerCount = childProcess.listenerCount('close');
            const exitListenerCount = childProcess.listenerCount('exit');
            let rejection: unknown;
            void terminateCliProcess(child, 'test Aspire CLI', { force: true }).catch(error => { rejection = error; });

            await clock.tickAsync(5000);

            assert.match(String(rejection), /Could not confirm test Aspire CLI process group termination within 5000ms/);
            assert.strictEqual(childProcess.listenerCount('close'), closeListenerCount);
            assert.strictEqual(childProcess.listenerCount('exit'), exitListenerCount);
            assert.strictEqual(clock.countTimers(), 0);
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
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
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

    test('force kills surviving POSIX descendants when termination starts after leader exit', async () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        let processGroupAlive = true;
        const noSuchProcess = Object.assign(new Error('No such process'), { code: 'ESRCH' });
        const processKillStub = sinon.stub(process, 'kill').callsFake((_pid, signal) => {
            if (signal === 0 && !processGroupAlive) {
                throw noSuchProcess;
            }

            return true;
        });
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const childProcess = createTestChildProcess(4545, 0);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['ls'], { createProcessGroup: true });
            let settled = false;
            const termination = terminateCliProcess(child, 'test Aspire CLI').then(() => { settled = true; });

            assert.deepStrictEqual(processKillStub.args, [
                [-4545, 0],
                [-4545, 'SIGKILL'],
                [-4545, 0],
            ]);
            assert.strictEqual(settled, false, 'Expected termination to wait until the surviving process group exits.');

            processGroupAlive = false;
            await clock.tickAsync(50);
            await termination;
            assert.strictEqual(settled, true);
        }
        finally {
            spawnStub.restore();
            clock.restore();
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

    test('rejects percent sequences that cmd command lines would expand', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');

        try {
            assert.throws(
                () => getCliSpawnCommand(
                    'C:\\Tools\\Aspire CLI\\aspire.cmd',
                    ['resource', 'api', 'echo', '--', '--path=%PATH%']),
                /Arguments containing %NAME% cannot be forwarded through a Windows \.cmd or \.bat shim/);
        }
        finally {
            platformStub.restore();
        }
    });

    test('allows percent expansion in a command shim path but not its forwarded arguments', () => {
        const result = getCmdShimSpawnCommandWithoutVerbatimArguments(
            'C:\\tools\\%ASPIRE_HOME%\\aspire.cmd',
            ['agent', 'mcp'],
        );

        assert.deepStrictEqual(result.args, [
            '/d',
            '/v:off',
            '/c',
            'C:\\tools\\%ASPIRE_HOME%\\aspire.cmd',
            'agent',
            'mcp',
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
            removeDirectorySafely(tempDirectory);
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

    test('rejects percent expansion in Windows command shim arguments', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');

        try {
            assert.throws(
                () => getCliSpawnCommand('C:\\Tools\\aspire.cmd', ['run', '--', '%ASPIRE_SECRET%']),
                /Arguments containing %NAME% cannot be forwarded through a Windows \.cmd or \.bat shim/);
        }
        finally {
            platformStub.restore();
        }
    });

    test('allows a single unmatched percent in Windows command shim arguments', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');

        try {
            assert.doesNotThrow(
                () => getCliSpawnCommand('C:\\Tools\\aspire.cmd', ['run', '--', '100%']));
        }
        finally {
            platformStub.restore();
        }
    });

    test('rejects percent expansion sequences that span Windows command shim arguments', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');

        try {
            assert.throws(
                () => getCliSpawnCommand('C:\\Tools\\aspire.cmd', ['run', '--', 'before%', '%after']),
                /Arguments containing %NAME% cannot be forwarded through a Windows \.cmd or \.bat shim/);
        }
        finally {
            platformStub.restore();
        }
    });

    test('waits for taskkill and child close when terminating a Windows process tree', async () => {
        // Regression coverage for the Windows CI break: `terminateCliProcess` deliberately never
        // calls `child.kill` on Windows, because killing the leader there orphans its descendants.
        // A test that asserts on `child.kill` therefore passes on POSIX and fails on Windows.
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const spawned: Array<{ command: string; args: readonly string[] }> = [];
        const taskkillUnref = sinon.stub();
        const taskkillProcess = Object.assign(new EventEmitter(), { unref: taskkillUnref });
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').callsFake(((command: string, args: readonly string[]) => {
            spawned.push({ command, args });
            return taskkillProcess as unknown as nodeChildProcess.ChildProcessWithoutNullStreams;
        }) as unknown as typeof nodeChildProcess.spawn);
        const child = createTestChildProcess(4747);

        try {
            let settled = false;
            const termination = terminateCliProcess(child, 'test Aspire CLI').then(() => { settled = true; });

            assert.deepStrictEqual(spawned, [{ command: 'taskkill.exe', args: ['/pid', '4747', '/t'] }]);
            assert.strictEqual(child.kill.callCount, 0);
            sinon.assert.notCalled(taskkillUnref);

            child.emit('close', null);
            await Promise.resolve();
            assert.strictEqual(settled, false, 'Expected termination to await taskkill completion after the child closes.');

            taskkillProcess.emit('close', 0);
            await termination;
            assert.strictEqual(settled, true);
            assert.strictEqual(child.listenerCount('close'), 0);
            assert.strictEqual(child.listenerCount('exit'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('close'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('error'), 0);
        }
        finally {
            spawnStub.restore();
            platformStub.restore();
        }
    });

    test('rejects when taskkill does not complete within the bounded deadline', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const taskkillKill = sinon.stub().returns(true);
        const taskkillProcess = Object.assign(new EventEmitter(), { kill: taskkillKill });
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(
            taskkillProcess as unknown as nodeChildProcess.ChildProcessWithoutNullStreams);
        const child = createTestChildProcess(4848);

        try {
            let rejection: unknown;
            void terminateCliProcess(child, 'test Aspire CLI', { force: true }).catch(error => { rejection = error; });

            await clock.tickAsync(5000);

            assert.match(String(rejection), /test Aspire CLI did not report exit within 5000ms after taskkill failed and the leader was killed/);
            assert.deepStrictEqual(spawnStub.firstCall.args[1], ['/pid', '4848', '/t', '/f']);
            sinon.assert.calledOnceWithExactly(taskkillKill, 'SIGKILL');
            sinon.assert.calledOnceWithExactly(child.kill, 'SIGKILL');
            assert.strictEqual(child.listenerCount('close'), 0);
            assert.strictEqual(child.listenerCount('exit'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('close'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('error'), 0);
            assert.strictEqual(clock.countTimers(), 0);
        }
        finally {
            spawnStub.restore();
            clock.restore();
            platformStub.restore();
        }
    });

    test('best-effort kills the leader but reports failure when taskkill cannot start', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const taskkillProcess = Object.assign(new EventEmitter(), { kill: sinon.stub().returns(true) });
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(
            taskkillProcess as unknown as nodeChildProcess.ChildProcessWithoutNullStreams);
        const child = createTestChildProcess(4898);

        try {
            const termination = terminateCliProcess(child, 'test Aspire CLI', { force: true });
            taskkillProcess.emit('error', new Error('spawn failed'));
            await Promise.resolve();
            sinon.assert.calledOnceWithExactly(child.kill, 'SIGKILL');
            (child as unknown as { exitCode: number | null }).exitCode = 1;
            child.emit('close', 1);

            await assert.rejects(
                termination,
                /Failed to run taskkill for test Aspire CLI \(PID 4898\): spawn failed/);
            assert.strictEqual(child.listenerCount('close'), 0);
            assert.strictEqual(child.listenerCount('exit'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('close'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('error'), 0);
            assert.strictEqual(clock.countTimers(), 0);
        }
        finally {
            spawnStub.restore();
            clock.restore();
            platformStub.restore();
        }
    });

    test('escalates a nonzero graceful taskkill result to forced tree termination', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const gracefulTaskkill = Object.assign(new EventEmitter(), { kill: sinon.stub().returns(true) });
        const forcedTaskkill = Object.assign(new EventEmitter(), { kill: sinon.stub().returns(true) });
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn');
        spawnStub.onFirstCall().returns(gracefulTaskkill as unknown as nodeChildProcess.ChildProcessWithoutNullStreams);
        spawnStub.onSecondCall().returns(forcedTaskkill as unknown as nodeChildProcess.ChildProcessWithoutNullStreams);
        const child = createTestChildProcess(4949);

        try {
            const termination = terminateCliProcess(child, 'test Aspire CLI');
            gracefulTaskkill.emit('close', 5);
            await new Promise(resolve => setImmediate(resolve));
            assert.deepStrictEqual(spawnStub.secondCall.args[1], ['/pid', '4949', '/t', '/f']);

            (child as unknown as { exitCode: number | null }).exitCode = 1;
            child.emit('close', 1);
            forcedTaskkill.emit('close', 0);
            await termination;
            sinon.assert.notCalled(child.kill);
            assert.strictEqual(child.listenerCount('close'), 0);
            assert.strictEqual(child.listenerCount('exit'), 0);
            assert.strictEqual(gracefulTaskkill.listenerCount('close'), 0);
            assert.strictEqual(gracefulTaskkill.listenerCount('error'), 0);
            assert.strictEqual(forcedTaskkill.listenerCount('close'), 0);
            assert.strictEqual(forcedTaskkill.listenerCount('error'), 0);
        }
        finally {
            spawnStub.restore();
            platformStub.restore();
        }
    });

    test('does not re-target a PID after taskkill reports that it no longer exists', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const taskkillProcess = Object.assign(new EventEmitter(), { kill: sinon.stub().returns(true) });
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(
            taskkillProcess as unknown as nodeChildProcess.ChildProcessWithoutNullStreams);
        const child = createTestChildProcess(4999);

        try {
            const termination = terminateCliProcess(child, 'test Aspire CLI', { force: true });
            taskkillProcess.emit('close', 128);
            await new Promise(resolve => setImmediate(resolve));

            sinon.assert.calledOnce(spawnStub);
            assert.deepStrictEqual(spawnStub.firstCall.args[1], ['/pid', '4999', '/t', '/f']);
            sinon.assert.notCalled(child.kill);

            (child as unknown as { exitCode: number | null }).exitCode = 0;
            child.emit('close', 0);
            await termination;
        }
        finally {
            spawnStub.restore();
            platformStub.restore();
        }
    });

    test('does not re-target a PID when forced taskkill escalation reports that it no longer exists', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const gracefulTaskkill = Object.assign(new EventEmitter(), { kill: sinon.stub().returns(true) });
        const forcedTaskkill = Object.assign(new EventEmitter(), { kill: sinon.stub().returns(true) });
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn');
        spawnStub.onFirstCall().returns(gracefulTaskkill as unknown as nodeChildProcess.ChildProcessWithoutNullStreams);
        spawnStub.onSecondCall().returns(forcedTaskkill as unknown as nodeChildProcess.ChildProcessWithoutNullStreams);
        const child = createTestChildProcess(5049);

        try {
            const termination = terminateCliProcess(child, 'test Aspire CLI');
            gracefulTaskkill.emit('close', 5);
            await new Promise(resolve => setImmediate(resolve));
            forcedTaskkill.emit('close', 128);
            await new Promise(resolve => setImmediate(resolve));

            sinon.assert.calledTwice(spawnStub);
            assert.deepStrictEqual(spawnStub.secondCall.args[1], ['/pid', '5049', '/t', '/f']);
            sinon.assert.notCalled(child.kill);

            (child as unknown as { exitCode: number | null }).exitCode = 0;
            child.emit('close', 0);
            await termination;
        }
        finally {
            spawnStub.restore();
            platformStub.restore();
        }
    });

    test('accepts a nonzero taskkill result when the target process already exited', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const taskkillProcess = Object.assign(new EventEmitter(), { kill: sinon.stub().returns(true) });
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(
            taskkillProcess as unknown as nodeChildProcess.ChildProcessWithoutNullStreams);
        const child = createTestChildProcess(5050);

        try {
            const termination = terminateCliProcess(child, 'test Aspire CLI', { force: true });
            (child as unknown as { exitCode: number | null }).exitCode = 0;
            child.emit('close', 0);
            taskkillProcess.emit('close', 128);

            await termination;
            sinon.assert.notCalled(child.kill);
            assert.strictEqual(child.listenerCount('close'), 0);
            assert.strictEqual(child.listenerCount('exit'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('close'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('error'), 0);
        }
        finally {
            spawnStub.restore();
            platformStub.restore();
        }
    });

    test('rejects when a Windows child does not close after successful taskkill', async () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const taskkillProcess = Object.assign(new EventEmitter(), { kill: sinon.stub().returns(true) });
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(
            taskkillProcess as unknown as nodeChildProcess.ChildProcessWithoutNullStreams);
        const child = createTestChildProcess(5151);

        try {
            let rejection: unknown;
            void terminateCliProcess(child, 'test Aspire CLI', { force: true }).catch(error => { rejection = error; });
            taskkillProcess.emit('close', 0);

            await clock.tickAsync(5000);

            assert.match(String(rejection), /test Aspire CLI did not report exit within 5000ms after taskkill succeeded/);
            assert.deepStrictEqual(spawnStub.firstCall.args[1], ['/pid', '5151', '/t', '/f']);
            sinon.assert.notCalled(child.kill);
            assert.strictEqual(child.listenerCount('close'), 0);
            assert.strictEqual(child.listenerCount('exit'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('close'), 0);
            assert.strictEqual(taskkillProcess.listenerCount('error'), 0);
            assert.strictEqual(clock.countTimers(), 0);
        }
        finally {
            spawnStub.restore();
            clock.restore();
            platformStub.restore();
        }
    });

    test('force terminates a POSIX process group immediately without waiting for the grace period', async () => {
        const platformStub = sinon.stub(process, 'platform').value('linux');
        let processGroupAlive = true;
        const noSuchProcess = Object.assign(new Error('No such process'), { code: 'ESRCH' });
        const processKillStub = sinon.stub(process, 'kill').callsFake((_pid, signal) => {
            if (signal === 0 && !processGroupAlive) {
                throw noSuchProcess;
            }

            return true;
        });
        const clock = sinon.useFakeTimers({ shouldClearNativeTimers: true });
        const childProcess = createTestChildProcess(4646);
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').returns(childProcess);
        const terminalProvider = { createEnvironment: () => ({}) } as AspireTerminalProvider;

        try {
            const child = spawnCliProcess(terminalProvider, '/usr/local/bin/aspire', ['run'], { createProcessGroup: true });
            let settled = false;
            const termination = terminateCliProcess(child, 'test Aspire CLI', { force: true }).then(() => { settled = true; });

            // No SIGTERM and no escalation timer: a caller that is itself shutting down cannot rely
            // on an `unref`'d timer still being there five seconds later.
            assert.deepStrictEqual(processKillStub.args, [
                [-4646, 'SIGKILL'],
            ]);
            await clock.tickAsync(50);
            assert.strictEqual(settled, false, 'Expected forced termination to confirm that the process group exited.');

            processGroupAlive = false;
            await clock.tickAsync(50);
            await termination;
            assert.strictEqual(settled, true);
            assert.deepStrictEqual(processKillStub.args, [
                [-4646, 'SIGKILL'],
                [-4646, 0],
                [-4646, 0],
            ]);
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
