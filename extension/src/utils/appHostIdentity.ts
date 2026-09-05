import * as fs from 'fs';
import * as path from 'path';
import { isSameFileSystemEntry } from './paths/fileSystemIdentity';
import { isAppHostSourceFile } from './paths/comparison';

/** Whether two paths name the same AppHost. */
export type AppHostIdentityRelation = 'same' | 'different' | 'ambiguous';

declare const opaqueAppHostIdentityBrand: unique symbol;

/** Opaque, extension-window-scoped identity for one lexical AppHost target. */
export type OpaqueAppHostIdentity = string & { readonly [opaqueAppHostIdentityBrand]: true };

export interface AppHostIdentityKeyInfo {
    readonly key: string;
    readonly pathKeys: readonly string[];
}

const appHostProjectFileExtensions = ['.csproj', '.fsproj', '.vbproj'];
const appHostAliasKeySuffix = '\u0000apphost';
const currentTargetIdentityRegistry = new Map<string, OpaqueAppHostIdentity>();
let nextOpaqueIdentity = 0;

interface CurrentTargetIdentityKeyInfo extends AppHostIdentityKeyInfo {
    readonly exactPathKey: string;
}

export function getAppHostPathComparisonKey(value: string): string {
    return canonicalize(path.normalize(path.resolve(value)));
}

/**
 * Exact paths match. A project and sibling AppHost source match only when the directory
 * contains exactly one candidate of each shape; otherwise their relationship is ambiguous.
 */
export function compareAppHostIdentity(left: string | undefined, right: string | undefined): AppHostIdentityRelation {
    if (!left || !right) {
        return 'different';
    }

    const leftPath = canonicalize(path.normalize(path.resolve(left)));
    const rightPath = canonicalize(path.normalize(path.resolve(right)));
    if (isSameFileSystemEntry(leftPath, rightPath)) {
        return 'same';
    }

    const directory = path.dirname(leftPath);
    if (!isSameFileSystemEntry(directory, path.dirname(rightPath))) {
        return 'different';
    }

    const projectFile = isAppHostProjectFile(leftPath)
        ? leftPath
        : isAppHostProjectFile(rightPath) ? rightPath : undefined;
    const sourceFile = isAppHostSourceFile(leftPath)
        ? leftPath
        : isAppHostSourceFile(rightPath) ? rightPath : undefined;
    if (!projectFile || !sourceFile) {
        return 'different';
    }

    const shapes = readDirectoryAppHostShapes(directory);
    if (!shapes.enumerated) {
        return 'ambiguous';
    }

    if (!containsPath(shapes.projectFiles, projectFile) || !containsPath(shapes.sourceFiles, sourceFile)) {
        return 'different';
    }

    return shapes.projectFiles.length === 1 && shapes.sourceFiles.length === 1 ? 'same' : 'ambiguous';
}

export function getAppHostIdentityKey(appHostPath: string): string {
    return getAppHostIdentityKeyInfo(appHostPath).key;
}

/**
 * Returns an identity bound to the canonical filesystem target currently selected by the path.
 *
 * Callers that capture launch, failure, or confirmation ownership retain the returned
 * opaque value; a later resolution of the same lexical path receives a different value
 * when its canonical target changed. Replacing the same target file in place or through
 * an atomic rename preserves identity because the selected AppHost did not change.
 */
