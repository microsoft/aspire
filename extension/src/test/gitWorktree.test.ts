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

        assert.strictEqual(tryGetLinkedWorktreeRoot(appHostPath), fs.realpathSync.native(worktreeRoot));
        assert.strictEqual(isLinkedGitWorktree(appHostPath), true);
        assert.strictEqual(resolveIsolated(undefined, appHostPath), true);
        assert.strictEqual(resolveIsolated(false, appHostPath), false);
    });

    for (const commonGitDirectoryName of ['repo.git', 'separate-git']) {
        test(`${commonGitDirectoryName} common Git directory is detected`, () => {
            const worktreeRoot = path.join(root, 'worktree');
            writeLinkedWorktreeMetadata(worktreeRoot, path.join(root, commonGitDirectoryName));

            assert.strictEqual(tryGetLinkedWorktreeRoot(worktreeRoot), fs.realpathSync.native(worktreeRoot));
        });
    }

    test('relative gitdir worktree is detected', () => {
        writeLinkedWorktreeMetadata(root, path.join(root, 'primary', '.git'), 'feature', true);

        assert.strictEqual(tryGetLinkedWorktreeRoot(root), fs.realpathSync.native(root));
    });

    test('checkout alias resolves relative metadata from the canonical checkout', function () {
        const worktreeRoot = path.join(root, 'physical', 'checkouts', 'feature');
        writeLinkedWorktreeMetadata(
            worktreeRoot,
            path.join(root, 'physical', 'primary', '.git'),
            'feature',
            true);
        const aliasRoot = path.join(root, 'aliases', 'feature');
        fs.mkdirSync(path.dirname(aliasRoot), { recursive: true });
        try {
            fs.symlinkSync(worktreeRoot, aliasRoot, process.platform === 'win32' ? 'junction' : 'dir');
        }
        catch {
            this.skip();
            return;
        }

        assert.strictEqual(tryGetLinkedWorktreeRoot(aliasRoot), fs.realpathSync.native(worktreeRoot));
    });

    test('admin alias resolves relative back-pointer from the canonical admin directory', function () {
        const worktreeRoot = path.join(root, 'checkout');
        const adminDirectory = path.join(root, 'physical', 'primary', '.git', 'worktrees', 'feature');
        fs.mkdirSync(adminDirectory, { recursive: true });
        const aliasDirectory = path.join(root, 'aliases', 'admin');
        fs.mkdirSync(path.dirname(aliasDirectory), { recursive: true });
        try {
            fs.symlinkSync(adminDirectory, aliasDirectory, process.platform === 'win32' ? 'junction' : 'dir');
        }
        catch {
            this.skip();
            return;
        }

        const gitFilePath = writeGitDirFile(worktreeRoot, aliasDirectory);
        fs.writeFileSync(
            path.join(adminDirectory, 'gitdir'),
            `${path.relative(adminDirectory, gitFilePath)}\n`);

        assert.strictEqual(tryGetLinkedWorktreeRoot(worktreeRoot), fs.realpathSync.native(worktreeRoot));
    });

    test('gitdir with a trailing directory separator is detected', () => {
        const adminDirectory = writeLinkedWorktreeMetadata(root, path.join(root, 'primary', '.git'));
        fs.writeFileSync(path.join(root, '.git'), `gitdir: ${adminDirectory}${path.sep}\n`);

        assert.strictEqual(tryGetLinkedWorktreeRoot(root), fs.realpathSync.native(root));
    });

    test('uppercase WORKTREES admin directory uses platform casing', () => {
        const worktreeRoot = path.join(root, 'worktree');
        const adminDirectory = path.join(root, 'primary', '.git', 'WORKTREES', 'feature');
        const gitFilePath = writeGitDirFile(worktreeRoot, adminDirectory);
        fs.writeFileSync(path.join(adminDirectory, 'gitdir'), `${gitFilePath}\n`);

        assert.strictEqual(
            tryGetLinkedWorktreeRoot(worktreeRoot),
            process.platform === 'win32' ? fs.realpathSync.native(worktreeRoot) : undefined);
    });

    test('case-variant back-pointer uses filesystem identity', () => {
        const adminDirectory = writeLinkedWorktreeMetadata(root, path.join(root, 'primary', '.git'));
        const gitFilePath = path.join(root, '.git');
        const caseVariantGitFile = path.join(root, '.GIT');
        fs.writeFileSync(path.join(adminDirectory, 'gitdir'), `${caseVariantGitFile}\n`);
        const caseVariantIsGitFile = fs.existsSync(caseVariantGitFile)
            && fs.realpathSync.native(caseVariantGitFile) === fs.realpathSync.native(gitFilePath);

        assert.strictEqual(
            tryGetLinkedWorktreeRoot(root),
            caseVariantIsGitFile ? fs.realpathSync.native(root) : undefined);
    });

    test('symlink alias back-pointer is detected', function () {
        const worktreeRoot = path.join(root, 'worktree');
        const adminDirectory = writeLinkedWorktreeMetadata(worktreeRoot, path.join(root, 'primary', '.git'));
        const aliasRoot = path.join(root, 'worktree-alias');
        try {
            fs.symlinkSync(worktreeRoot, aliasRoot, process.platform === 'win32' ? 'junction' : 'dir');
        }
        catch {
            this.skip();
            return;
        }

        fs.writeFileSync(path.join(adminDirectory, 'gitdir'), `${path.join(aliasRoot, '.git')}\n`);

        assert.strictEqual(tryGetLinkedWorktreeRoot(worktreeRoot), fs.realpathSync.native(worktreeRoot));
    });

    test('submodule inside a linked worktree is not detected', () => {
        const worktreeRoot = path.join(root, 'worktree');
        const adminDirectory = writeLinkedWorktreeMetadata(worktreeRoot, path.join(root, 'primary', '.git'));
        const submoduleRoot = path.join(worktreeRoot, 'extern', 'dep');
        writeGitDirFile(submoduleRoot, path.join(adminDirectory, 'modules', 'dep'));

        assert.strictEqual(tryGetLinkedWorktreeRoot(path.join(submoduleRoot, 'AppHost.csproj')), undefined);
    });

    test('reciprocal back-pointer outside worktrees is not detected', () => {
        const worktreeRoot = path.join(root, 'worktree');
        const adminDirectory = path.join(root, 'primary', '.git', 'modules', 'dep');
        const gitFilePath = writeGitDirFile(worktreeRoot, adminDirectory);
        fs.writeFileSync(path.join(adminDirectory, 'gitdir'), `${gitFilePath}\n`);

        assert.strictEqual(tryGetLinkedWorktreeRoot(worktreeRoot), undefined);
    });

    test('back-pointer to a different checkout is not detected', () => {
        const commonGitDirectory = path.join(root, 'primary', '.git');
        const worktreeRoot = path.join(root, 'worktree');
        const adminDirectory = writeLinkedWorktreeMetadata(worktreeRoot, commonGitDirectory);
        const otherWorktreeRoot = path.join(root, 'other-worktree');
        const otherGitFile = writeGitDirFile(
            otherWorktreeRoot,
            path.join(commonGitDirectory, 'worktrees', 'other'));
        fs.writeFileSync(path.join(adminDirectory, 'gitdir'), `${otherGitFile}\n`);

        assert.strictEqual(tryGetLinkedWorktreeRoot(worktreeRoot), undefined);
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
