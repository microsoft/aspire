import * as assert from 'assert';
import * as vscode from 'vscode';
import {
    getInternalMicrosoftTelemetryIdentity,
    InternalMicrosoftTelemetryProvider,
} from '../utils/internalMicrosoftTelemetry';
import { CommonTelemetryProperties } from '../utils/telemetry';

const microsoftTenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47';

suite('InternalMicrosoftTelemetryProvider tests', () => {
    test('returns canonical identity details for a Microsoft tenant account', () => {
        const identity = getInternalMicrosoftTelemetryIdentity([
            { id: `unique.${microsoftTenantId}`, label: 'User.Name@REDMOND.CORP.MICROSOFT.COM' },
        ]);

        assert.deepStrictEqual(identity, {
            isInternal: true,
            alias: 'user.name',
            domain: 'redmond.corp.microsoft.com',
        });
    });

    test('uses tenant evidence without emitting an unbound external login', () => {
        const identity = getInternalMicrosoftTelemetryIdentity([
            { id: `unique.${microsoftTenantId}`, label: 'external.user@example.com' },
        ]);

        assert.deepStrictEqual(identity, { isInternal: true });
    });

    test('uses tenant evidence without emitting a malformed Microsoft domain', () => {
        for (const domain of [
            'secret path.microsoft.com',
            'secret\npath.microsoft.com',
            '-secret.microsoft.com',
            'secret-.microsoft.com',
            'secret..microsoft.com',
            `${'a'.repeat(64)}.microsoft.com`,
            `${Array(121).fill('a').join('.')}.microsoft.com`,
        ]) {
            const identity = getInternalMicrosoftTelemetryIdentity([
                { id: `unique.${microsoftTenantId}`, label: `user@${domain}` },
            ]);

            assert.deepStrictEqual(identity, { isInternal: true }, domain);
        }
    });

    test('preserves legitimate alias prefixes for one corporate account', () => {
        const identity = getInternalMicrosoftTelemetryIdentity([
            { id: `unique.${microsoftTenantId}`, label: 'Microsoft-User@Microsoft.com' },
        ]);

        assert.deepStrictEqual(identity, {
            isInternal: true,
            alias: 'microsoft-user',
            domain: 'microsoft.com',
        });
    });

    test('ignores a non-Microsoft tenant', () => {
        const identity = getInternalMicrosoftTelemetryIdentity([
            { id: 'unique.external-tenant', label: 'other@microsoft.com' },
        ]);

        assert.deepStrictEqual(identity, { isInternal: false });
    });

    test('omits identity details when multiple Microsoft accounts are returned', () => {
        const identity = getInternalMicrosoftTelemetryIdentity([
            { id: `second.${microsoftTenantId}`, label: 'z.user@microsoft.com' },
            { id: `first.${microsoftTenantId}`, label: 'a.user@microsoft.com' },
        ]);

        assert.deepStrictEqual(identity, { isInternal: true });
    });

    test('omits identity details when a corporate account shares the provider with another account', () => {
        const identity = getInternalMicrosoftTelemetryIdentity([
            { id: `corporate.${microsoftTenantId}`, label: 'corporate.user@microsoft.com' },
            { id: 'personal.consumer-tenant', label: 'personal@example.com' },
        ]);

        assert.deepStrictEqual(identity, { isInternal: true });
    });

    test('publishes account changes to common telemetry properties', async () => {
        const sessionChanges = new vscode.EventEmitter<vscode.AuthenticationSessionsChangeEvent>();
        let accounts: readonly vscode.AuthenticationSessionAccountInformation[] = [
            { id: `first.${microsoftTenantId}`, label: 'first.user@microsoft.com' },
        ];
        const published: CommonTelemetryProperties[] = [];
        const provider = new InternalMicrosoftTelemetryProvider(
            {
                getAccounts: async () => accounts,
                onDidChangeSessions: sessionChanges.event,
            },
            properties => published.push({ ...properties }),
            () => { });

        try {
            await provider.initializeAsync();
            assert.deepStrictEqual(published.at(-1), {
                is_microsoft_internal: 'true',
                microsoft_internal_alias: 'first.user',
                microsoft_internal_domain: 'microsoft.com',
            });

            accounts = [{ id: `second.${microsoftTenantId}`, label: 'second.user@microsoft.com' }];
            sessionChanges.fire({
                provider: { id: 'microsoft', label: 'Microsoft' },
            });

            await waitFor(() => published.at(-1)?.microsoft_internal_alias === 'second.user');
            assert.deepStrictEqual(published.at(-1), {
                is_microsoft_internal: 'true',
                microsoft_internal_alias: 'second.user',
                microsoft_internal_domain: 'microsoft.com',
            });
        }
        finally {
            provider.dispose();
            sessionChanges.dispose();
        }
    });

    test('waits for the Microsoft provider to publish cold-start accounts', async () => {
        const sessionChanges = new vscode.EventEmitter<vscode.AuthenticationSessionsChangeEvent>();
        let accounts: readonly vscode.AuthenticationSessionAccountInformation[] = [];
        const published: CommonTelemetryProperties[] = [];
        const provider = new InternalMicrosoftTelemetryProvider(
            {
                getAccounts: async () => accounts,
                onDidChangeSessions: sessionChanges.event,
            },
            properties => published.push({ ...properties }),
            () => { },
            100);

        try {
            const initialization = provider.initializeAsync();
            await new Promise(resolve => setTimeout(resolve, 0));
            accounts = [{ id: `current.${microsoftTenantId}`, label: 'current.user@microsoft.com' }];
            sessionChanges.fire({
                provider: { id: 'microsoft', label: 'Microsoft' },
            });
            await initialization;

            assert.deepStrictEqual(published.at(-1), {
                is_microsoft_internal: 'true',
                microsoft_internal_alias: 'current.user',
                microsoft_internal_domain: 'microsoft.com',
            });
        }
        finally {
            provider.dispose();
            sessionChanges.dispose();
        }
    });

    test('publishes a safe negative result when account enumeration fails', async () => {
        const warnings: string[] = [];
        const published: CommonTelemetryProperties[] = [];
        const provider = new InternalMicrosoftTelemetryProvider(
            {
                getAccounts: async () => {
                    throw new Error('sensitive failure');
                },
                onDidChangeSessions: () => ({ dispose() { } }),
            },
            properties => published.push({ ...properties }),
            message => warnings.push(message),
            10);

        try {
            await provider.initializeAsync();

            assert.deepStrictEqual(published.at(-1), {
                is_microsoft_internal: 'false',
                microsoft_internal_alias: undefined,
                microsoft_internal_domain: undefined,
            });
            assert.deepStrictEqual(warnings, ['Unable to query VS Code Microsoft accounts for telemetry enrichment.']);
        }
        finally {
            provider.dispose();
        }
    });

    test('bounds initialization when account enumeration stalls', async () => {
        const published: CommonTelemetryProperties[] = [];
        const provider = new InternalMicrosoftTelemetryProvider(
            {
                getAccounts: () => new Promise(() => { }),
                onDidChangeSessions: () => ({ dispose() { } }),
            },
            properties => published.push({ ...properties }),
            () => { },
            10);

        try {
            await provider.initializeAsync();

            assert.deepStrictEqual(published.at(-1), {
                is_microsoft_internal: 'false',
                microsoft_internal_alias: undefined,
                microsoft_internal_domain: undefined,
            });
        }
        finally {
            provider.dispose();
        }
    });

    test('does not access accounts while disabled and refreshes when re-enabled', async () => {
        const sessionChanges = new vscode.EventEmitter<vscode.AuthenticationSessionsChangeEvent>();
        let accountQueries = 0;
        let accounts: readonly vscode.AuthenticationSessionAccountInformation[] = [
            { id: `first.${microsoftTenantId}`, label: 'first.user@microsoft.com' },
        ];
        const published: CommonTelemetryProperties[] = [];
        const provider = new InternalMicrosoftTelemetryProvider(
            {
                getAccounts: async () => {
                    accountQueries++;
                    return accounts;
                },
                onDidChangeSessions: sessionChanges.event,
            },
            properties => published.push({ ...properties }),
            () => { },
            10);

        try {
            provider.disable();
            sessionChanges.fire({ provider: { id: 'microsoft', label: 'Microsoft' } });
            await new Promise(resolve => setTimeout(resolve, 0));
            assert.strictEqual(accountQueries, 0);

            await provider.initializeAsync();
            assert.strictEqual(accountQueries, 1);
            assert.strictEqual(published.at(-1)?.microsoft_internal_alias, 'first.user');

            provider.disable();
            accounts = [{ id: `second.${microsoftTenantId}`, label: 'second.user@microsoft.com' }];
            sessionChanges.fire({ provider: { id: 'microsoft', label: 'Microsoft' } });
            await new Promise(resolve => setTimeout(resolve, 0));
            assert.strictEqual(accountQueries, 1);
            assert.deepStrictEqual(published.at(-1), {
                is_microsoft_internal: undefined,
                microsoft_internal_alias: undefined,
                microsoft_internal_domain: undefined,
            });

            await provider.initializeAsync();
            assert.strictEqual(accountQueries, 2);
            assert.strictEqual(published.at(-1)?.microsoft_internal_alias, 'second.user');
        }
        finally {
            provider.dispose();
            sessionChanges.dispose();
        }
    });

    test('does not publish an account query that completes after telemetry is disabled', async () => {
        let resolveAccounts: (accounts: readonly vscode.AuthenticationSessionAccountInformation[]) => void = () => { };
        const accounts = new Promise<readonly vscode.AuthenticationSessionAccountInformation[]>(resolve => {
            resolveAccounts = resolve;
        });
        const published: CommonTelemetryProperties[] = [];
        const provider = new InternalMicrosoftTelemetryProvider(
            {
                getAccounts: () => accounts,
                onDidChangeSessions: () => ({ dispose() { } }),
            },
            properties => published.push({ ...properties }),
            () => { },
            10);

        try {
            const initialization = provider.initializeAsync();
            provider.disable();
            resolveAccounts([{ id: `current.${microsoftTenantId}`, label: 'current.user@microsoft.com' }]);
            await initialization;

            assert.deepStrictEqual(published.at(-1), {
                is_microsoft_internal: undefined,
                microsoft_internal_alias: undefined,
                microsoft_internal_domain: undefined,
            });
        }
        finally {
            provider.dispose();
        }
    });

});

async function waitFor(predicate: () => boolean): Promise<void> {
    for (let attempt = 0; attempt < 20; attempt++) {
        if (predicate()) {
            return;
        }

        await new Promise(resolve => setTimeout(resolve, 0));
    }

    assert.fail('Condition was not satisfied.');
}
