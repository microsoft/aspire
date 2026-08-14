import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import { ensureIsolatedCliArg, isLinkedGitWorktree, resolveIsolated, tryGetLinkedWorktreeRoot } from '../utils/gitWorktree';
import { writeGitDirFile, writeLinkedWorktreeMetadata } from './testGitWorktree';

suite('gitWorktree', () => {
    let root: string;

    setup(() => {
        root = fs.mkdtempSync(path.join(os.tmpdir(), 'aspire-git-worktree-'));
    });

    teardown(() => {
        fs.rmSync(root, { recursive: true, force: true });
    });

    test('primary checkout is not a linked worktree', () => {
        fs.mkdirSync(path.join(root, '.git'));
        const appHostPath = path.join(root, 'AppHost', 'AppHost.csproj');

        assert.strictEqual(tryGetLinkedWorktreeRoot(appHostPath), undefined);
        assert.strictEqual(isLinkedGitWorktree(root), false);
        assert.strictEqual(resolveIsolated(undefined, appHostPath), false);
        assert.strictEqual(resolveIsolated(true, appHostPath), true);
    });

    test('standard common Git directory is detected', () => {
        const worktreeRoot = path.join(root, 'worktree');
        writeLinkedWorktreeMetadata(worktreeRoot, path.join(root, 'primary', '.git'));
        const appHostPath = path.join(worktreeRoot, 'AppHost', 'AppHost.csproj');

        assert.strictEqual(tryGetLinkedWorktreeRoot(appHostPath), worktreeRoot);
        assert.strictEqual(isLinkedGitWorktree(appHostPath), true);
        assert.strictEqual(resolveIsolated(undefined, appHostPath), true);
        assert.strictEqual(resolveIsolated(false, appHostPath), false);
    });

    for (const commonGitDirectoryName of ['repo.git', 'separate-git']) {
        test(`${commonGitDirectoryName} common Git directory is detected`, () => {
            const worktreeRoot = path.join(root, 'worktree');
            writeLinkedWorktreeMetadata(worktreeRoot, path.join(root, commonGitDirectoryName));

            assert.strictEqual(tryGetLinkedWorktreeRoot(worktreeRoot), worktreeRoot);
        });
    }

    test('relative gitdir worktree is detected', () => {
        writeLinkedWorktreeMetadata(root, path.join(root, 'primary', '.git'), 'feature', true);

        assert.strictEqual(tryGetLinkedWorktreeRoot(root), root);
    });

    test('submodule inside a linked worktree is not detected', () => {
        const worktreeRoot = path.join(root, 'worktree');
        const adminDirectory = writeLinkedWorktreeMetadata(worktreeRoot, path.join(root, 'primary', '.git'));
        const submoduleRoot = path.join(worktreeRoot, 'extern', 'dep');
        writeGitDirFile(submoduleRoot, path.join(adminDirectory, 'modules', 'dep'));

        assert.strictEqual(tryGetLinkedWorktreeRoot(path.join(submoduleRoot, 'AppHost.csproj')), undefined);
    });

    test('decoy worktree pointer without a back-pointer is not detected', () => {
        const worktreeRoot = path.join(root, 'worktree');
        writeGitDirFile(worktreeRoot, path.join(root, 'primary', '.git', 'worktrees', 'stale'));

        assert.strictEqual(tryGetLinkedWorktreeRoot(worktreeRoot), undefined);
    });

    test('submodule .git file is not a linked worktree', () => {
        fs.mkdirSync(path.join(root, '.git'));
        const submoduleRoot = path.join(root, 'extern', 'dep');
        fs.mkdirSync(submoduleRoot, { recursive: true });
        writeGitDirFile(submoduleRoot, path.join(root, '.git', 'modules', 'dep'));

        assert.strictEqual(tryGetLinkedWorktreeRoot(path.join(submoduleRoot, 'AppHost.csproj')), undefined);
        assert.strictEqual(resolveIsolated(undefined, submoduleRoot), false);
    });

    test('ensureIsolatedCliArg leaves args unchanged when isolation is unspecified', () => {
        assert.deepStrictEqual(ensureIsolatedCliArg(undefined, undefined), undefined);
        assert.deepStrictEqual(ensureIsolatedCliArg(['--no-build'], undefined), ['--no-build']);
    });

    test('ensureIsolatedCliArg inserts the isolation value before --', () => {
        assert.deepStrictEqual(ensureIsolatedCliArg(undefined, false), ['--isolated', 'false']);
        assert.deepStrictEqual(ensureIsolatedCliArg(undefined, true), ['--isolated']);
        assert.deepStrictEqual(ensureIsolatedCliArg(['--no-build'], true), ['--no-build', '--isolated']);
        assert.deepStrictEqual(
            ensureIsolatedCliArg(['--no-build', '--', '--custom'], true),
            ['--no-build', '--isolated', '--', '--custom']);
        assert.deepStrictEqual(
            ensureIsolatedCliArg(['--no-build', '--', '--custom'], false),
            ['--no-build', '--isolated', 'false', '--', '--custom']);
    });

    test('ensureIsolatedCliArg does not duplicate an existing isolation option', () => {
        assert.deepStrictEqual(
            ensureIsolatedCliArg(['--isolated', '--no-build'], true),
            ['--isolated', '--no-build']);
        assert.deepStrictEqual(
            ensureIsolatedCliArg(['--isolated', 'false', '--no-build'], true),
            ['--isolated', 'false', '--no-build']);
        assert.deepStrictEqual(
            ensureIsolatedCliArg(['--isolated=false', '--no-build'], true),
            ['--isolated=false', '--no-build']);
    });
});
