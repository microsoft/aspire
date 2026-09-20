import * as fs from 'fs';
import * as path from 'path';

export function writeLinkedWorktreeMetadata(
    worktreeRoot: string,
    commonGitDirectory: string,
    worktreeName = 'feature',
    useRelativePaths = false): string {
    const fullWorktreeRoot = path.resolve(worktreeRoot);
    const adminDirectory = path.join(path.resolve(commonGitDirectory), 'worktrees', worktreeName);
    fs.mkdirSync(adminDirectory, { recursive: true });

    const gitFilePath = writeGitDirFile(fullWorktreeRoot, adminDirectory, useRelativePaths);
    const backPointer = useRelativePaths
        ? path.relative(adminDirectory, gitFilePath)
        : gitFilePath;
    fs.writeFileSync(path.join(adminDirectory, 'gitdir'), `${backPointer}\n`);

    return adminDirectory;
}

export function writeGitDirFile(
    checkoutRoot: string,
    gitDirectory: string,
    useRelativePath = false): string {
    const fullCheckoutRoot = path.resolve(checkoutRoot);
    const fullGitDirectory = path.resolve(gitDirectory);
    fs.mkdirSync(fullCheckoutRoot, { recursive: true });
    fs.mkdirSync(fullGitDirectory, { recursive: true });

    const gitFilePath = path.join(fullCheckoutRoot, '.git');
    const pointer = useRelativePath
        ? path.relative(fullCheckoutRoot, fullGitDirectory)
        : fullGitDirectory;
    fs.writeFileSync(gitFilePath, `gitdir: ${pointer}\n`);

    return gitFilePath;
}
