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
 * A linked worktree stores a `.git` file whose `gitdir:` line points at
 * `<main>/.git/worktrees/<name>`. The primary checkout has a `.git` directory.
 * Submodule `.git` files point at `.git/modules/` and are not treated as worktrees.
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

        if (isFile(gitPath) && isLinkedWorktreeGitFile(gitPath, current)) {
            return current;
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

export function ensureIsolatedCliArg(args: string[] | undefined, isolated: boolean): string[] | undefined {
    if (!isolated) {
        return args;
    }

    const existing = args ?? [];
    const separatorIndex = existing.indexOf('--');
    const beforeSeparator = separatorIndex === -1 ? existing : existing.slice(0, separatorIndex);
    if (beforeSeparator.includes('--isolated')) {
        return args;
    }

    if (separatorIndex === -1) {
        return [...existing, '--isolated'];
    }

    return [...beforeSeparator, '--isolated', ...existing.slice(separatorIndex)];
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

function isLinkedWorktreeGitFile(gitFilePath: string, worktreeRoot: string): boolean {
    let contents: string;
    try {
        contents = fs.readFileSync(gitFilePath, 'utf8');
    }
    catch {
        return false;
    }

    // gitdir: /repo/.git/worktrees/feature
    // gitdir: ../.git/worktrees/feature
    for (const rawLine of contents.split(/\r?\n/)) {
        const line = rawLine.trim();
        if (!line.toLowerCase().startsWith(gitDirPrefix)) {
            continue;
        }

        const gitDir = line.slice(gitDirPrefix.length).trim();
        if (gitDir.length === 0) {
            return false;
        }

        const absoluteGitDir = path.isAbsolute(gitDir)
            ? path.normalize(gitDir)
            : path.resolve(worktreeRoot, gitDir);
        return containsGitWorktreesSegment(absoluteGitDir);
    }

    return false;
}

function containsGitWorktreesSegment(gitDirPath: string): boolean {
    const segments = gitDirPath.split(/[/\\]/).filter(segment => segment.length > 0);
    for (let i = 1; i < segments.length; i++) {
        if (segments[i].toLowerCase() === worktreesSegment &&
            segments[i - 1].toLowerCase() === gitDirectoryName) {
            return true;
        }
    }

    return false;
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