export function getOrCreateIdentityForCurrentAppHostTarget(appHostPath: string): OpaqueAppHostIdentity {
    for (let attempt = 0; attempt < 3; attempt++) {
        const keyInfo = getCurrentTargetIdentityKeyInfo(appHostPath);
        if (!sameCurrentTargetIdentityKeyInfo(keyInfo, getCurrentTargetIdentityKeyInfo(appHostPath))) {
            continue;
        }

        const exactIdentity = currentTargetIdentityRegistry.get(keyInfo.exactPathKey);
        if (exactIdentity) {
            assignUnmappedCurrentTargetAliases(keyInfo, exactIdentity);
            return exactIdentity;
        }

        const issuedIdentities = new Set<OpaqueAppHostIdentity>();
        const aliasIdentity = currentTargetIdentityRegistry.get(keyInfo.key);
        if (aliasIdentity) {
            issuedIdentities.add(aliasIdentity);
        }
        for (const pathKey of keyInfo.pathKeys) {
            const identity = currentTargetIdentityRegistry.get(pathKey);
            if (identity) {
                issuedIdentities.add(identity);
            }
        }

        const identity = issuedIdentities.size === 1
            ? [...issuedIdentities][0]
            : createOpaqueAppHostIdentity();
        currentTargetIdentityRegistry.set(keyInfo.exactPathKey, identity);
        if (issuedIdentities.size <= 1) {
            assignUnmappedCurrentTargetAliases(keyInfo, identity);
        }

        return identity;
    }

    // A target that changes during every bounded sample cannot be safely correlated.
    // Return an unregistered identity so the next resolution necessarily differs.
    return createOpaqueAppHostIdentity();
}

function createOpaqueAppHostIdentity(): OpaqueAppHostIdentity {
    return `apphost-${++nextOpaqueIdentity}` as OpaqueAppHostIdentity;
}

/**
 * One AppHost captured as the identity it currently resolves to plus the physical path that
 * identity was derived from.
 *
 * A selector - a registry entry, a tool argument, a launch configuration's program - is only a
 * name, and a name can be repointed while an asynchronous read or launch is in flight. Holding
 * the canonical path alongside the identity is what lets a caller perform the operation against
 * the AppHost it decided on while still using the selector to decide whether that decision is
 * still valid.
 *
 * SECURITY - what this does and does not guarantee.
 *
 * Guaranteed: a mutable selector cannot redirect an operation after it was resolved. The
 * operation travels as the captured canonical path, containment is validated against that
 * captured path rather than against a fresh sample of the selector, identity freshness is
 * re-checked at the moment a result is published or a launch commits, and any change observed at
 * one of those boundaries fails the operation closed rather than continuing against a different
 * AppHost.
 *
 * Not guaranteed: protection against replacing the canonical physical path itself, or one of its
 * ancestors, after the final check and before the child process opens it. The Aspire CLI accepts
 * a path, not an open filesystem descriptor, so that window is inherent to the interface and no
 * amount of re-checking removes it. What is defended here is the far larger and remotely
 * reachable class of confusion - an alias or registry entry silently naming a different AppHost
 * between resolution and use - not an attacker who can already rewrite the real project file.
 */
export interface CurrentAppHostTargetBinding {
    readonly identity: OpaqueAppHostIdentity;
    /**
     * Physical path of the AppHost the identity was captured from, or the caller's own path
     * when the filesystem cannot canonicalize it.
     *
     * Never rendered to a user or a model: it defeats the workspace-relative display the
     * selector provides, and a caller that showed it would leak paths outside the workspace.
     */
    readonly canonicalPath: string;
}

/**
 * Captures an AppHost as one identity and the physical path that identity belongs to.
 *
 * The canonical path is sampled repeatedly until two consecutive samples agree, and the identity
 * is then derived from *that captured path* rather than from the selector again. Deriving it from
 * the selector is what pairs one AppHost's identity with another one's path: the selector can be
 * repointed and repointed back between the samples that bracket the identity read, so both
 * samples agree while the identity read observed a different file. Reads and launches would then
 * run against the captured path while every freshness check answered for the other AppHost.
 *
 * The captured path is confirmed to still canonicalize to itself before the binding is returned.
 * That closes the window in which the physical file is replaced by an alias between the capture
 * and the identity read; it cannot close the window after the binding is returned, which is why
 * identity freshness is re-checked at the point an operation commits. See the security note on
 * {@link CurrentAppHostTargetBinding}.
 *
 * A target that moves during every sample is bound to a fresh identity no registry entry holds,
 * so the first revalidation refuses it instead of publishing or launching anything.
 */
