import * as assert from 'assert';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import { ensureIsolatedCliArg, isLinkedGitWorktree, resolveIsolated, tryGetLinkedWorktreeRoot } from '../utils/gitWorktree';

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

    test('linked worktree .git file is detected', () => {
        fs.writeFileSync(path.join(root, '.git'), `gitdir: ${path.join(root, '.git', 'worktrees', 'feature')}\n`);
        const appHostPath = path.join(root, 'AppHost', 'AppHost.csproj');

        assert.strictEqual(tryGetLinkedWorktreeRoot(appHostPath), root);
        assert.strictEqual(isLinkedGitWorktree(appHostPath), true);
        assert.strictEqual(resolveIsolated(undefined, appHostPath), true);
        assert.strictEqual(resolveIsolated(false, appHostPath), false);
    });

    test('relative gitdir worktree is detected', () => {
        fs.writeFileSync(path.join(root, '.git'), 'gitdir: ../.git/worktrees/feature\n');

        assert.strictEqual(tryGetLinkedWorktreeRoot(root), root);
    });

    test('submodule .git file is not a linked worktree', () => {
        fs.mkdirSync(path.join(root, '.git'));
        const submoduleRoot = path.join(root, 'extern', 'dep');
        fs.mkdirSync(submoduleRoot, { recursive: true });
        fs.writeFileSync(
            path.join(submoduleRoot, '.git'),
            `gitdir: ${path.join(root, '.git', 'modules', 'dep')}\n`);

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
