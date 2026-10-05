import * as assert from 'assert';
import * as sinon from 'sinon';
import { assertExactLinkedAppHostCliLaunch, assertLinkedAppHostCliLaunch, getExpectedLinkedAppHostCliProcessArguments } from './helpers/processArguments';

suite('process argument parsing', () => {
    const cliPath = '/tools/aspire';
    const appHostPath = '/workspace/Linked Worktree/LinkedAppHost.csproj';

    teardown(() => {
        sinon.restore();
    });

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

    test('ignores option-shaped AppHost arguments after the separator', () => {
        assert.doesNotThrow(() => assertLinkedAppHostCliLaunch(
            [
                cliPath,
                'run',
                '--isolated',
                '--start-debug-session',
                '--apphost',
                appHostPath,
                '--',
                '--isolated=false',
                '--start-debug-session',
                '--apphost',
            ],
            appHostPath,
            cliPath,
            'linux'));
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

    test('accepts the exact linked AppHost CLI argv including application argument boundaries', () => {
        const appHostArguments = ['--custom', 'value with spaces', '', 'literal "quote"', String.raw`C:\tools\backslash\path`];

        assert.doesNotThrow(() => assertExactLinkedAppHostCliLaunch(
            [
                'C:\\Tools\\aspire.exe',
                'run',
                '--isolated',
                '--start-debug-session',
                '--nologo',
                '--apphost',
                'c:\\Users\\runner\\workspace with spaces\\AppHost.csproj',
                '--',
                ...appHostArguments,
            ],
            'C:\\Users\\runner\\workspace with spaces\\AppHost.csproj',
            'C:\\Tools\\ASPIRE.EXE',
            appHostArguments,
            'win32'));
    });

    test('builds the exact cmd shim process argv for a linked AppHost launch', () => {
        sinon.stub(process, 'platform').value('win32');
        const originalComSpec = process.env.ComSpec;
        process.env.ComSpec = 'C:\\Windows\\System32\\cmd.exe';

        try {
            const result = getExpectedLinkedAppHostCliProcessArguments(
                'C:\\Tools\\Aspire CLI\\aspire.cmd',
                'C:\\worktrees\\linked apphost\\AppHost.csproj',
                ['--custom', 'value with spaces', '', 'literal "quote"'],
            );

            assert.deepStrictEqual(result, [
                'C:\\Windows\\System32\\cmd.exe',
                '/d',
                '/v:off',
                '/s',
                '/c',
                '""C:\\Tools\\Aspire CLI\\aspire.cmd" "run" "--isolated" "--start-debug-session" "--nologo" "--apphost" "C:\\worktrees\\linked apphost\\AppHost.csproj" "--" "--custom" "value with spaces" "" "literal ""quote""""',
            ]);
            assert.doesNotThrow(() => assertExactLinkedAppHostCliLaunch(
                result,
                'C:\\worktrees\\linked apphost\\AppHost.csproj',
                'C:\\Tools\\Aspire CLI\\aspire.cmd',
                ['--custom', 'value with spaces', '', 'literal "quote"']));
        }
        finally {
            if (originalComSpec === undefined) {
                delete process.env.ComSpec;
            }
            else {
                process.env.ComSpec = originalComSpec;
            }
        }
    });

    test('rejects a repeated resolver result that duplicates the root isolation switch', () => {
        assert.throws(
            () => assertExactLinkedAppHostCliLaunch(
                [
                    cliPath,
                    'run',
                    '--isolated',
                    '--isolated',
                    '--start-debug-session',
                    '--nologo',
                    '--apphost',
                    appHostPath,
                    '--',
                    '--custom',
                ],
                appHostPath,
                cliPath,
                ['--custom'],
                'linux'),
            /Expected exact Aspire CLI argv/);
    });

    test('rejects flattened or changed AppHost argument tokens', () => {
        assert.throws(
            () => assertExactLinkedAppHostCliLaunch(
                [
                    cliPath,
                    'run',
                    '--isolated',
                    '--start-debug-session',
                    '--nologo',
                    '--apphost',
                    appHostPath,
                    '--',
                    '--custom',
                    'value with spaces  literal "quote"',
                    String.raw`C:\tools\backslash\path`,
                ],
                appHostPath,
                cliPath,
                ['--custom', 'value with spaces', '', 'literal "quote"', String.raw`C:\tools\backslash\path`],
                'linux'),
            /Expected exact Aspire CLI argv/);
    });
});
