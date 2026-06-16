import * as assert from 'assert';
import * as path from 'path';
import * as sinon from 'sinon';
import { getCliSpawnCommand } from '../debugger/languages/cli';
import { getAspireCliPathForMSBuild } from '../utils/environment';

suite('spawnCliProcess tests', () => {
    test('runs Windows cmd wrappers through cmd.exe', () => {
        const platformStub = sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getCliSpawnCommand('C:\\Tools\\Aspire CLI\\aspire.cmd', ['config', 'info']);

            assert.strictEqual(result.command, process.env.ComSpec);
            assert.deepStrictEqual(result.args, ['/d', '/c', 'call', 'C:\\Tools\\Aspire CLI\\aspire.cmd', 'config', 'info']);
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

    test('does not set MSBuild AspireCliPath for explicit Windows batch wrappers', () => {
        assert.strictEqual(getAspireCliPathForMSBuild('C:\\Users\\me\\AppData\\Roaming\\npm\\aspire.cmd'), undefined);
        assert.strictEqual(getAspireCliPathForMSBuild('C:\\Users\\me\\AppData\\Roaming\\npm\\aspire.bat'), undefined);
    });

    test('resolves explicit CLI paths for MSBuild AspireCliPath', () => {
        const workingDirectory = path.join(path.sep, 'workspace');
        const relativeCliPath = path.join('artifacts', 'bin', 'Aspire.Cli', 'Debug', 'net10.0', 'aspire');
        const absoluteCliPath = path.join(path.sep, 'repo', relativeCliPath);

        assert.strictEqual(getAspireCliPathForMSBuild(absoluteCliPath, workingDirectory), absoluteCliPath);
        assert.strictEqual(getAspireCliPathForMSBuild(relativeCliPath, workingDirectory), path.resolve(workingDirectory, relativeCliPath));
    });
});
