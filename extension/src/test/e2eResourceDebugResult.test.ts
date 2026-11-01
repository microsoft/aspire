import * as assert from 'assert';
import { assertSafeResourceDebugResult, type ResourceDebugToolResult } from '../test-e2e/helpers/resourceDebugResult';

suite('E2E resource debug result privacy assertion', () => {
    function createResult(overrides: Partial<ResourceDebugToolResult> = {}): ResourceDebugToolResult {
        return {
            tool: 'aspire_resource_debug',
            success: true,
            outcome: 'started',
            appHost: 'AspireE2E.AppHost/AspireE2E.AppHost.csproj',
            resourceName: 'e2e-go-gykscndf',
            requestedStrategy: 'auto',
            effectiveStrategy: 'attach',
            controller: 'editor',
            provider: 'go',
            ...overrides,
        };
    }

    test('accepts a requested resource name whose generated suffix contains a sensitive term', () => {
        // DCP appends a random lowercase suffix to resource names; this one was observed on CI and contains "env".
        const result = createResult({ resourceName: 'e2e-go-ypduenvb' });

        assertSafeResourceDebugResult(result, 'e2e-go-ypduenvb');
    });

    test('accepts the empty resource name reported for invalid input', () => {
        const result = createResult({ success: false, outcome: 'invalidInput', appHost: '', resourceName: '', effectiveStrategy: 'none', controller: 'none', provider: undefined });
        delete result.provider;

        assertSafeResourceDebugResult(result, '');
    });

    test('rejects a resource name that differs from the requested resource', () => {
        const result = createResult({ resourceName: 'e2e-go-ypduenvb' });

        assert.throws(() => assertSafeResourceDebugResult(result, 'e2e-go-gykscndf'), assert.AssertionError);
    });

    test('rejects sensitive terms outside the requested resource name', () => {
        const cases: Array<Partial<ResourceDebugToolResult>> = [
            { outcome: 'processExited' },
            { appHost: 'apps/environment/AppHost.csproj' },
            { appHost: 'https://example.test/AppHost.csproj' },
            { debuggerExtensions: [{ id: 'golang.go', label: '--args' }] },
            { debuggerExtensions: [{ id: 'golang.go', label: '/tmp/dlv' }] },
        ];

        for (const overrides of cases) {
            const result = createResult(overrides);
            assert.throws(() => assertSafeResourceDebugResult(result, result.resourceName), assert.AssertionError, JSON.stringify(overrides));
        }
    });

    test('rejects an absolute AppHost path', () => {
        const result = createResult({ appHost: process.platform === 'win32' ? 'C:\\repo\\AppHost.csproj' : '/repo/AppHost.csproj' });

        assert.throws(() => assertSafeResourceDebugResult(result, result.resourceName), assert.AssertionError);
    });
});
