import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import { getCliSpawnCommand, spawnCliProcess } from '../debugger/languages/cli';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { getAspireCliPathForMSBuild, mergeEnvs, withAspireCliPathForMSBuild } from '../utils/environment';

suite('spawnCliProcess tests', () => {
    test('runs Windows cmd wrappers through cmd.exe', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('C:\\Tools\\Aspire CLI\\aspire.cmd', ['config', 'info']);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.strictEqual('windowsVerbatimArguments' in result ? result.windowsVerbatimArguments : undefined, true);
            assert.deepStrictEqual(result.args, ['/d', '/s', '/c', 'call "C:\\Tools\\Aspire CLI\\aspire.cmd" "config" "info"']);
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

    test('escapes Windows cmd wrapper paths and args with command metacharacters', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('C:\\R&D\\aspire.cmd', ['config', 'set', 'features.x', 'a&b']);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.strictEqual('windowsVerbatimArguments' in result ? result.windowsVerbatimArguments : undefined, true);
            assert.deepStrictEqual(result.args, ['/d', '/s', '/c', 'call "C:\\R&D\\aspire.cmd" "config" "set" "features.x" "a&b"']);
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

    test('does not set MSBuild AspireCliPath for bare PATH commands', () => {
        assert.strictEqual(getAspireCliPathForMSBuild('aspire'), undefined);
        assert.strictEqual(getAspireCliPathForMSBuild('aspire.exe'), undefined);
        assert.strictEqual(getAspireCliPathForMSBuild('aspire.cmd'), undefined);
        assert.strictEqual(getAspireCliPathForMSBuild('aspire.bat'), undefined);
    });

    test('resolves explicit CLI paths for MSBuild AspireCliPath', () => {
        const workingDirectory = path.join(path.sep, 'workspace');
        const relativeCliPath = path.join('artifacts', 'bin', 'Aspire.Cli', 'Debug', 'net10.0', 'aspire');
        const absoluteCliPath = path.join(path.sep, 'repo', relativeCliPath);
        const commandShimPath = path.join(path.sep, 'repo', 'tools', 'aspire.cmd');

        assert.strictEqual(getAspireCliPathForMSBuild(absoluteCliPath, workingDirectory), absoluteCliPath);
        assert.strictEqual(getAspireCliPathForMSBuild(relativeCliPath, workingDirectory), path.resolve(workingDirectory, relativeCliPath));
        assert.strictEqual(getAspireCliPathForMSBuild(commandShimPath, workingDirectory), commandShimPath);
    });

    test('removes stale MSBuild AspireCliPath when resolved CLI is a PATH command', () => {
        const env = [
            { name: 'AspireCliPath', value: '/stale/from/old/session/aspire' },
            { name: 'OTHER_ENV', value: '1' },
        ];

        assert.deepStrictEqual(withAspireCliPathForMSBuild(env, 'aspire'), [
            { name: 'OTHER_ENV', value: '1' },
        ]);
    });

    test('removes stale MSBuild AspireCliPath from inherited environment', () => {
        const env = mergeEnvs({
            AspireCliPath: '/stale/from/old/session/aspire',
            OTHER_ENV: '1',
        });

        assert.strictEqual(env.AspireCliPath, undefined);
        assert.strictEqual(env.OTHER_ENV, '1');
    });

    test('keeps explicit MSBuild AspireCliPath when merging environment variables', () => {
        const env = mergeEnvs({
            AspireCliPath: '/stale/from/old/session/aspire',
            OTHER_ENV: '1',
        }, [
            { name: 'AspireCliPath', value: '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire' },
        ]);

        assert.strictEqual(env.AspireCliPath, '/repo/artifacts/bin/Aspire.Cli/Debug/net10.0/aspire');
        assert.strictEqual(env.OTHER_ENV, '1');
    });

    test('sets MSBuild AspireCliPath when extension variables are disabled', async () => {
        const createEnvironmentStub = sinon.stub().returns({});
        const terminalProvider = {
            createEnvironment: createEnvironmentStub
        } as unknown as AspireTerminalProvider;
        let stdout = '';
        let stderr = '';

        const exitCode = await new Promise<number | null>((resolve, reject) => {
            const child = spawnCliProcess(terminalProvider, process.execPath, ['-e', 'process.stdout.write(process.env.AspireCliPath ?? "")'], {
                noExtensionVariables: true,
                env: [{ name: 'ELECTRON_RUN_AS_NODE', value: '1' }],
                stdoutCallback: data => { stdout += data; },
                stderrCallback: data => { stderr += data; },
                exitCallback: resolve,
                errorCallback: reject,
            });

            child.on('error', reject);
        });

        assert.strictEqual(exitCode, 0, stderr);
        assert.strictEqual(stdout, process.execPath);
        assert.strictEqual(createEnvironmentStub.calledOnceWith(undefined, undefined, true, process.execPath), true);
    });

    test('keeps computed MSBuild AspireCliPath when caller env contains AspireCliPath', async () => {
        const createEnvironmentStub = sinon.stub().returns({});
        const terminalProvider = {
            createEnvironment: createEnvironmentStub
        } as unknown as AspireTerminalProvider;
        let stdout = '';
        let stderr = '';

        const exitCode = await new Promise<number | null>((resolve, reject) => {
            const child = spawnCliProcess(terminalProvider, process.execPath, ['-e', 'process.stdout.write(process.env.AspireCliPath ?? "")'], {
                noExtensionVariables: true,
                env: [
                    { name: 'AspireCliPath', value: '/stale/from/launch-config/aspire' },
                    { name: 'ELECTRON_RUN_AS_NODE', value: '1' }
                ],
                stdoutCallback: data => { stdout += data; },
                stderrCallback: data => { stderr += data; },
                exitCallback: resolve,
                errorCallback: reject,
            });

            child.on('error', reject);
        });

        assert.strictEqual(exitCode, 0, stderr);
        assert.strictEqual(stdout, process.execPath);
    });
});
