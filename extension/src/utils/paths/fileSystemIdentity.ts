import * as fs from 'fs';
import * as path from 'path';
import { isSamePath } from './comparison';

type FileSystemEntryIdentity = Pick<fs.BigIntStats, 'dev' | 'ino'>;
type FileSystemEntryIdentityProvider = (filePath: string) => FileSystemEntryIdentity | undefined;

export interface FileSystemEntryDescriptor {
    resolvedPath: string;
    identity: FileSystemEntryIdentity | undefined;
}

export function getFileSystemEntryDescriptor(
    filePath: string,
    getIdentity: FileSystemEntryIdentityProvider = tryGetFileSystemEntryIdentity): FileSystemEntryDescriptor {
    const resolvedPath = path.resolve(filePath);
    return {
        resolvedPath,
        identity: getIdentity(resolvedPath),
    };
}

export function isSameFileSystemEntryDescriptor(
    left: FileSystemEntryDescriptor,
    right: FileSystemEntryDescriptor): boolean {
    if (left.resolvedPath === right.resolvedPath) {
        return true;
    }

    if (hasStableFileSystemEntryIdentity(left.identity) && hasStableFileSystemEntryIdentity(right.identity)) {
        return left.identity.dev === right.identity.dev && left.identity.ino === right.identity.ino;
    }

    return isSamePath(left.resolvedPath, right.resolvedPath);
}

export function isSameFileSystemEntry(
    left: string,
    right: string,
    getIdentity: FileSystemEntryIdentityProvider = tryGetFileSystemEntryIdentity): boolean {
    const resolvedLeft = path.resolve(left);
    const resolvedRight = path.resolve(right);
    if (resolvedLeft === resolvedRight) {
        return true;
    }

    return isSameFileSystemEntryDescriptor(
        { resolvedPath: resolvedLeft, identity: getIdentity(resolvedLeft) },
        { resolvedPath: resolvedRight, identity: getIdentity(resolvedRight) });
}

function hasStableFileSystemEntryIdentity(
    identity: FileSystemEntryIdentity | undefined): identity is FileSystemEntryIdentity {
    return identity !== undefined && identity.ino !== 0n;
}

function tryGetFileSystemEntryIdentity(filePath: string): FileSystemEntryIdentity | undefined {
    try {
        return fs.statSync(filePath, { bigint: true });
    }
    catch {
        return undefined;
    }
}

export class FileSystemEntryDescriptorIndex {
    private readonly _descriptors: FileSystemEntryDescriptor[] = [];
    private readonly _exactPathBuckets = new Map<string, number[]>();
    private readonly _identityBuckets = new Map<string, number[]>();
    private readonly _fallbackPathBuckets = new Map<string, number[]>();
    private readonly _unstableFallbackPathBuckets = new Map<string, number[]>();

    find(descriptor: FileSystemEntryDescriptor): number | undefined {
        const identityKey = getStableIdentityKey(descriptor);
        const candidateBuckets = [
            this._exactPathBuckets.get(descriptor.resolvedPath),
            identityKey === undefined ? undefined : this._identityBuckets.get(identityKey),
            identityKey === undefined
                ? this._fallbackPathBuckets.get(getFallbackPathKey(descriptor))
                : this._unstableFallbackPathBuckets.get(getFallbackPathKey(descriptor)),
        ];
        const checkedIndexes = new Set<number>();

        for (const bucket of candidateBuckets) {
            for (const index of bucket ?? []) {
                if (!checkedIndexes.has(index)
                    && isSameFileSystemEntryDescriptor(this._descriptors[index], descriptor)) {
                    return index;
                }

                checkedIndexes.add(index);
            }
        }

        return undefined;
    }

    add(descriptor: FileSystemEntryDescriptor): void {
        const index = this._descriptors.length;
        this._descriptors.push(descriptor);
        this.addToBuckets(index, descriptor);
    }

    replace(index: number, descriptor: FileSystemEntryDescriptor): void {
        this._descriptors[index] = descriptor;
        this.addToBuckets(index, descriptor);
    }

    private addToBuckets(index: number, descriptor: FileSystemEntryDescriptor): void {
        addToBucket(this._exactPathBuckets, descriptor.resolvedPath, index);

        const fallbackPathKey = getFallbackPathKey(descriptor);
        addToBucket(this._fallbackPathBuckets, fallbackPathKey, index);

        const identityKey = getStableIdentityKey(descriptor);
        if (identityKey === undefined) {
            addToBucket(this._unstableFallbackPathBuckets, fallbackPathKey, index);
        } else {
            addToBucket(this._identityBuckets, identityKey, index);
        }
    }
}

function getStableIdentityKey(descriptor: FileSystemEntryDescriptor): string | undefined {
    const identity = descriptor.identity;
    return identity !== undefined && identity.ino !== 0n
        ? `${identity.dev}:${identity.ino}`
        : undefined;
}

function getFallbackPathKey(descriptor: FileSystemEntryDescriptor): string {
    return process.platform === 'win32'
        ? descriptor.resolvedPath.toLowerCase()
        : descriptor.resolvedPath;
}

function addToBucket(buckets: Map<string, number[]>, key: string, index: number): void {
    const bucket = buckets.get(key);
    if (!bucket) {
        buckets.set(key, [index]);
    } else if (bucket[bucket.length - 1] !== index) {
        bucket.push(index);
    }
}
