import * as assert from 'assert';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import { EventEmitter } from 'events';
import { AppHostCliRunner, isDescribeUnsupportedOutput } from '../data/appHostCliRunner';
import { AspireTerminalProvider } from '../utils/AspireTerminalProvider';
import * as cliModule from '../utils/process/cliProcess';

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

        test('does not track a one-shot process that completes before the spawn returns', async () => {
            const cliProcess = new TestChildProcess();
            spawnStub.callsFake((_provider: unknown, _cliPath: string, _args: string[], options: cliModule.SpawnProcessOptions) => {
                // A spawn that fails or is faked reports completion synchronously, so the exit
                // callback runs before the caller ever sees the process handle.
                options.stdoutCallback?.('{"resources":[]}');
                cliProcess.exitCode = 0;
                options.exitCallback?.(0);
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

        test('tracks a one-shot process that is still running so it can be stopped', async () => {
            const cliProcess = new TestChildProcess();
            let exitCallback: ((code: number | null) => void) | undefined;
            spawnStub.callsFake((_provider: unknown, _cliPath: string, _args: string[], options: cliModule.SpawnProcessOptions) => {
                exitCallback = options.exitCallback;
                return cliProcess;
            });

            const runner = new AppHostCliRunner(terminalProvider);
            try {
                const pending = runner.runCliCommand('describe', ['describe', '--format', 'json']);
                await waitForCondition(() => exitCallback !== undefined, 'expected the describe command to spawn');

                runner.stopOneShotProcesses();
                assert.strictEqual(terminateStub.callCount, 1, 'a running one-shot process must be terminated');
                assert.strictEqual(terminateStub.firstCall.args[0], cliProcess);

                cliProcess.exitCode = 1;
                exitCallback!(1);
                await assert.rejects(pending);
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
