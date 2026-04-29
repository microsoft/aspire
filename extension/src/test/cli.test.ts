import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import { spawnCliProcess, withCliLogOutputChannelArgs } from '../debugger/languages/cli';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { cliLogsOutputChannel } from '../utils/logging';

suite('debugger/languages/cli tests', () => {
    teardown(() => {
        sinon.restore();
    });

    test('adds logging args when none are provided', () => {
        assert.deepStrictEqual(withCliLogOutputChannelArgs(), ['--debug', '--no-log-file']);
    });

    test('inserts logging args before the forwarded app args delimiter', () => {
        const args = ['run', '--apphost', '/repo/AppHost.csproj', '--', '--applicationArg'];

        assert.deepStrictEqual(withCliLogOutputChannelArgs(args), ['run', '--apphost', '/repo/AppHost.csproj', '--debug', '--no-log-file', '--', '--applicationArg']);
    });

    test('does not duplicate logging args that are already present before the delimiter', () => {
        const args = ['run', '--debug', '--no-log-file', '--', '--applicationArg'];

        assert.deepStrictEqual(withCliLogOutputChannelArgs(args), args);
    });

    test('adds only the missing logging arg before the delimiter', () => {
        const args = ['run', '--debug', '--', '--applicationArg'];

        assert.deepStrictEqual(withCliLogOutputChannelArgs(args), ['run', '--debug', '--no-log-file', '--', '--applicationArg']);
    });

    test('routes stderr from logged CLI processes to the CLI logs output channel without logging stdout', async () => {
        const append = sinon.stub(cliLogsOutputChannel, 'append');
        const appendLine = sinon.stub(cliLogsOutputChannel, 'appendLine');
        const terminalProvider = {
            createEnvironment: () => ({ PATH: process.env.PATH ?? '' })
        } as unknown as AspireTerminalProvider;
        const stdout: string[] = [];
        const stderr: string[] = [];
        const tempDirectory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-cli-test-'));

        try {
            const scriptPath = path.join(tempDirectory, process.platform === 'win32' ? 'write-output.cmd' : 'write-output.sh');
            fs.writeFileSync(
                scriptPath,
                process.platform === 'win32'
                    ? '@echo off\r\nset /p dummy=stdout from cli<nul\r\nset /p dummy=stderr from cli<nul 1>&2\r\nexit /b 0\r\n'
                    : "#!/bin/sh\nprintf 'stdout from cli'\nprintf 'stderr from cli' >&2\n");
            fs.chmodSync(scriptPath, 0o755);
            const command = process.platform === 'win32' ? (process.env.ComSpec ?? 'cmd.exe') : '/bin/sh';
            const args = process.platform === 'win32' ? ['/d', '/s', '/c', scriptPath] : [scriptPath];

            const exitCode = await new Promise<number | null>((resolve, reject) => {
                spawnCliProcess(
                    terminalProvider,
                    command,
                    args,
                    {
                        workingDirectory: process.cwd(),
                        logToCliOutputChannel: true,
                        stdoutCallback: data => stdout.push(data),
                        stderrCallback: data => stderr.push(data),
                        exitCallback: resolve,
                        errorCallback: reject,
                    });
            });

            assert.strictEqual(exitCode, 0);
            assert.strictEqual(stdout.join(''), 'stdout from cli');
            assert.strictEqual(stderr.join(''), 'stderr from cli');
            assert.ok(append.calledWith('stderr from cli'), 'should write stderr to the CLI logs output channel');
            assert.ok(!append.getCalls().some(call => String(call.args[0]).includes('stdout from cli')), 'should not write stdout to the CLI logs output channel');

            const outputLines = appendLine.getCalls().map(call => String(call.args[0]));
            assert.ok(outputLines.some(line => line.includes('Spawning CLI process') && line.includes('--debug') && line.includes('--no-log-file')), 'should log the spawned CLI command with output-channel logging args');
            assert.ok(outputLines.some(line => line.includes('CLI process exited with code 0')), 'should log the spawned CLI process exit');
        }
        finally {
            fs.rmSync(tempDirectory, { recursive: true, force: true });
        }
    });
});
