import * as assert from 'assert';
import { spawn } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { FileSystemOutdatedCliSuppressionStore } from '../utils/outdatedCliSuppressionStore';

suite('outdatedCliSuppressionStore', () => {
    let directory: string;

    setup(() => {
        directory = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-cli-suppressions-'));
    });

    teardown(() => {
        fs.rmSync(directory, { recursive: true, force: true });
    });

    test('preserves concurrent writes from separate stores', async () => {
        const first = new FileSystemOutdatedCliSuppressionStore(directory);
        const second = new FileSystemOutdatedCliSuppressionStore(directory);

        await Promise.all([
            first.add('/cli/a\u000013.5.0'),
            second.add('/cli/b\u000013.5.0'),
        ]);

        assert.deepStrictEqual(
            (await first.readAll()).sort(),
            ['/cli/a\u000013.5.0', '/cli/b\u000013.5.0']);
    });

    test('serializes a suppression written after the final notification check', async () => {
        const first = new FileSystemOutdatedCliSuppressionStore(directory);
        const second = new FileSystemOutdatedCliSuppressionStore(directory);
        const notificationKey = '/cli/aspire\u000013.5.0';

        const claim = await second.tryClaimNotification(notificationKey);
        assert.ok(claim);

        let suppressionCompleted = false;
        const suppression = first.add(notificationKey).then(() => suppressionCompleted = true);
        await new Promise(resolve => setTimeout(resolve, 50));
        assert.strictEqual(suppressionCompleted, false);

        await claim.release();
        await suppression;

        assert.strictEqual(suppressionCompleted, true);
        assert.strictEqual(await second.tryClaimNotification(notificationKey), undefined);
    });

    test('recovers a lock abandoned by another extension host', async () => {
        const exitedProcessId = await startAndWaitForProcess();
        const storageDirectory = path.join(directory, 'outdated-cli-suppressions');
        const ownerFileName = `.operation-lock-owner-${Date.now()}-${exitedProcessId}-0`;
        const ownerPath = path.join(storageDirectory, ownerFileName);
        fs.mkdirSync(storageDirectory, { recursive: true });
        fs.writeFileSync(ownerPath, ownerFileName);
        fs.linkSync(ownerPath, path.join(storageDirectory, '.operation-lock'));

        const first = new FileSystemOutdatedCliSuppressionStore(directory);
        const second = new FileSystemOutdatedCliSuppressionStore(directory);
        await Promise.all([
            first.add('/cli/a\u000013.5.0'),
            second.add('/cli/b\u000013.5.0'),
        ]);

        assert.deepStrictEqual(
            (await first.readAll()).sort(),
            ['/cli/a\u000013.5.0', '/cli/b\u000013.5.0']);
    });
});

async function startAndWaitForProcess(): Promise<number> {
    const child = spawn(process.execPath, ['-e', '']);
    assert.ok(child.pid);
    await new Promise<void>((resolve, reject) => {
        child.once('error', reject);
        child.once('exit', () => resolve());
    });
    return child.pid;
}
