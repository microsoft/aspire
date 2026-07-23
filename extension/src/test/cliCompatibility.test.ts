import * as assert from 'assert';
import {
    containsQuotedCliToken,
    hasRootNoLogoOption,
    isNoLogoUnsupportedOutput,
    noLogoOption,
    removeRootNoLogoOption,
} from '../utils/cliCompatibility';

suite('utils/cliCompatibility tests', () => {

    suite('hasRootNoLogoOption', () => {
        test('returns true when --nologo is a root option', () => {
            assert.strictEqual(hasRootNoLogoOption(['ls', '--format', 'json', noLogoOption]), true);
        });

        test('returns false when no --nologo is present', () => {
            assert.strictEqual(hasRootNoLogoOption(['ls', '--format', 'json']), false);
        });

        test('returns false when --nologo only appears after the -- delimiter', () => {
            // After `--`, args belong to the user-supplied resource command and must not be
            // treated as root options.
            assert.strictEqual(hasRootNoLogoOption(['resource', 'cache', 'flush', '--', noLogoOption]), false);
        });

        test('returns true when --nologo precedes a -- delimiter that also has --nologo after it', () => {
            assert.strictEqual(hasRootNoLogoOption(['ls', noLogoOption, '--', '--something', noLogoOption]), true);
        });
    });

    suite('removeRootNoLogoOption', () => {
        test('removes --nologo from root args only', () => {
            assert.deepStrictEqual(removeRootNoLogoOption(['ls', noLogoOption, '--format', 'json']), ['ls', '--format', 'json']);
        });

        test('preserves --nologo that appears after the -- delimiter', () => {
            const args = ['resource', 'cache', 'flush', '--', noLogoOption, '--verbose'];
            assert.deepStrictEqual(removeRootNoLogoOption(args), ['resource', 'cache', 'flush', '--', noLogoOption, '--verbose']);
        });

        test('removes only the root --nologo and leaves a post-delimiter --nologo intact', () => {
            const args = ['ls', noLogoOption, '--format', 'json', '--', noLogoOption];
            assert.deepStrictEqual(removeRootNoLogoOption(args), ['ls', '--format', 'json', '--', noLogoOption]);
        });

        test('returns a shallow copy when no root --nologo is present', () => {
            const args = ['ls', '--format', 'json'];
            const result = removeRootNoLogoOption(args);
            assert.deepStrictEqual(result, args);
            assert.notStrictEqual(result, args, 'Result should be a copy, not the original array');
        });
    });

    suite('isNoLogoUnsupportedOutput', () => {
        test('returns true for System.CommandLine unrecognized command-or-argument error', () => {
            const stderr = `Unrecognized command or argument '${noLogoOption}'.`;
            assert.strictEqual(isNoLogoUnsupportedOutput(['ls', noLogoOption], '', stderr), true);
        });

        test('returns true for System.CommandLine unrecognized-option error', () => {
            const stderr = `Unrecognized option '${noLogoOption}'.`;
            assert.strictEqual(isNoLogoUnsupportedOutput(['ls', noLogoOption], '', stderr), true);
        });

        test('returns true for localized unsupported-option output that mentions the token', () => {
            const stderr = `No se encuentra el recurso '${noLogoOption}'.`;
            assert.strictEqual(isNoLogoUnsupportedOutput(['ls', noLogoOption], '', stderr), true);
        });

        test('returns false when args do not contain a root --nologo even if stderr says so', () => {
            // If we never passed --nologo at the root, an unrecognized-option message that
            // happens to reference --nologo is not ours to retry.
            const stderr = `Unrecognized command or argument '${noLogoOption}'.`;
            assert.strictEqual(isNoLogoUnsupportedOutput(['ls', '--format', 'json'], '', stderr), false);
        });

        test('returns false when --nologo only appears after the -- delimiter', () => {
            const stderr = `Unrecognized command or argument '${noLogoOption}'.`;
            const args = ['resource', 'cache', 'flush', '--', noLogoOption];
            assert.strictEqual(isNoLogoUnsupportedOutput(args, '', stderr), false);
        });

        test('matches the error message on stdout (some CLIs route errors there)', () => {
            const stdout = `Unrecognized option '${noLogoOption}'.`;
            assert.strictEqual(isNoLogoUnsupportedOutput(['ls', noLogoOption], stdout, ''), true);
        });
    });

    suite('containsQuotedCliToken', () => {
        test('matches CLI tokens surrounded by straight or curly quotes', () => {
            assert.strictEqual(containsQuotedCliToken("Unrecognized command or argument '--stream'.", '--stream'), true);
            assert.strictEqual(containsQuotedCliToken('Unrecognized command or argument "--stream".', '--stream'), true);
            assert.strictEqual(containsQuotedCliToken('Unrecognized command or argument \u2018--stream\u2019.', '--stream'), true);
            assert.strictEqual(containsQuotedCliToken('Unrecognized command or argument \u201C--stream\u201D.', '--stream'), true);
        });

        test('does not match unquoted tokens or quoted prefixes', () => {
            assert.strictEqual(containsQuotedCliToken('Unrecognized command or argument --stream.', '--stream'), false);
            assert.strictEqual(containsQuotedCliToken("Unrecognized command or argument '--streaming'.", '--stream'), false);
        });
    });
});
