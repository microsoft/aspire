import * as fs from 'fs';
import * as path from 'path';

const maxAncestorWalks = 64;
const gitDirPrefix = 'gitdir:';
const gitDirectoryName = '.git';
const worktreesSegment = 'worktrees';

/**
 * Returns the linked worktree root that contains `startPath`, or `undefined` when the
 * path is in the primary checkout, a submodule, or not a git repository.
 *
 * Git stores linked-worktree metadata in these shapes:
 *   Standard:  /repo/.git/worktrees/feature
 *   Bare:      /repo.git/worktrees/feature
 *   Separate:  /separate-git/worktrees/feature
 *   Submodule: /repo/.git/worktrees/feature/modules/dependency
 *
 *   /checkout/.git:
 *   gitdir: /repo/.git/worktrees/feature
 *
 *   /repo/.git/worktrees/feature/gitdir:
 *   /checkout/.git
 *
 * The admin `gitdir` back-pointer rejects stale metadata, while requiring the admin
 * directory's direct parent to be `worktrees` excludes linked-worktree submodules.
 * See https://git-scm.com/docs/git-worktree
 */
export function tryGetLinkedWorktreeRoot(startPath: string | undefined): string | undefined {
    if (!startPath) {
        return undefined;
    }

    let current = getWalkStartDirectory(startPath);
    for (let i = 0; i < maxAncestorWalks; i++) {
        const gitPath = path.join(current, gitDirectoryName);
        if (isDirectory(gitPath)) {
            return undefined;
        }

        if (isFile(gitPath)) {
            return isLinkedWorktreeGitFile(gitPath) ? canonicalizePath(current) : undefined;
        }

        const parent = path.dirname(current);
        if (parent === current) {
            return undefined;
        }

        current = parent;
    }

    return undefined;
}

export function isLinkedGitWorktree(startPath: string | undefined): boolean {
    return tryGetLinkedWorktreeRoot(startPath) !== undefined;
}

export function resolveIsolated(explicit: boolean | undefined, startPath: string | undefined): boolean {
    return explicit ?? isLinkedGitWorktree(startPath);
}

/**
 * Reads the root Aspire CLI `--isolated` option, ignoring any AppHost-owned args after
 * the first `--` separator. `aspire run -- --isolated false` forwards `--isolated false`
 * to the AppHost itself, so counting those entries as root options would stop the
 * extension from adding the launch isolation the linked-worktree path still needs.
 */
export function getRootIsolatedCliArg(args: readonly string[] | undefined): boolean | undefined {
    if (!args) {
        return undefined;
    }

    const beforeSeparator = getArgsBeforeAppSeparator(args);
    for (let i = 0; i < beforeSeparator.length; i++) {
        const current = beforeSeparator[i];
        if (current === '--isolated') {
            return beforeSeparator[i + 1]?.toLowerCase() === 'false' ? false : true;
        }

        if (current.startsWith('--isolated=')) {
            return current.slice('--isolated='.length).toLowerCase() === 'false' ? false : true;
        }
    }

    return undefined;
}

export function ensureIsolatedCliArg(args: string[] | undefined, isolated: boolean | undefined): string[] | undefined {
    if (isolated === undefined) {
        return removeRootIsolatedCliArgs(args);
    }

    const existing = args ?? [];
    const separatorIndex = existing.indexOf('--');
    const beforeSeparator = getArgsBeforeAppSeparator(existing);
    if (beforeSeparator.some(arg => arg === '--isolated' || arg.startsWith('--isolated='))) {
        return args;
    }

    const isolationArgs = isolated ? ['--isolated'] : ['--isolated', 'false'];
    if (separatorIndex === -1) {
        return [...existing, ...isolationArgs];
    }

    return [...beforeSeparator, ...isolationArgs, ...existing.slice(separatorIndex)];
}

