import * as assert from 'assert';
import { determineVSCodeServerReadyAction } from '../debugger/vscode/serverReadyAction';

suite('VS Code Server Ready Action Tests', () => {
    suite('determineVSCodeServerReadyAction', () => {
        const assertLiteralUriServerReadyAction = (
            result: ReturnType<typeof determineVSCodeServerReadyAction>,
            expectedUriFormat: string
        ): void => {
            assert.notStrictEqual(result, undefined);
            assert.ok(result);
            assert.ok('uriFormat' in result);
            assert.strictEqual(result.uriFormat, expectedUriFormat);
            assert.strictEqual(result.pattern, `\\bNow listening on:\\s+${expectedUriFormat}`);
        };

        const assertUriServerReadyAction = (
            result: ReturnType<typeof determineVSCodeServerReadyAction>,
            expectedUriFormat: string
        ): void => {
            assert.notStrictEqual(result, undefined);
            assert.ok(result);
            assert.ok('uriFormat' in result);
            assert.strictEqual(result.uriFormat, expectedUriFormat);
            assert.strictEqual(result.pattern, '\\bNow listening on:\\s+(https?://\\S+)');
        };

        test('returns undefined when launchBrowser is false', () => {
            const result = determineVSCodeServerReadyAction(false, 'https://localhost:5001');
            assert.strictEqual(result, undefined);
        });

        test('returns undefined when applicationUrl is undefined and no launch config serverReadyAction', () => {
            const result = determineVSCodeServerReadyAction(true, undefined, undefined);
            assert.strictEqual(result, undefined);
        });

        test('returns existing when launchBrowser is undefined, applicationUrl is undefined and existing launch config serverReadyAction', () => {
            const result = determineVSCodeServerReadyAction(undefined, undefined, { action: 'openExternally', uriFormat: 'https://localhost:5001', pattern: '\\bNow listening on:\\s+(https?://\\S+)' });
            assert.strictEqual(result?.action, 'openExternally');
            assertUriServerReadyAction(result, 'https://localhost:5001');
        });

        test('returns existing when launchBrowser is true, applicationUrl is undefined and existing launch config serverReadyAction', () => {
            const result = determineVSCodeServerReadyAction(true, undefined, { action: 'openExternally', uriFormat: 'https://localhost:5001', pattern: '\\bNow listening on:\\s+(https?://\\S+)' });
            assert.strictEqual(result?.action, 'openExternally');
            assertUriServerReadyAction(result, 'https://localhost:5001');
        });

        test('returns existing browser debugger serverReadyAction when provided', () => {
            const existing = {
                action: 'debugWithEdge' as const,
                pattern: '\\bNow listening on:\\s+(https?://\\S+)',
                uriFormat: 'https://localhost:5001',
                webRoot: '/client'
            };

            const result = determineVSCodeServerReadyAction(true, undefined, existing);

            assert.deepStrictEqual(result, existing);
        });

        test('returns existing custom serverReadyAction when provided', () => {
            const existing = {
                action: 'someFutureAction',
                pattern: 'listening on port ([0-9]+)',
                killOnServerStop: true
            };

            const result = determineVSCodeServerReadyAction(true, undefined, existing);

            assert.deepStrictEqual(result, existing);
        });

        test('returns undefined when launchBrowser is false, applicationUrl is undefined and existing launch config serverReadyAction', () => {
            const result = determineVSCodeServerReadyAction(false, undefined, { action: 'openExternally', uriFormat: 'https://localhost:5001', pattern: '\\bNow listening on:\\s+(https?://\\S+)' });
            assert.strictEqual(result, undefined);
        });

        test('returns undefined when launchBrowser is false, applicationUrl is not undefined and existing launch config serverReadyAction', () => {
            const result = determineVSCodeServerReadyAction(false, 'https://localhost:5001', { action: 'openExternally', uriFormat: 'https://localhost:5001', pattern: '\\bNow listening on:\\s+(https?://\\S+)' });
            assert.strictEqual(result, undefined);
        });

        test('returns serverReadyAction when launchBrowser true and applicationUrl provided', () => {
            const applicationUrl = 'https://localhost:5001';
            const result = determineVSCodeServerReadyAction(true, applicationUrl);

            assert.strictEqual(result?.action, 'openExternally');
            assertLiteralUriServerReadyAction(result, applicationUrl);
        });

        test('returns placeholder-based serverReadyAction when multiple URLs are separated by semicolon', () => {
            const applicationUrl = 'https://localhost:5001;http://localhost:5000';
            const result = determineVSCodeServerReadyAction(true, applicationUrl);

            assert.strictEqual(result?.action, 'openExternally');
            assertUriServerReadyAction(result, '%s');
        });
    });
});
