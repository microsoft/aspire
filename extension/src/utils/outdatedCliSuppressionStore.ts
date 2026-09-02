import { mkdir, readFile, readdir, rename, unlink, writeFile } from 'fs/promises';
import * as path from 'path';
import { setTimeout as delay } from 'timers/promises';

const suppressionDirectoryName = 'outdated-cli-suppressions';
const suppressionFilePrefix = 'suppression-';
const notificationClaimFilePrefix = 'notification-claim-';
const markerFileSuffix = '.json';
const notificationClaimRetryIntervalMs = 10;
const notificationClaimLeaseMs = 60_000;
let markerSequence = 0;

/** Represents a published claim that remains valid until its bounded lease expires. */
export interface OutdatedCliNotificationClaim {
    isValid(): boolean;
    release(): Promise<void>;
}

export interface OutdatedCliSuppressionStore {
    readAll(): Promise<string[]>;
    add(notificationKey: string): Promise<void>;
    tryClaimNotification(notificationKey: string): Promise<OutdatedCliNotificationClaim | undefined>;
}

interface NotificationClaimMarker {
    notificationKey?: unknown;
    processId?: unknown;
    createdAt?: unknown;
}

/**
 * Uses immutable markers to order notifications and suppressions across extension hosts. A claim is
 * published before its final suppression read. A suppression is published before it waits for older
 * claims to dispatch, so it cannot complete before a warning that already passed the final read.
 */
export class FileSystemOutdatedCliSuppressionStore implements OutdatedCliSuppressionStore {
    private readonly _directoryPath: string;

    constructor(globalStoragePath: string) {
        this._directoryPath = path.join(globalStoragePath, suppressionDirectoryName);
    }

    async readAll(): Promise<string[]> {
        await mkdir(this._directoryPath, { recursive: true });
        return await this._readAllSuppressions();
    }

    async add(notificationKey: string): Promise<void> {
        await this._publishMarker(suppressionFilePrefix, notificationKey);
        await this._waitForNotificationClaims(notificationKey);
    }

    async tryClaimNotification(notificationKey: string): Promise<OutdatedCliNotificationClaim | undefined> {
        const createdAt = Date.now();
        const claimPath = await this._publishMarker(notificationClaimFilePrefix, {
            notificationKey,
            processId: process.pid,
            createdAt,
        }, createdAt);
        let released = false;
        const claim: OutdatedCliNotificationClaim = {
            isValid: () => isLeaseCurrent(createdAt),
            release: async () => {
                if (released) {
                    return;
                }
                await unlink(claimPath).catch(error => {
                    if (!hasErrorCode(error, 'ENOENT')) {
                        throw error;
                    }
                });
                released = true;
            },
        };

        try {
            if ((await this._readAllSuppressions()).includes(notificationKey)) {
                await claim.release();
                return undefined;
            }

            return claim;
        }
        catch (error) {
            await claim.release();
            throw error;
        }
    }

    private async _readAllSuppressions(): Promise<string[]> {
        const entries = await readdir(this._directoryPath, { withFileTypes: true });
        const suppressions: string[] = [];

        for (const entry of entries) {
            if (!entry.isFile() ||
                !entry.name.startsWith(suppressionFilePrefix) ||
                !entry.name.endsWith(markerFileSuffix)) {
                continue;
            }

            // Each suppression marker contains a JSON string:
            //   "C:\\tools\\aspire.exe\u000013.5.0"
            const notificationKey = JSON.parse(
                await readFile(path.join(this._directoryPath, entry.name), 'utf8')) as unknown;
            if (typeof notificationKey !== 'string') {
                throw new Error(`Invalid Aspire CLI warning suppression file: ${entry.name}`);
            }
            suppressions.push(notificationKey);
        }

        return suppressions;
    }

    private async _waitForNotificationClaims(notificationKey: string): Promise<void> {
        while (true) {
            let hasActiveClaim = false;
            const entries = await readdir(this._directoryPath, { withFileTypes: true });
            for (const entry of entries) {
                if (!entry.isFile() ||
                    !entry.name.startsWith(notificationClaimFilePrefix) ||
                    !entry.name.endsWith(markerFileSuffix)) {
                    continue;
                }

                const claimPath = path.join(this._directoryPath, entry.name);
                const markerIdentity = getNotificationClaimIdentity(entry.name);
                // Claim markers contain:
                //   { "notificationKey": "<normalized-path>\\u0000<version>",
                //     "processId": 12345, "createdAt": 1788280000000 }
                const contents = await readFile(claimPath, 'utf8').catch(error => {
                    if (hasErrorCode(error, 'ENOENT')) {
                        return undefined;
                    }
                    throw error;
                });
                if (contents === undefined) {
                    continue;
                }
                let claim: NotificationClaimMarker | null = null;
                try {
                    claim = JSON.parse(contents) as NotificationClaimMarker | null;
                }
                catch (error) {
                    if (!(error instanceof SyntaxError)) {
                        throw error;
                    }
                }

                if (claim === null ||
                    !markerIdentity ||
                    typeof claim.notificationKey !== 'string' ||
                    claim.processId !== markerIdentity.processId ||
                    claim.createdAt !== markerIdentity.createdAt) {
                    if (markerIdentity &&
                        isLeaseCurrent(markerIdentity.createdAt) &&
                        isProcessRunning(markerIdentity.processId)) {
                        hasActiveClaim = true;
                    } else {
                        await removeFileIfPresent(claimPath);
                    }
                    continue;
                }

                if (isLeaseCurrent(claim.createdAt) && isProcessRunning(claim.processId)) {
                    if (claim.notificationKey === notificationKey) {
                        hasActiveClaim = true;
                    }
                    continue;
                }

                await removeFileIfPresent(claimPath);
            }

            if (!hasActiveClaim) {
                return;
            }
            await delay(notificationClaimRetryIntervalMs);
        }
    }

    private async _publishMarker(
        prefix: string,
        value: unknown,
        createdAt = Date.now(),
    ): Promise<string> {
        await mkdir(this._directoryPath, { recursive: true });
        const generation = `${createdAt}-${process.pid}-${markerSequence++}`;
        const fileName = `${prefix}${generation}${markerFileSuffix}`;
        const temporaryPath = path.join(this._directoryPath, `.${fileName}.tmp`);
        const finalPath = path.join(this._directoryPath, fileName);

        await writeFile(temporaryPath, JSON.stringify(value), { encoding: 'utf8', flag: 'wx' });
        try {
            await rename(temporaryPath, finalPath);
        }
        catch (error) {
            await unlink(temporaryPath).catch(cleanupError => {
                if (!hasErrorCode(cleanupError, 'ENOENT')) {
                    throw cleanupError;
                }
            });
            throw error;
        }

        return finalPath;
    }
}

function isLeaseCurrent(createdAt: number): boolean {
    const age = Date.now() - createdAt;
    return age >= 0 && age < notificationClaimLeaseMs;
}

function getNotificationClaimIdentity(
    fileName: string,
): { createdAt: number; processId: number } | undefined {
    const match = /^notification-claim-(\d+)-(\d+)-\d+\.json$/.exec(fileName);
    return match
        ? { createdAt: Number(match[1]), processId: Number(match[2]) }
        : undefined;
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

async function removeFileIfPresent(filePath: string): Promise<void> {
    await unlink(filePath).catch(error => {
        if (!hasErrorCode(error, 'ENOENT')) {
            throw error;
        }
    });
}

function hasErrorCode(error: unknown, ...codes: string[]): boolean {
    const code = error instanceof Error && 'code' in error ? error.code : undefined;
    return typeof code === 'string' && codes.includes(code);
}
