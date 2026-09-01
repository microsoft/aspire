import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { FileSystemOutdatedCliSuppressionStore } from '../utils/outdatedCliSuppressionStore';

suite('outdatedCliSuppressionStore', () => {
    test('preserves concurrent writes from separate stores', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-cli-suppressions-'));
        const first = new FileSystemOutdatedCliSuppressionStore(directory);
        const second = new FileSystemOutdatedCliSuppressionStore(directory);

        try {
            await Promise.all([
                first.add('/cli/a\u000013.5.0'),
                second.add('/cli/b\u000013.5.0'),
            ]);

            assert.deepStrictEqual(
                (await first.readAll()).sort(),
                ['/cli/a\u000013.5.0', '/cli/b\u000013.5.0']);
        }
        finally {
            fs.rmSync(directory, { recursive: true, force: true });
        }
    });
});
