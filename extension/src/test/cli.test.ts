// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

import * as assert from 'assert';
import nodeChildProcess = require('child_process');
import type { ChildProcessWithoutNullStreams } from 'child_process';
import { EventEmitter } from 'node:events';
import { PassThrough } from 'node:stream';
import * as sinon from 'sinon';
import { terminateCliProcess } from '../debugger/languages/cli';

suite('CLI process termination', () => {
    teardown(() => {
        sinon.restore();
    });

    test('forcefully terminates the Windows process tree for an already-exited leader', () => {
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

        sinon.assert.calledOnce(spawnStub);
        assert.strictEqual(spawnStub.firstCall.args[0], 'taskkill.exe');
        assert.deepStrictEqual(spawnStub.firstCall.args[1], ['/pid', '4242', '/t', '/f']);
        assert.deepStrictEqual(spawnStub.firstCall.args[2], {
            stdio: 'ignore',
            windowsHide: true,
        });
        sinon.assert.calledOnce(taskkillUnref);
        sinon.assert.notCalled(childProcess.kill);
    });
});

function createFakeCliProcess(pid: number, exitCode: number | null): ChildProcessWithoutNullStreams & { kill: sinon.SinonStub } {
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
