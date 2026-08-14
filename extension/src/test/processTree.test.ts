import * as assert from 'assert';
import * as sinon from 'sinon';
// Imported with `require` rather than `import *` so the module object stays writable and `spawn` can be stubbed.
import nodeChildProcess = require('child_process');
import { EventEmitter } from 'events';
import { processGroupSpawnOptions, terminateProcessTree } from '../utils/processTree';

suite('Process Tree Tests', () => {
    teardown(() => sinon.restore());

    function fakeChild(pid: number | undefined): nodeChildProcess.ChildProcess {
        const killSignals: Array<NodeJS.Signals | undefined> = [];
        const child = Object.assign(new EventEmitter(), {
            pid,
            killSignals,
            kill: (signal?: NodeJS.Signals) => {
                killSignals.push(signal);
                return true;
            },
        });

        return child as unknown as nodeChildProcess.ChildProcess;
    }

    test('spawns as a process group leader everywhere except Windows', () => {
        sinon.stub(process, 'platform').value('win32');
        assert.deepStrictEqual(processGroupSpawnOptions(), { detached: false });

        sinon.stub(process, 'platform').value('linux');
        assert.deepStrictEqual(processGroupSpawnOptions(), { detached: true });
    });

    test('Windows termination walks the tree with taskkill', () => {
        sinon.stub(process, 'platform').value('win32');
        const calls: Array<{ command: string; args: string[] }> = [];
        sinon.stub(nodeChildProcess, 'spawn').callsFake((command: string, args?: readonly string[]) => {
            calls.push({ command, args: [...(args ?? [])] });
            return Object.assign(new EventEmitter(), { unref: () => { } }) as nodeChildProcess.ChildProcess;
        });

        assert.strictEqual(terminateProcessTree(fakeChild(4242), false), true);
        assert.strictEqual(terminateProcessTree(fakeChild(4242), true), true);

        assert.deepStrictEqual(calls, [
            { command: 'taskkill.exe', args: ['/pid', '4242', '/t'] },
            { command: 'taskkill.exe', args: ['/pid', '4242', '/t', '/f'] },
        ]);
    });

    test('POSIX termination signals the whole process group', () => {
        sinon.stub(process, 'platform').value('linux');
        const killed: Array<{ pid: number; signal: string | number | undefined }> = [];
        sinon.stub(process, 'kill').callsFake((pid: number, signal?: string | number) => {
            killed.push({ pid, signal });
            return true;
        });

        assert.strictEqual(terminateProcessTree(fakeChild(4242), false), true);
        assert.strictEqual(terminateProcessTree(fakeChild(4242), true), true);

        // A negative pid is the process group, which is what takes rustc and the linker down with cargo.
        assert.deepStrictEqual(killed, [
            { pid: -4242, signal: 'SIGTERM' },
            { pid: -4242, signal: 'SIGKILL' },
        ]);
    });

    test('POSIX termination falls back to the direct child when there is no process group', () => {
        sinon.stub(process, 'platform').value('linux');
        sinon.stub(process, 'kill').throws(new Error('ESRCH'));

        const child = fakeChild(4242);

        assert.strictEqual(terminateProcessTree(child, true), true);
        assert.deepStrictEqual((child as unknown as { killSignals: string[] }).killSignals, ['SIGKILL']);
    });

    test('reports a signal delivered to the direct child when there is no pid to walk', () => {
        sinon.stub(process, 'platform').value('linux');
        const child = fakeChild(undefined);

        assert.strictEqual(terminateProcessTree(child, false), true);
        assert.deepStrictEqual((child as unknown as { killSignals: unknown[] }).killSignals, [undefined]);
    });
});
