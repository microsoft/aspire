// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import * as sinon from 'sinon';
import { terminateCliProcess } from '../utils/process/cliProcess';

suite('CLI process termination', () => {
    teardown(() => {
        sinon.restore();
    });

    test('does not forcefully terminate the Windows process tree for an already-exited leader', () => {
        sinon.stub(process, 'platform').value('win32');
        const childProcess = createFakeCliProcess(4242, 0);
        const taskkillUnref = sinon.stub();
        const spawnStub = sinon.stub(nodeChildProcess, 'spawn').callsFake((command: string, args?: readonly string[], options?: nodeChildProcess.SpawnOptions) => {
            return Object.assign(new EventEmitter(), {
                command,
                args: [...(args ?? [])],
                options,
                unref: taskkillUnref,
            }) as unknown as nodeChildProcess.ChildProcess;
        });

        terminateCliProcess(childProcess, 'Aspire CLI', { force: true });

        // After Node has observed exit, taskkill would resolve PID 4242 against the current process
        // table rather than a durable handle to the former CLI tree.
        sinon.assert.notCalled(spawnStub);
        sinon.assert.notCalled(taskkillUnref);
        sinon.assert.notCalled(childProcess.kill);
    });

    test('forcefully terminates a live process when graceful signaling fails', async () => {
        sinon.stub(process, 'platform').value('linux');
        const clock = sinon.useFakeTimers();
        const childProcess = createFakeCliProcess(4242, null);
        childProcess.kill.onFirstCall().returns(false);
        childProcess.kill.onSecondCall().returns(true);

        const termination = terminateCliProcess(childProcess, 'Aspire CLI');
        await clock.tickAsync(5000);

        assert.deepStrictEqual(childProcess.kill.args, [
            [undefined],
            ['SIGKILL'],
        ]);
        (childProcess as unknown as { signalCode: NodeJS.Signals | null }).signalCode = 'SIGKILL';
        childProcess.emit('close', null);
        await termination;
    });

    test('stops tracking immediately when a PID-less child cannot be signaled', async () => {
        sinon.stub(process, 'platform').value('linux');
        const clock = sinon.useFakeTimers();
        const childProcess = createFakeCliProcess(undefined, null);
        childProcess.kill.returns(false);
        let settled = false;

        void terminateCliProcess(childProcess, 'Aspire CLI').then(() => { settled = true; });
        await clock.tickAsync(0);

        assert.strictEqual(settled, true);
        sinon.assert.calledOnceWithExactly(childProcess.kill, undefined);
    });

    test('forcefully terminates a live process when graceful signaling throws', async () => {
        sinon.stub(process, 'platform').value('linux');
        const clock = sinon.useFakeTimers();
        const childProcess = createFakeCliProcess(4242, null);
        childProcess.kill.onFirstCall().throws(new Error('signal failed'));
        childProcess.kill.onSecondCall().returns(true);

        const termination = terminateCliProcess(childProcess, 'Aspire CLI');
        await clock.tickAsync(5000);

        assert.deepStrictEqual(childProcess.kill.args, [
            [undefined],
            ['SIGKILL'],
        ]);
        (childProcess as unknown as { signalCode: NodeJS.Signals | null }).signalCode = 'SIGKILL';
        childProcess.emit('close', null);
        await termination;
    });

    test('stops tracking immediately when signaling a PID-less child throws', async () => {
        sinon.stub(process, 'platform').value('linux');
        const clock = sinon.useFakeTimers();
        const childProcess = createFakeCliProcess(undefined, null);
        childProcess.kill.throws(new Error('signal failed'));
        let settled = false;

        void terminateCliProcess(childProcess, 'Aspire CLI').then(() => { settled = true; });
        await clock.tickAsync(0);

        assert.strictEqual(settled, true);
        sinon.assert.calledOnceWithExactly(childProcess.kill, undefined);
    });
});

function createFakeCliProcess(pid: number | undefined, exitCode: number | null): ChildProcessWithoutNullStreams & { kill: sinon.SinonStub } {
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
    }) as unknown as ChildProcessWithoutNullStreams & { kill: sinon.SinonStub };
}
