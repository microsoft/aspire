import { mkdir, readFile, readdir, rename, writeFile } from 'fs/promises';
import * as path from 'path';

const suppressionDirectoryName = 'outdated-cli-suppressions';
const suppressionFilePrefix = 'suppression-';
let suppressionSequence = 0;

export interface OutdatedCliSuppressionStore {
    readAll(): Promise<string[]>;
    add(notificationKey: string): Promise<void>;
}

/**
 * Publishes each suppression as an immutable file so separate extension hosts can add entries
 * without a cross-window read-modify-write race.
 */
export class FileSystemOutdatedCliSuppressionStore implements OutdatedCliSuppressionStore {
    private readonly _directoryPath: string;

    constructor(globalStoragePath: string) {
        this._directoryPath = path.join(globalStoragePath, suppressionDirectoryName);
    }

    async readAll(): Promise<string[]> {
        await mkdir(this._directoryPath, { recursive: true });
        const entries = await readdir(this._directoryPath, { withFileTypes: true });
        const suppressions: string[] = [];

        for (const entry of entries) {
            if (!entry.isFile() || !entry.name.startsWith(suppressionFilePrefix) || !entry.name.endsWith('.json')) {
                continue;
            }

            const notificationKey = JSON.parse(await readFile(path.join(this._directoryPath, entry.name), 'utf8')) as unknown;
            if (typeof notificationKey !== 'string') {
                throw new Error(`Invalid Aspire CLI warning suppression file: ${entry.name}`);
            }
            suppressions.push(notificationKey);
        }

        return suppressions;
    }

    async add(notificationKey: string): Promise<void> {
        await mkdir(this._directoryPath, { recursive: true });
        const generation = `${Date.now()}-${process.pid}-${suppressionSequence++}`;
        const fileName = `${suppressionFilePrefix}${generation}.json`;
        const temporaryPath = path.join(this._directoryPath, `.${fileName}.tmp`);
        const finalPath = path.join(this._directoryPath, fileName);

        await writeFile(temporaryPath, JSON.stringify(notificationKey), { encoding: 'utf8', flag: 'wx' });
        await rename(temporaryPath, finalPath);
    }
}
