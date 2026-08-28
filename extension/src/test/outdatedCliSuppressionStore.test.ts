import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { FileSystemOutdatedCliSuppressionStore } from '../utils/outdatedCliSuppressionStore';

suite('outdatedCliSuppressionStore', () => {
    let directoryPath: string;

    setup(() => {
        directoryPath = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-cli-suppressions-'));
    });

    teardown(() => {
        fs.rmSync(directoryPath, { recursive: true, force: true });
    });

    test('shares immutable generations across store instances', async () => {
        const first = new FileSystemOutdatedCliSuppressionStore(directoryPath);
        const second = new FileSystemOutdatedCliSuppressionStore(directoryPath);

        const [firstSuppression, secondSuppression] = await Promise.all([
            first.write('/cli/a\u000013.5.0', 1),
            second.write('/cli/b\u000013.5.0', 2),
        ]);

        assert.notStrictEqual(firstSuppression.storageKey, secondSuppression.storageKey);
        assert.deepStrictEqual(
            (await first.readAll())
                .map(({ notificationKey, suppressedAt }) => ({ notificationKey, suppressedAt }))
                .sort((left, right) => left.suppressedAt - right.suppressedAt),
            [
                { notificationKey: '/cli/a\u000013.5.0', suppressedAt: 1 },
                { notificationKey: '/cli/b\u000013.5.0', suppressedAt: 2 },
            ]);

        await first.delete(firstSuppression.storageKey);

        assert.deepStrictEqual(
            (await second.readAll()).map(({ notificationKey }) => notificationKey),
            ['/cli/b\u000013.5.0']);
    });

    test('creates missing storage and ignores incomplete or malformed entries', async () => {
        const missingDirectory = path.join(directoryPath, 'missing');
        const store = new FileSystemOutdatedCliSuppressionStore(missingDirectory);

        assert.deepStrictEqual(await store.readAll(), []);
        fs.writeFileSync(
            path.join(missingDirectory, '.outdated-cli-suppression-incomplete.json.tmp'),
            '{"notificationKey":"incomplete"}');
        fs.writeFileSync(
            path.join(missingDirectory, 'outdated-cli-suppression-malformed.json'),
            '{');
        fs.writeFileSync(
            path.join(missingDirectory, 'outdated-cli-suppression-oversized.json'),
            'x'.repeat(256 * 1024 + 1));
        const valid = await store.write('/cli/valid\u000013.5.0', 3);

        assert.deepStrictEqual(
            (await store.readAll()).map(({ notificationKey }) => notificationKey),
            ['/cli/valid\u000013.5.0']);
        await store.delete('outdated-cli-suppression-missing.json');
        await store.delete(valid.storageKey);
        assert.deepStrictEqual(await store.readAll(), []);
    });
});
