import * as assert from 'assert';
import { formatProcessSnapshot, parsePosixProcessSnapshot } from '../testing/processDiagnostics';

suite('E2E process diagnostics', () => {
    test('reports the parent, status, and command line for a surviving POSIX process', () => {
        const snapshot = parsePosixProcessSnapshot(
            ' 4711     1 S+   node /workspace/AspireE2E.NodeApp/app.js\n',
            4711);

        assert.deepStrictEqual(snapshot, {
            pid: 4711,
            parentPid: 1,
            status: 'S+',
            commandLine: 'node /workspace/AspireE2E.NodeApp/app.js',
        });
        assert.strictEqual(
            formatProcessSnapshot(snapshot, 4711),
            'pid=4711, parentPid=1, status=S+, command=node /workspace/AspireE2E.NodeApp/app.js');
    });

    test('rejects a mismatched pid instead of misidentifying the process', () => {
        // A stale `ps` snapshot for a different pid must not be attributed to the caller's pid,
        // otherwise the timeout error would name the wrong process and lead the reader elsewhere.
        const snapshot = parsePosixProcessSnapshot(' 9999     1 S+   node app.js\n', 4711);

        assert.strictEqual(snapshot, undefined);
    });

    test('preserves a command line that contains internal whitespace', () => {
        const snapshot = parsePosixProcessSnapshot(
            ' 4711     1 S+   node /workspace/app.js --port 5173\n',
            4711);

        assert.strictEqual(snapshot?.commandLine, 'node /workspace/app.js --port 5173');
    });

    test('reports the pid even when the process is already gone', () => {
        assert.strictEqual(
            formatProcessSnapshot(undefined, 4711),
            'pid=4711, process details unavailable');
    });
});

