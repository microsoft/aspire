import { mkdir, readFile, readdir, rename, stat, unlink, writeFile } from 'fs/promises';
import * as path from 'path';
import { extensionLogOutputChannel } from './logging';

const suppressionFilePrefix = 'outdated-cli-suppression-';
const suppressionFileSuffix = '.json';
const maximumSuppressionFileLength = 256 * 1024;
let suppressionFileSequence = 0;

export interface PersistedCliSuppression {
    notificationKey: string;
    storageKey: string;
    suppressedAt: number;
}

export interface OutdatedCliSuppressionStore {
    readAll(): Promise<PersistedCliSuppression[]>;
    write(notificationKey: string, suppressedAt: number): Promise<PersistedCliSuppression>;
    delete(storageKey: string): Promise<void>;
}

/**
 * Uses one immutable file per suppression generation so separate VS Code extension hosts never
 * overwrite one another's cached state object. Publishing by rename keeps readers from observing
 * partially written JSON.
 */
export class FileSystemOutdatedCliSuppressionStore implements OutdatedCliSuppressionStore {
    constructor(private readonly _directoryPath: string) {
    }

    async readAll(): Promise<PersistedCliSuppression[]> {
        await mkdir(this._directoryPath, { recursive: true });
        const entries = await readdir(this._directoryPath, { withFileTypes: true });
        const suppressions: PersistedCliSuppression[] = [];

        for (const entry of entries) {
            if (!entry.isFile() ||
                !entry.name.startsWith(suppressionFilePrefix) ||
                !entry.name.endsWith(suppressionFileSuffix)) {
                continue;
            }

            const filePath = path.join(this._directoryPath, entry.name);
            try {
                if ((await stat(filePath)).size > maximumSuppressionFileLength) {
                    extensionLogOutputChannel.warn(`Ignoring oversized Aspire CLI warning suppression file '${entry.name}'.`);
                    continue;
                }

                // Suppression files contain:
                //   { "notificationKey": "<normalized-cli-path>\\u0000<version>", "suppressedAt": 1787880000000 }
                const value = JSON.parse(await readFile(filePath, 'utf8')) as {
                    notificationKey?: unknown;
                    suppressedAt?: unknown;
                };
                if (typeof value.notificationKey !== 'string' ||
                    typeof value.suppressedAt !== 'number' ||
                    !Number.isFinite(value.suppressedAt)) {
                    extensionLogOutputChannel.warn(`Ignoring malformed Aspire CLI warning suppression file '${entry.name}'.`);
                    continue;
                }

                suppressions.push({
                    notificationKey: value.notificationKey,
                    storageKey: entry.name,
                    suppressedAt: value.suppressedAt,
                });
            }
            catch (error) {
                if (isFileNotFoundError(error)) {
                    continue;
                }
                if (error instanceof SyntaxError) {
                    extensionLogOutputChannel.warn(`Ignoring malformed Aspire CLI warning suppression file '${entry.name}'.`);
                    continue;
                }
                throw new Error(
                    `Unable to read Aspire CLI warning suppression file '${entry.name}': ${String(error)}`);
            }
        }

        return suppressions;
    }

    async write(notificationKey: string, suppressedAt: number): Promise<PersistedCliSuppression> {
        await mkdir(this._directoryPath, { recursive: true });
        const generation = `${suppressedAt}-${process.pid}-${suppressionFileSequence++}`;
        const storageKey = `${suppressionFilePrefix}${generation}${suppressionFileSuffix}`;
        const temporaryPath = path.join(this._directoryPath, `.${storageKey}.tmp`);
        const storagePath = path.join(this._directoryPath, storageKey);

        await writeFile(
            temporaryPath,
            JSON.stringify({ notificationKey, suppressedAt }),
            { encoding: 'utf8', flag: 'wx' });
        try {
            await rename(temporaryPath, storagePath);
        }
        catch (error) {
            await unlink(temporaryPath).catch(cleanupError => {
                if (!isFileNotFoundError(cleanupError)) {
                    extensionLogOutputChannel.warn(
                        `Unable to remove temporary Aspire CLI warning suppression file: ${String(cleanupError)}`);
                }
            });
            throw error;
        }

        return { notificationKey, storageKey, suppressedAt };
    }

    async delete(storageKey: string): Promise<void> {
        try {
            await unlink(path.join(this._directoryPath, storageKey));
        }
        catch (error) {
            if (!isFileNotFoundError(error)) {
                throw error;
            }
        }
    }
}

function isFileNotFoundError(error: unknown): error is NodeJS.ErrnoException {
    return error instanceof Error &&
        'code' in error &&
        (error as NodeJS.ErrnoException).code === 'ENOENT';
}