export function bindCurrentAppHostTarget(appHostPath: string): CurrentAppHostTargetBinding {
    // Normalized so a path the filesystem cannot canonicalize still keys one AppHost one way:
    // this value becomes the bound path, and reservations, lock keys, and comparisons are all
    // built from it.
    const resolvedPath = path.normalize(path.resolve(appHostPath));
    let canonicalPath = canonicalize(resolvedPath);
    for (let attempt = 0; attempt < 3; attempt++) {
        const confirmation = canonicalize(resolvedPath);
        if (confirmation !== canonicalPath) {
            canonicalPath = confirmation;
            continue;
        }

        const identity = getOrCreateIdentityForCurrentAppHostTarget(canonicalPath);
        // The identity was read through the captured path, so that path has to still be the file
        // it named. A physical path replaced by an alias in between would hand back an identity
        // belonging to whatever the alias points at.
        if (canonicalize(canonicalPath) === canonicalPath) {
            return { identity, canonicalPath };
        }
    }

    return { identity: createOpaqueAppHostIdentity(), canonicalPath };
}

function getCurrentTargetIdentityKeyInfo(appHostPath: string): CurrentTargetIdentityKeyInfo {
    // Capture a mutable selector once. Deriving the alias key and exact key from separate
    // realpath samples could combine one target's aliases with another target's exact key when a
    // symlink is retargeted between the two calls.
    const resolvedPath = canonicalize(path.normalize(path.resolve(appHostPath)));
    const keyInfo = getAppHostIdentityKeyInfoFromCanonicalPath(resolvedPath);
    return {
        ...keyInfo,
        exactPathKey: getCapturedAppHostPathKey(resolvedPath),
    };
}

function sameCurrentTargetIdentityKeyInfo(left: CurrentTargetIdentityKeyInfo, right: CurrentTargetIdentityKeyInfo): boolean {
    return left.exactPathKey === right.exactPathKey &&
        left.key === right.key &&
        left.pathKeys.length === right.pathKeys.length &&
        left.pathKeys.every((pathKey, index) => pathKey === right.pathKeys[index]);
}

function assignUnmappedCurrentTargetAliases(keyInfo: CurrentTargetIdentityKeyInfo, identity: OpaqueAppHostIdentity): void {
    const keys = new Set([keyInfo.exactPathKey, keyInfo.key, ...keyInfo.pathKeys]);
    if ([...keys].some(key => {
        const existing = currentTargetIdentityRegistry.get(key);
        return existing !== undefined && existing !== identity;
    })) {
        return;
    }

    for (const key of keys) {
        if (!currentTargetIdentityRegistry.has(key)) {
            currentTargetIdentityRegistry.set(key, identity);
        }
    }
}

export function resetAppHostIdentityRegistry(): void {
    currentTargetIdentityRegistry.clear();
    nextOpaqueIdentity = 0;
}

export function __resetAppHostIdentityRegistryForTests(): void {
    resetAppHostIdentityRegistry();
}

export function getAppHostIdentityKeyInfo(appHostPath: string): AppHostIdentityKeyInfo {
    const resolvedPath = canonicalize(path.normalize(path.resolve(appHostPath)));
    return getAppHostIdentityKeyInfoFromCanonicalPath(resolvedPath);
}

