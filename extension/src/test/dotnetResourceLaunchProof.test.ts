import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as sinon from 'sinon';
import * as vscode from 'vscode';
import * as debuggerExtensions from '../debugger/debuggerExtensions';
import { externalBuildProjectDebuggerExtension } from '../debugger/languages/dotnet';
import type { AspireResourceExtendedDebugConfiguration } from '../dcp/types';
import { proveDotnetResourceLaunch, type DotnetResourceLaunchProofOptions } from '../testing/dotnetResourceLaunchProof';
import { removeDirectorySafely } from './testHelpers';

suite('Dotnet resource launch proof', () => {
    let sandbox: sinon.SinonSandbox;
    let directory: string;
    let options: DotnetResourceLaunchProofOptions;
    let createConfiguration: sinon.SinonStub;
    let startDebugging: sinon.SinonStub;
    let stopDebugging: sinon.SinonStub;
    let session: vscode.DebugSession;
    let finishProcess: () => void;
    let startListener: (session: vscode.DebugSession) => unknown;
    let terminateListener: (session: vscode.DebugSession) => unknown;
    let trackerFactory: vscode.DebugAdapterTrackerFactory;
    let tracker: vscode.DebugAdapterTracker;
    let writeReadiness: boolean;
    let stackSourcePath: string;
    let adapterCloseError: boolean;
    let exitCode: number;
    let failedRequestCommand: string | undefined;

    setup(() => {
        sandbox = sinon.createSandbox();
        const fixtureRoot = path.resolve(__dirname, '..', '..', '.test-artifacts');
        fs.mkdirSync(fixtureRoot, { recursive: true });
        directory = fs.mkdtempSync(path.join(fixtureRoot, 'dotnet-resource-proof-'));
        options = {
            launchConfig: {
                type: 'project-with-external-build.v1',
                project_path: path.join(directory, 'Worker.fsproj'),
                suppress_build: true,
                build_configuration: 'Release',
            },
            sourcePath: path.join(directory, 'Program.fs'),
            breakpointLine: 3,
            readyFilePath: path.join(directory, 'ready.txt'),
            debug: false,
            timeoutMs: 1000,
        };
        writeReadiness = true;
        stackSourcePath = options.sourcePath;
        adapterCloseError = false;
        exitCode = 0;
        failedRequestCommand = undefined;
        sandbox.stub(vscode.extensions, 'getExtension').returns({
            activate: sandbox.stub().resolves(undefined),
        } as unknown as vscode.Extension<unknown>);
        sandbox.stub(debuggerExtensions, 'getResourceDebuggerExtensions').returns([externalBuildProjectDebuggerExtension]);
        createConfiguration = sandbox.stub(debuggerExtensions, 'createDebugSessionConfiguration').callsFake(async (_parent, _launch, _args, _env, launchOptions) => ({
            type: 'coreclr',
            request: 'launch',
            name: 'Resource proof',
            program: 'dotnet',
            runId: launchOptions.runId,
            debugSessionId: launchOptions.debugSessionId,
            isApphost: launchOptions.isApphost,
            noDebug: !launchOptions.debug,
        }));
        sandbox.stub(vscode.workspace, 'getWorkspaceFolder').returns(undefined);
        sandbox.stub(vscode.debug, 'onDidStartDebugSession').callsFake(listener => {
            startListener = listener;
            return { dispose() { } };
        });
        sandbox.stub(vscode.debug, 'onDidTerminateDebugSession').callsFake(listener => {
            terminateListener = listener;
            return { dispose() { } };
        });
        sandbox.stub(vscode.debug, 'registerDebugAdapterTrackerFactory').callsFake((_type, factory) => {
            trackerFactory = factory;
            return { dispose() { } };
        });
        sandbox.stub(vscode.debug, 'addBreakpoints');
        sandbox.stub(vscode.debug, 'removeBreakpoints');
        finishProcess = () => {
            if (writeReadiness) {
                tracker.onDidSendMessage?.({ type: 'event', event: 'output', body: { output: 'ready\n' } });
                fs.writeFileSync(options.readyFilePath, 'Release|custom');
                if (failedRequestCommand) {
                    tracker.onDidSendMessage?.({ type: 'response', command: failedRequestCommand, success: false, message: 'request failed' });
                }
                tracker.onDidSendMessage?.({ type: 'event', event: 'exited', body: { exitCode } });
                if (adapterCloseError) {
                    tracker.onError?.(new Error('read error'));
                }
                terminateListener(session);
            }
        };
        startDebugging = sandbox.stub(vscode.debug, 'startDebugging').callsFake(async (_folder, configuration) => {
            session = {
                id: 'resource',
                type: 'coreclr',
                name: 'Resource proof',
                configuration,
                customRequest: sandbox.stub().callsFake(async (command: string) => {
                    if (command === 'stackTrace') {
                        return { stackFrames: [{ source: { path: stackSourcePath }, line: options.breakpointLine + 1 }] };
                    }
                    assert.strictEqual(command, 'continue');
                    finishProcess();
                }),
            } as unknown as vscode.DebugSession;
            tracker = await trackerFactory.createDebugAdapterTracker(session) as vscode.DebugAdapterTracker;
            startListener(session);
            tracker.onWillReceiveMessage?.({ type: 'request', command: 'launch', arguments: configuration });
            if (options.debug) {
                tracker.onDidSendMessage?.({ type: 'response', command: 'setBreakpoints', success: true, body: { breakpoints: [{ verified: true }] } });
                tracker.onDidSendMessage?.({ type: 'event', event: 'stopped', body: { reason: 'breakpoint', threadId: 1 } });
            } else {
                finishProcess();
            }
            return true;
        });
        stopDebugging = sandbox.stub(vscode.debug, 'stopDebugging').callsFake(async stopped => {
            assert.strictEqual(stopped, session);
            terminateListener(session);
        });
    });

    teardown(() => {
        sandbox.restore();
        removeDirectorySafely(directory);
    });

    for (const debug of [false, true]) {
        test(`requires a real resource ${debug ? 'breakpoint and continuation' : 'process'} before accepting readiness`, async () => {
            options.debug = debug;

            const proof = await proveDotnetResourceLaunch(options) as {
                proof: string;
                configuration: AspireResourceExtendedDebugConfiguration;
                breakpointHit?: { reason: string; sourcePath: string; line: number };
                exitCode: number;
                readiness: string;
            };

            assert.strictEqual(proof.proof, debug ? 'dotnet-resource-breakpoint-hit' : 'dotnet-resource-run-completed');
            assert.strictEqual(proof.configuration.noDebug, !debug);
            assert.strictEqual(proof.configuration.isApphost, false);
            assert.strictEqual(proof.readiness, 'Release|custom');
            assert.strictEqual(proof.exitCode, 0);
            assert.strictEqual(createConfiguration.firstCall.args[4].isApphost, false);
            assert.strictEqual(createConfiguration.firstCall.args[1].suppress_build, true);
            assert.strictEqual(createConfiguration.firstCall.args[1].build_configuration, 'Release');
            if (debug) {
                assert.deepStrictEqual(proof.breakpointHit, { reason: 'breakpoint', sourcePath: options.sourcePath, line: 4 });
            } else {
                assert.strictEqual(proof.breakpointHit, undefined);
            }
            sinon.assert.calledOnce(startDebugging);
            sinon.assert.notCalled(stopDebugging);
        });
    }

    test('rejects a NoDebug fallback when Debug was requested', async () => {
        options.debug = true;
        createConfiguration.resolves({ type: 'coreclr', noDebug: true, isApphost: false });

        await assert.rejects(proveDotnetResourceLaunch(options), /did not preserve the requested Run\/Debug mode/);

        sinon.assert.notCalled(startDebugging);
    });

    test('accepts adapter pipe teardown only after a successful resource exit', async () => {
        adapterCloseError = true;

        const proof = await proveDotnetResourceLaunch(options) as { exitCode: number };

        assert.strictEqual(proof.exitCode, 0);
    });

    test('ignores a VS Code inspection request racing with source-breakpoint continuation', async () => {
        options.debug = true;
        failedRequestCommand = 'scopes';

        const proof = await proveDotnetResourceLaunch(options) as { proof: string; exitCode: number };

        assert.strictEqual(proof.proof, 'dotnet-resource-breakpoint-hit');
        assert.strictEqual(proof.exitCode, 0);
    });

    for (const command of ['launch', 'setBreakpoints', 'configurationDone']) {
        test(`rejects a failed ${command} request even if the resource produced readiness`, async () => {
            failedRequestCommand = command;

            await assert.rejects(proveDotnetResourceLaunch(options), new RegExp(`${command}: request failed`));
        });
    }

    test('rejects a failed process even when it wrote readiness before pipe teardown', async () => {
        adapterCloseError = true;
        exitCode = 9;

        await assert.rejects(proveDotnetResourceLaunch(options), /resource exited with code 9/);
    });

    test('rejects stale readiness rather than accepting an earlier process run', async () => {
        fs.writeFileSync(options.readyFilePath, 'stale');

        await assert.rejects(proveDotnetResourceLaunch(options), /Readiness file already exists/);

        sinon.assert.notCalled(startDebugging);
    });

    test('stops only its resource session when readiness times out', async () => {
        writeReadiness = false;

        await assert.rejects(proveDotnetResourceLaunch(options), /waiting for resource readiness output/);

        sinon.assert.calledOnceWithExactly(stopDebugging, session);
    });

    test('rejects a breakpoint stop in a different source file', async () => {
        options.debug = true;
        stackSourcePath = path.join(directory, 'Unrelated.fs');

        await assert.rejects(proveDotnetResourceLaunch(options), /waiting for a resource source breakpoint/);

        sinon.assert.calledOnceWithExactly(stopDebugging, session);
    });

    test('fails explicitly without an installed .NET resource debugger', async () => {
        sandbox.restore();
        sandbox.stub(debuggerExtensions, 'getResourceDebuggerExtensions').returns([]);

        await assert.rejects(proveDotnetResourceLaunch(options), /Install the C# debugger VSIX/);
    });
});
