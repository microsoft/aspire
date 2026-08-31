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

    test('ignores non-Microsoft tenants and preserves legitimate alias prefixes', () => {
        const identity = getInternalMicrosoftTelemetryIdentity([
            { id: 'unique.external-tenant', label: 'other@microsoft.com' },
            { id: `unique.${microsoftTenantId}`, label: 'Microsoft-User@Microsoft.com' },
        ]);

        assert.deepStrictEqual(identity, {
            isInternal: true,
            alias: 'microsoft-user',
            domain: 'microsoft.com',
        });
    });

    test('selects the same identity regardless of account ordering', () => {
        const accounts = [
            { id: `second.${microsoftTenantId}`, label: 'z.user@microsoft.com' },
            { id: `first.${microsoftTenantId}`, label: 'a.user@microsoft.com' },
        ];

        const forward = getInternalMicrosoftTelemetryIdentity(accounts);
        const reverse = getInternalMicrosoftTelemetryIdentity([...accounts].reverse());

        assert.deepStrictEqual(forward, reverse);
        assert.strictEqual(forward.alias, 'a.user');
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
                is_microsoft_internal: 'false',
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
