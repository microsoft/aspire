import { link, mkdir, readFile, readdir, rename, unlink, writeFile } from 'fs/promises';
import * as path from 'path';
import { setTimeout as delay } from 'timers/promises';

const suppressionDirectoryName = 'outdated-cli-suppressions';
const suppressionFilePrefix = 'suppression-';
const operationLockFileName = '.operation-lock';
const operationLockOwnerPrefix = '.operation-lock-owner-';
const operationLockRetryIntervalMs = 10;
const operationLockAcquireTimeoutMs = 5_000;
let suppressionSequence = 0;
let operationLockSequence = 0;

/** Holds suppression writes until the warning has been dispatched, but not while awaiting user input. */
export interface OutdatedCliNotificationClaim {
    release(): Promise<void>;
}

export interface OutdatedCliSuppressionStore {
    readAll(): Promise<string[]>;
    add(notificationKey: string): Promise<void>;
    tryClaimNotification(notificationKey: string): Promise<OutdatedCliNotificationClaim | undefined>;
}

/**
 * Publishes each suppression as an immutable file so separate extension hosts can add entries
 * without a cross-window read-modify-write race. A short filesystem lease serializes suppression
 * writes with the final notification claim, closing the gap between the last read and showing UI.
 */
export class FileSystemOutdatedCliSuppressionStore implements OutdatedCliSuppressionStore {
    private readonly _directoryPath: string;
    private readonly _operationLockPath: string;

    constructor(globalStoragePath: string) {
        this._directoryPath = path.join(globalStoragePath, suppressionDirectoryName);
        this._operationLockPath = path.join(this._directoryPath, operationLockFileName);
    }

    async readAll(): Promise<string[]> {
        await mkdir(this._directoryPath, { recursive: true });
        return await this._readAll();
    }

    async add(notificationKey: string): Promise<void> {
        const release = await this._acquireOperationLock();
        try {
            const generation = `${Date.now()}-${process.pid}-${suppressionSequence++}`;
            const fileName = `${suppressionFilePrefix}${generation}.json`;
            const temporaryPath = path.join(this._directoryPath, `.${fileName}.tmp`);
            const finalPath = path.join(this._directoryPath, fileName);

            await writeFile(temporaryPath, JSON.stringify(notificationKey), { encoding: 'utf8', flag: 'wx' });
            await rename(temporaryPath, finalPath);
        }
        finally {
            await release();
        }
    }

    async tryClaimNotification(notificationKey: string): Promise<OutdatedCliNotificationClaim | undefined> {
        const release = await this._acquireOperationLock();
        try {
            if ((await this._readAll()).includes(notificationKey)) {
                await release();
                return undefined;
            }

            return { release };
        }
        catch (error) {
            await release();
            throw error;
        }
    }

    private async _readAll(): Promise<string[]> {
        const entries = await readdir(this._directoryPath, { withFileTypes: true });
        const suppressions: string[] = [];

        for (const entry of entries) {
            if (!entry.isFile() || !entry.name.startsWith(suppressionFilePrefix) || !entry.name.endsWith('.json')) {
                continue;
            }

            // Each marker contains a JSON string:
            //   "C:\\tools\\aspire.exe\u000013.5.0"
            const notificationKey = JSON.parse(await readFile(path.join(this._directoryPath, entry.name), 'utf8')) as unknown;
            if (typeof notificationKey !== 'string') {
                throw new Error(`Invalid Aspire CLI warning suppression file: ${entry.name}`);
            }
            suppressions.push(notificationKey);
        }

        return suppressions;
    }

    private async _acquireOperationLock(): Promise<() => Promise<void>> {
        await mkdir(this._directoryPath, { recursive: true });
        const startedAt = Date.now();

        while (true) {
            const generation = `${Date.now()}-${process.pid}-${operationLockSequence++}`;
            const ownerFileName = `${operationLockOwnerPrefix}${generation}`;
            const ownerPath = path.join(this._directoryPath, ownerFileName);
            await writeFile(ownerPath, ownerFileName, { encoding: 'utf8', flag: 'wx' });
            try {
                try {
                    // The fixed path and unique owner path become hard links to the same file.
                    // Creating the fixed link is the single atomic cross-process claim operation.
                    await link(ownerPath, this._operationLockPath);
                }
                catch (error) {
                    if (!hasErrorCode(error, 'EEXIST')) {
                        throw error;
                    }
                    await unlink(ownerPath);
                    await this._recoverAbandonedOperationLock();

                    if (Date.now() - startedAt >= operationLockAcquireTimeoutMs) {
                        throw new Error('Timed out acquiring the Aspire CLI warning suppression lock.');
                    }
                    await delay(operationLockRetryIntervalMs);
                    continue;
                }

                let released = false;
                return async () => {
                    if (released) {
                        return;
                    }
                    await this._releaseOperationLock(ownerPath);
                    released = true;
                };
            }
            catch (error) {
                await unlink(ownerPath).catch(error => {
                    if (!hasErrorCode(error, 'ENOENT')) {
                        throw error;
                    }
                });
                throw error;
            }
        }
    }

    private async _recoverAbandonedOperationLock(): Promise<void> {
        try {
            // The lock file contains its unique owner filename:
            //   .operation-lock-owner-1788280000000-12345-0
            const ownerFileName = await readFile(this._operationLockPath, 'utf8');
            const match = /^\.operation-lock-owner-\d+-(\d+)-\d+$/.exec(ownerFileName);
            if (!match || isProcessRunning(Number(match[1]))) {
                return;
            }

            try {
                // Removing the unique owner link grants one cleaner authority to remove the fixed
                // link. Other cleaners see ENOENT and leave any replacement lock untouched.
                await unlink(path.join(this._directoryPath, ownerFileName));
            }
            catch (error) {
                if (hasErrorCode(error, 'ENOENT')) {
                    return;
                }
                throw error;
            }

            await unlink(this._operationLockPath);
        }
        catch (error) {
            if (hasErrorCode(error, 'ENOENT')) {
                return;
            }
            throw error;
        }
    }

    private async _releaseOperationLock(ownerPath: string): Promise<void> {
        try {
            await unlink(this._operationLockPath);
        }
        catch (error) {
            if (!hasErrorCode(error, 'ENOENT')) {
                throw error;
            }
        }

        try {
            await unlink(ownerPath);
        }
        catch (error) {
            if (!hasErrorCode(error, 'ENOENT')) {
                throw error;
            }
        }
    }
}

function isProcessRunning(processId: number): boolean {
    try {
        process.kill(processId, 0);
        return true;
    }
    catch (error) {
        return !hasErrorCode(error, 'ESRCH');
    }
}

function hasErrorCode(error: unknown, ...codes: string[]): boolean {
    const code = error instanceof Error && 'code' in error ? error.code : undefined;
    return typeof code === 'string' && codes.includes(code);
}