function getAppHostIdentityKeyInfoFromCanonicalPath(resolvedPath: string): AppHostIdentityKeyInfo {
    if (!isAppHostProjectFile(resolvedPath) && !isAppHostSourceFile(resolvedPath)) {
        const key = getCapturedAppHostPathKey(resolvedPath);
        return { key, pathKeys: [key] };
    }

    const directory = path.dirname(resolvedPath);
    const shapes = readDirectoryAppHostShapes(directory);
    const isAliasedPair = shapes.enumerated &&
        shapes.projectFiles.length === 1 &&
        shapes.sourceFiles.length === 1 &&
        (containsPath(shapes.projectFiles, resolvedPath) || containsPath(shapes.sourceFiles, resolvedPath));

    if (isAliasedPair) {
        return {
            key: `${getCapturedAppHostPathKey(directory)}${appHostAliasKeySuffix}`,
            pathKeys: [
                getCapturedAppHostPathKey(shapes.projectFiles[0]),
                getCapturedAppHostPathKey(shapes.sourceFiles[0]),
            ],
        };
    }

    const key = getCapturedAppHostPathKey(resolvedPath);
    return { key, pathKeys: [key] };
}

function getCapturedAppHostPathKey(value: string): string {
    return path.normalize(path.resolve(value));
}

export function isAppHostProjectFile(value: string): boolean {
    return appHostProjectFileExtensions.includes(path.extname(value).toLowerCase());
}

interface DirectoryAppHostShapes {
    readonly projectFiles: readonly string[];
    readonly sourceFiles: readonly string[];
    readonly enumerated: boolean;
}

function readDirectoryAppHostShapes(directoryPath: string): DirectoryAppHostShapes {
    let entries: fs.Dirent[];
    try {
        entries = fs.readdirSync(directoryPath, { withFileTypes: true });
    }
    catch {
        return { projectFiles: [], sourceFiles: [], enumerated: false };
    }

    const projectFiles: string[] = [];
    const sourceFiles: string[] = [];
    for (const entry of entries) {
        if (!entry.isFile() && !entry.isSymbolicLink()) {
            continue;
        }

        const entryPath = path.join(directoryPath, entry.name);
        if (isAppHostProjectFile(entry.name)) {
            projectFiles.push(entryPath);
        }
        else if (isAppHostSourceFile(entry.name)) {
            sourceFiles.push(entryPath);
        }
    }

    return { projectFiles, sourceFiles, enumerated: true };
}

function containsPath(paths: readonly string[], candidate: string): boolean {
    return paths.some(value => isSameFileSystemEntry(value, candidate));
}

export function canonicalizeAppHostPath(resolvedPath: string): string {
    return canonicalize(resolvedPath);
}

export function isAppHostPathWithinDirectory(appHostPath: string, directoryPath: string): boolean {
    return isCapturedAppHostPathWithinDirectory(
        canonicalize(path.normalize(path.resolve(appHostPath))),
        canonicalize(path.normalize(path.resolve(directoryPath))));
}

/**
 * Reports whether an already-captured physical AppHost path lies within an already-captured
 * physical directory.
 *
 * Neither side is canonicalized again. That is the point: a caller that has bound an AppHost has
 * already decided which physical file the operation runs against, and re-following the selector
 * here would decide containment from a *different* sample than the one the operation carries. An
 * alias flipped between the two samples would then pass a containment check for a file inside the
 * workspace while the bound path names one outside it.
 *
 * The ancestor walk still reads the filesystem to compare directories, so it answers for the
 * directory tree as it exists now. Replacing an ancestor of the captured path itself is outside
 * what this can detect; see the security note on {@link CurrentAppHostTargetBinding}.
 */
export function isCapturedAppHostPathWithinDirectory(capturedAppHostPath: string, capturedDirectoryPath: string): boolean {
    const directory = path.normalize(path.resolve(capturedDirectoryPath));
    let current = path.normalize(path.resolve(capturedAppHostPath));
    while (true) {
        if (isSameFileSystemEntry(current, directory)) {
            return true;
        }

        const parent = path.dirname(current);
        if (parent === current) {
            return false;
        }

        current = parent;
    }
}

function canonicalize(resolvedPath: string): string {
    try {
        // Native realpath returns the filesystem's canonical casing on Windows. That keeps
        // differently-cased references to one file on one key without collapsing distinct
        // files in a case-sensitive Windows directory.
        return fs.realpathSync.native(resolvedPath);
    }
    catch {
        return resolvedPath;
    }
}
