import * as assert from 'assert';
import { assertLinkedAppHostCliLaunch } from './helpers/processArguments';

suite('process argument parsing', () => {
    const cliPath = '/tools/aspire';
    const appHostPath = '/workspace/Linked Worktree/LinkedAppHost.csproj';

    test('accepts exact Windows launch arguments and compares paths case-insensitively', () => {
        assert.doesNotThrow(() => assertLinkedAppHostCliLaunch(
            ['C:\\Tools\\aspire.exe', 'run', '--isolated', '--start-debug-session', '--apphost', 'c:\\Users\\runner\\workspace with spaces\\AppHost.csproj'],
            'C:\\Users\\runner\\workspace with spaces\\AppHost.csproj',
            'C:\\Tools\\ASPIRE.EXE',
            'win32'));
    });

    test('rejects --isolated=false as evidence of inferred isolation', () => {
        assert.throws(
            () => assertLinkedAppHostCliLaunch(
                [cliPath, 'run', '--isolated=false', '--start-debug-session', '--apphost', appHostPath],
                appHostPath,
                cliPath,
                'linux'),
            /Expected exact '--isolated'/);
    });

    test('rejects false immediately after --isolated', () => {
        assert.throws(
            () => assertLinkedAppHostCliLaunch(
                [cliPath, 'run', '--isolated', 'false', '--start-debug-session', '--apphost', appHostPath],
                appHostPath,
                cliPath,
                'linux'),
            /Expected inferred isolation to use only the true-form --isolated switch/);
    });

    test('rejects --start-debug-session embedded in another argument', () => {
        assert.throws(
            () => assertLinkedAppHostCliLaunch(
                [cliPath, 'run', '--isolated', '--prefix--start-debug-session', '--apphost', appHostPath],
                appHostPath,
                cliPath,
                'linux'),
            /Expected exact '--start-debug-session'/);
    });

    test('rejects --apphost embedded in another argument', () => {
        assert.throws(
            () => assertLinkedAppHostCliLaunch(
                [cliPath, 'run', '--isolated', '--start-debug-session', '--prefix--apphost', appHostPath],
                appHostPath,
                cliPath,
                'linux'),
            /Expected exact '--apphost'/);
    });

    test('rejects an AppHost path embedded in another argument', () => {
        assert.throws(
            () => assertLinkedAppHostCliLaunch(
                [cliPath, 'run', '--isolated', '--start-debug-session', '--apphost', `${appHostPath}.backup`],
                appHostPath,
                cliPath,
                'linux'),
            /Expected exact --apphost path/);
    });

    test('requires the AppHost path immediately after --apphost', () => {
        assert.throws(
            () => assertLinkedAppHostCliLaunch(
                [cliPath, 'run', '--isolated', '--start-debug-session', '--apphost', '--other', appHostPath],
                appHostPath,
                cliPath,
                'linux'),
            /Expected exact --apphost path/);
    });
});
