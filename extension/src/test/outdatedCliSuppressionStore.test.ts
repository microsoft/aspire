import * as assert from 'assert';
import { spawn } from 'child_process';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as sinon from 'sinon';
import {
    FileSystemOutdatedCliSuppressionStore,
    OutdatedCliNotificationClaim,
} from '../utils/outdatedCliSuppressionStore';

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

    test('uses one timestamp for a claim marker and payload', async () => {
        const nowStub = sinon.stub(Date, 'now');
        nowStub.onFirstCall().returns(1_000);
        nowStub.returns(1_001);
        const first = new FileSystemOutdatedCliSuppressionStore(directory);
        const second = new FileSystemOutdatedCliSuppressionStore(directory);
        let claim: OutdatedCliNotificationClaim | undefined;

        try {
            claim = await first.tryClaimNotification('/cli/a\u000013.5.0');
            assert.ok(claim);
            const storageDirectory = path.join(directory, 'outdated-cli-suppressions');
            const claimMarker = fs.readdirSync(storageDirectory)
                .find(entry => entry.startsWith('notification-claim-'));
            assert.ok(claimMarker?.startsWith('notification-claim-1000-'));

            await second.add('/cli/b\u000013.5.0');
            assert.deepStrictEqual(await second.readAll(), ['/cli/b\u000013.5.0']);
        }
        finally {
            await claim?.release();
            nowStub.restore();
        }
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

    test('does not wait for a claim abandoned by another extension host', async () => {
        const exitedProcessId = await startAndWaitForProcess();
        createNotificationClaim(directory, '/cli/aspire\u000013.5.0', exitedProcessId);

        const store = new FileSystemOutdatedCliSuppressionStore(directory);
        await store.add('/cli/aspire\u000013.5.0');

        assert.deepStrictEqual(await store.readAll(), ['/cli/aspire\u000013.5.0']);
    });

    test('does not wait for an expired claim whose process ID has been reused', async () => {
        createNotificationClaim(directory, '/cli/aspire\u000013.5.0', process.pid, 0);

        const store = new FileSystemOutdatedCliSuppressionStore(directory);
        await store.add('/cli/aspire\u000013.5.0');

        assert.deepStrictEqual(await store.readAll(), ['/cli/aspire\u000013.5.0']);
    });

    test('does not wait for a claim timestamped in the future', async () => {
        createNotificationClaim(
            directory,
            '/cli/aspire\u000013.5.0',
            process.pid,
            Date.now() + 60_000);

        const store = new FileSystemOutdatedCliSuppressionStore(directory);
        await store.add('/cli/aspire\u000013.5.0');

        assert.deepStrictEqual(await store.readAll(), ['/cli/aspire\u000013.5.0']);
    });

    test('removes a malformed abandoned claim for another notification', async () => {
        const exitedProcessId = await startAndWaitForProcess();
        const claimPath = createNotificationClaim(directory, '/cli/other\u000013.5.0', exitedProcessId);
        fs.writeFileSync(claimPath, '{');

        const store = new FileSystemOutdatedCliSuppressionStore(directory);
        await store.add('/cli/aspire\u000013.5.0');

        assert.strictEqual(fs.existsSync(claimPath), false);
        assert.deepStrictEqual(await store.readAll(), ['/cli/aspire\u000013.5.0']);
    });
});

function createNotificationClaim(
    directory: string,
    notificationKey: string,
    processId: number,
    createdAt = Date.now(),
): string {
    const storageDirectory = path.join(directory, 'outdated-cli-suppressions');
    fs.mkdirSync(storageDirectory, { recursive: true });
    const claimPath = path.join(storageDirectory, `notification-claim-${createdAt}-${processId}-0.json`);
    fs.writeFileSync(claimPath, JSON.stringify({ notificationKey, processId, createdAt }));
    return claimPath;
}

async function startAndWaitForProcess(): Promise<number> {
    const child = spawn(process.execPath, ['-e', '']);
    assert.ok(child.pid);
    await new Promise<void>((resolve, reject) => {
        child.once('error', reject);
        child.once('exit', () => resolve());
    });
    return child.pid;
}
