import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { EventEmitter } from 'events';
import { AppHostCliRunner, isDescribeUnsupportedOutput, parseCliJsonOutput } from '../data/appHostCliRunner';
import { AspireCliFailedError } from '../data/appHostCliContracts';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import { workspaceFolderCliPathTarget } from '../utils/cliPathVariables';
import * as cliModule from '../utils/process/cliProcess';
import { onDidResolveCliForOperation } from '../utils/cliOperationResolution';

class TestChildProcess extends EventEmitter {
    killed = false;
    exitCode: number | null = null;
    signalCode: NodeJS.Signals | null = null;

    kill(): boolean {
        this.killed = true;
        return true;
    }
}

suite('data/appHostCliRunner tests', () => {

    suite('parseCliJsonOutput', () => {
        test('parses multiline JSON after startup diagnostics', () => {
            const output = `Starting AppHost...\n[
  {
    "name": "deploy"
  }
]`;

            assert.deepStrictEqual(parseCliJsonOutput(output), [{ name: 'deploy' }]);
        });
    });

    suite('AppHostCliRunner one-shot process tracking', () => {
        let subscriptions: vscode.Disposable[];
        let terminalProvider: AspireTerminalProvider;
        let getCliPathStub: sinon.SinonStub;
        let spawnStub: sinon.SinonStub;
        let terminateStub: sinon.SinonStub;

        setup(() => {
            subscriptions = [];
            terminalProvider = new AspireTerminalProvider(subscriptions);
            getCliPathStub = sinon.stub(terminalProvider, 'getAspireCliExecutablePath').resolves('aspire');
            spawnStub = sinon.stub(cliModule, 'spawnCliProcess');
            terminateStub = sinon.stub(cliModule, 'terminateCliProcess').resolves();
        });

        teardown(() => {
            terminateStub.restore();
            spawnStub.restore();
            getCliPathStub.restore();
            subscriptions.forEach(subscription => subscription.dispose());
        });

        test('completes a one-shot command when the direct process exits before close', async () => {
            const cliProcess = new TestChildProcess();
            let processExitCallback: ((code: number | null) => void) | undefined;
            spawnStub.callsFake((_provider: unknown, _cliPath: string, _args: string[], options: cliModule.SpawnProcessOptions) => {
                processExitCallback = options.processExitCallback;
                options.stdoutCallback?.('{"resources":[{"name":"api"}]}');
                options.stderrCallback?.('AppHost diagnostic');
                return cliProcess;
            });

            const runner = new AppHostCliRunner(terminalProvider);
            try {
                const pending = runner.runCliCommand('describe', ['describe', '--format', 'json']);
                await waitForCondition(() => processExitCallback !== undefined, 'expected the direct process exit callback');

                cliProcess.exitCode = 0;
                processExitCallback!(0);

                const result = await pending;
                assert.deepStrictEqual(result, {
                    stdout: '{"resources":[{"name":"api"}]}',
                    stderr: 'AppHost diagnostic',
                });

                runner.stopOneShotProcesses();
                assert.strictEqual(terminateStub.callCount, 0, 'the directly-exited process must not be retained');
            } finally {
                runner.dispose();
            }

            assert.strictEqual(terminateStub.callCount, 0, 'disposing the runner must not terminate the directly-exited process');
            assert.strictEqual(cliProcess.killed, false);
        });

        test('does not track a one-shot process that completes before the spawn returns', async () => {
            const cliProcess = new TestChildProcess();
            spawnStub.callsFake((_provider: unknown, _cliPath: string, _args: string[], options: cliModule.SpawnProcessOptions) => {
                // A spawn that fails or is faked reports completion synchronously, so the exit
                // callback runs before the caller ever sees the process handle.
                options.stdoutCallback?.('{"resources":[]}');
                cliProcess.exitCode = 0;
                options.processExitCallback?.(0);
                return cliProcess;
            });

            const runner = new AppHostCliRunner(terminalProvider);
            try {
                const result = await runner.runCliCommand('describe', ['describe', '--format', 'json']);
                assert.strictEqual(result.stdout, '{"resources":[]}');
                assert.strictEqual(terminateStub.callCount, 0, 'a completed one-shot command should not terminate anything');

                runner.stopOneShotProcesses();
                assert.strictEqual(terminateStub.callCount, 0, 'the already-exited process must not be retained as a running one-shot process');
            } finally {
                runner.dispose();
            }

            assert.strictEqual(terminateStub.callCount, 0, 'disposing the runner must not terminate an already-completed process');
            assert.strictEqual(cliProcess.killed, false);
        });

        test('resolves a one-shot command with its supplied workspace target', async () => {
            const folder: vscode.WorkspaceFolder = {
                uri: vscode.Uri.file('/workspace'),
                name: 'workspace',
                index: 0,
            };
            const target = workspaceFolderCliPathTarget(folder);
            const cliProcess = new TestChildProcess();
            spawnStub.callsFake((_provider: unknown, _cliPath: string, _args: string[], options: cliModule.SpawnProcessOptions) => {
                cliProcess.exitCode = 0;
                options.processExitCallback?.(0);
                return cliProcess;
            });

            const runner = new AppHostCliRunner(terminalProvider);
            try {
                const options = { timeoutMs: undefined, target };
                await runner.runCliCommand('describe', ['describe'], options);

                assert.strictEqual(getCliPathStub.calledOnceWithExactly(target), true);
            } finally {
                runner.dispose();
            }
        });

        test('uses a supplied concrete CLI path without resolving it again', async () => {
            const cliProcess = new TestChildProcess();
            spawnStub.callsFake((_provider: unknown, _cliPath: string, _args: string[], options: cliModule.SpawnProcessOptions) => {
                cliProcess.exitCode = 0;
                options.processExitCallback?.(0);
                return cliProcess;
            });

            const runner = new AppHostCliRunner(terminalProvider);
            const resolutions: string[] = [];
            const subscription = onDidResolveCliForOperation(resolution => resolutions.push(resolution.cliPath));
            try {
                await runner.runCliCommand('list pipeline steps', ['do', '--list-steps'], {
                    cliPath: '/repo/tools/aspire',
                });

                assert.strictEqual(getCliPathStub.called, false);
                assert.strictEqual(spawnStub.firstCall.args[1], '/repo/tools/aspire');
                assert.deepStrictEqual(resolutions, ['/repo/tools/aspire']);
            }
            finally {
                subscription.dispose();
                runner.dispose();
            }
        });

        test('isolates nologo fallback by concrete CLI path', async () => {
            const folderA: vscode.WorkspaceFolder = {
                uri: vscode.Uri.file('/workspace/a'),
                name: 'a',
                index: 0,
            };
            const folderB: vscode.WorkspaceFolder = {
                uri: vscode.Uri.file('/workspace/b'),
                name: 'b',
                index: 1,
            };
            const targetA = workspaceFolderCliPathTarget(folderA);
            const targetB = workspaceFolderCliPathTarget(folderB);
            getCliPathStub.callsFake(async target => target?.kind === 'workspaceFolder'
                ? `/cli/${target.workspaceFolder.name}/aspire`
                : '/cli/global/aspire');
            spawnStub.callsFake(() => new TestChildProcess());

            const completeSpawn = (index: number, code: number, stderr = '') => {
                const call = spawnStub.getCall(index);
                const process = call.returnValue as TestChildProcess;
                if (stderr) {
                    call.args[3].stderrCallback(stderr);
                }
                process.exitCode = code;
                call.args[3].processExitCallback(code);
            };

            const runner = new AppHostCliRunner(terminalProvider);
            try {
                const firstA = runner.runCliCommand('describe A', runner.withNoLogo(['describe']), { target: targetA });
                await waitForCondition(() => spawnStub.callCount === 1, 'expected first CLI A invocation');
                completeSpawn(0, 1, "Unrecognized command or argument '--nologo'.");
                await waitForCondition(() => spawnStub.callCount === 2, 'expected CLI A fallback invocation');
                completeSpawn(1, 0);
                await firstA;

                const firstB = runner.runCliCommand('describe B', runner.withNoLogo(['describe']), { target: targetB });
                await waitForCondition(() => spawnStub.callCount === 3, 'expected first CLI B invocation');
                completeSpawn(2, 0);
                await firstB;

                const laterA = runner.runCliCommand('describe A', runner.withNoLogo(['describe']), { target: targetA });
                await waitForCondition(() => spawnStub.callCount === 4, 'expected later CLI A invocation');
                completeSpawn(3, 0);
                await laterA;

                assert.deepStrictEqual(spawnStub.getCall(0).args.slice(1, 3), ['/cli/a/aspire', ['describe', '--nologo']]);
                assert.deepStrictEqual(spawnStub.getCall(1).args.slice(1, 3), ['/cli/a/aspire', ['describe']]);
                assert.deepStrictEqual(spawnStub.getCall(2).args.slice(1, 3), ['/cli/b/aspire', ['describe', '--nologo']]);
                assert.deepStrictEqual(spawnStub.getCall(3).args.slice(1, 3), ['/cli/a/aspire', ['describe']]);
            } finally {
                runner.dispose();
            }
        });

        test('tracks a one-shot process that is still running so it can be stopped', async () => {
            const cliProcess = new TestChildProcess();
            let processExitCallback: ((code: number | null) => void) | undefined;
            spawnStub.callsFake((_provider: unknown, _cliPath: string, _args: string[], options: cliModule.SpawnProcessOptions) => {
                processExitCallback = options.processExitCallback;
                return cliProcess;
            });

            const runner = new AppHostCliRunner(terminalProvider);
            try {
                const pending = runner.runCliCommand('describe', ['describe', '--format', 'json']);
                await waitForCondition(() => processExitCallback !== undefined, 'expected the describe command to spawn');

                runner.stopOneShotProcesses();
                assert.strictEqual(terminateStub.callCount, 1, 'a running one-shot process must be terminated');
                assert.strictEqual(terminateStub.firstCall.args[0], cliProcess);

                cliProcess.exitCode = 1;
                processExitCallback!(1);
                await assert.rejects(pending, (error: unknown) => {
                    assert.ok(error instanceof AspireCliFailedError);
                    assert.strictEqual(error.exitCode, 1);
                    return true;
                });
            } finally {
                runner.dispose();
            }
        });
    });

    suite('isDescribeUnsupportedOutput', () => {
        test('returns true when an old CLI rejects the describe command', () => {
            assert.strictEqual(isDescribeUnsupportedOutput([], "Unrecognized command or argument 'describe'."), true);
        });

        test('returns true when an old CLI rejects an option the extension passes', () => {
            assert.strictEqual(isDescribeUnsupportedOutput([], "Unrecognized command or argument '--follow'."), true);
        });

        test('returns true for a localized rejection that keeps the token verbatim', () => {
            assert.strictEqual(isDescribeUnsupportedOutput([], "No se encuentra el recurso '--apphost'."), true);
        });

        test('returns true when the rejected token is echoed unquoted', () => {
            assert.strictEqual(isDescribeUnsupportedOutput([], 'Unrecognized command or argument --follow'), true);
        });

        test('returns true for top-level CLI help output', () => {
            assert.strictEqual(isDescribeUnsupportedOutput(['Description:', 'Usage:', 'aspire [command] [options]', 'Commands:'], ''), true);
        });

        test('returns true for localized top-level CLI help output', () => {
            assert.strictEqual(isDescribeUnsupportedOutput(['Uso:', 'aspire <comando> [opciones]'], ''), true);
        });

        test('returns false when a current CLI rejects a user-supplied option', () => {
            // The AppHost (not the CLI) rejected `--publisher`, so the real error has to survive
            // instead of being reported as an outdated CLI that cannot describe.
            assert.strictEqual(isDescribeUnsupportedOutput([], "Unrecognized command or argument '--publisher'."), false);
        });

        test('returns false when a current CLI reports an unrecognized user option', () => {
            assert.strictEqual(isDescribeUnsupportedOutput([], "Unrecognized option '--publisher'."), false);
        });

        test('returns false when a user option is rejected for the describe command', () => {
            assert.strictEqual(isDescribeUnsupportedOutput([], "Option '--publisher' is not valid for command 'describe'."), false);
        });

        test('returns false for an unrelated AppHost failure', () => {
            assert.strictEqual(isDescribeUnsupportedOutput(['Unhandled exception. System.InvalidOperationException: AppHost failed to start.'], ''), false);
        });

        test('returns false for empty output', () => {
            assert.strictEqual(isDescribeUnsupportedOutput([], ''), false);
        });
    });
});

async function waitForCondition(condition: () => boolean, message: string, timeoutMs = 2000): Promise<void> {
    const deadline = Date.now() + timeoutMs;
    while (!condition()) {
        if (Date.now() > deadline) {
            assert.fail(message);
        }

        await new Promise(resolve => setTimeout(resolve, 5));
    }
}