function removeRootIsolatedCliArgs(args: string[] | undefined): string[] | undefined {
    if (!args) {
        return undefined;
    }

    const separatorIndex = args.indexOf('--');
    const beforeSeparator = getArgsBeforeAppSeparator(args);
    const filtered: string[] = [];
    let changed = false;
    for (let i = 0; i < beforeSeparator.length; i++) {
        const current = beforeSeparator[i];
        if (current === '--isolated') {
            changed = true;
            const value = beforeSeparator[i + 1]?.toLowerCase();
            if (value === 'true' || value === 'false') {
                i++;
            }
        }
        else if (current.startsWith('--isolated=')) {
            changed = true;
        }
        else {
            filtered.push(current);
        }
    }

    if (!changed) {
        return args;
    }

    return separatorIndex === -1
        ? filtered
        : [...filtered, ...args.slice(separatorIndex)];
}

function getArgsBeforeAppSeparator(args: readonly string[]): readonly string[] {
    const separatorIndex = args.indexOf('--');
    return separatorIndex === -1 ? args : args.slice(0, separatorIndex);
}

function getWalkStartDirectory(startPath: string): string {
    try {
        if (fs.statSync(startPath).isDirectory()) {
            return startPath;
        }
    }
    catch {
        // The path may not exist yet (launch tests use placeholder AppHost paths).
    }

    const directory = path.dirname(startPath);
    return directory || startPath;
}

function isLinkedWorktreeGitFile(gitFilePath: string): boolean {
    const adminDirectory = tryReadGitDirTarget(gitFilePath);
    if (!adminDirectory || !isDirectory(adminDirectory)) {
        return false;
    }

    const canonicalAdminDirectory = canonicalizePath(adminDirectory);
    const adminParentName = path.basename(path.dirname(canonicalAdminDirectory));
    const isWorktreesParent = process.platform === 'win32'
        ? adminParentName.toLowerCase() === worktreesSegment
        : adminParentName === worktreesSegment;
    if (!isWorktreesParent) {
        return false;
    }

    // Git resolves this back-pointer from the physical admin directory, even when
    // the checkout's .git file reached that directory through an alias.
    const checkoutGitFile = tryReadPath(
        path.join(canonicalAdminDirectory, 'gitdir'),
        canonicalAdminDirectory);
    return checkoutGitFile !== undefined && pathsEqual(checkoutGitFile, gitFilePath);
}

function tryReadGitDirTarget(gitFilePath: string): string | undefined {
    // Relative gitdir values are based on the physical directory containing this
    // metadata file, not the lexical checkout alias used to discover it.
    const canonicalGitFilePath = canonicalizePath(gitFilePath);
    let contents: string;
    try {
        contents = fs.readFileSync(canonicalGitFilePath, 'utf8');
    }
    catch {
        return undefined;
    }

    for (const rawLine of contents.split(/\r?\n/)) {
        const line = rawLine.trim();
        if (!line.toLowerCase().startsWith(gitDirPrefix)) {
            continue;
        }

        const gitDir = line.slice(gitDirPrefix.length).trim();
        if (gitDir.length === 0) {
            return undefined;
        }

        return path.resolve(path.dirname(canonicalGitFilePath), gitDir);
    }

    return undefined;
}

function tryReadPath(filePath: string, relativeTo: string): string | undefined {
    try {
        const value = fs.readFileSync(filePath, 'utf8').trim();
        return value.length > 0 ? path.resolve(relativeTo, value) : undefined;
    }
    catch {
        return undefined;
    }
}

function pathsEqual(left: string, right: string): boolean {
    const canonicalLeft = canonicalizePath(left);
    const canonicalRight = canonicalizePath(right);
    return process.platform === 'win32'
        ? canonicalLeft.toLowerCase() === canonicalRight.toLowerCase()
        : canonicalLeft === canonicalRight;
}

function canonicalizePath(value: string): string {
    const resolved = path.resolve(value);
    try {
        return fs.realpathSync.native(resolved);
    }
    catch {
        return resolved;
    }
}

function isDirectory(target: string): boolean {
    try {
        return fs.statSync(target).isDirectory();
    }
    catch {
        return false;
    }
}

function isFile(target: string): boolean {
    try {
        return fs.statSync(target).isFile();
    }
    catch {
        return false;
    }
}
