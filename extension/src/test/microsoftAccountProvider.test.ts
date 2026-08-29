import * as assert from 'assert';
import * as vscode from 'vscode';
import { getInternalMicrosoftAlias, MicrosoftAccountProvider } from '../utils/microsoftAccountProvider';

const microsoftTenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47';

suite('MicrosoftAccountProvider tests', () => {
    test('selects a normalized alias from a Microsoft tenant account', () => {
        const alias = getInternalMicrosoftAlias([
            createAccount('external-user.external-tenant', 'external@example.com'),
            createAccount(`internal-user.${microsoftTenantId.toUpperCase()}`, 'Current.Alias@microsoft.com'),
        ]);

        assert.strictEqual(alias, 'current.alias');
    });

    test('ignores malformed account identifiers and labels', () => {
        const alias = getInternalMicrosoftAlias([
            createAccount(microsoftTenantId, 'user@microsoft.com'),
            createAccount(`internal-user.${microsoftTenantId}`, 'Display Name'),
            createAccount(`internal-user.${microsoftTenantId}`, 'bad alias@microsoft.com'),
            createAccount(`internal-user.${microsoftTenantId}`, 'external.user@example.com'),
        ]);

        assert.strictEqual(alias, undefined);
    });

    test('refreshes when Microsoft authentication sessions change', async () => {
        const sessionChanges = new vscode.EventEmitter<vscode.AuthenticationSessionsChangeEvent>();
        let accounts: vscode.AuthenticationSessionAccountInformation[] = [];
        const provider = new MicrosoftAccountProvider({
            getAccounts: async () => accounts,
            onDidChangeSessions: sessionChanges.event,
        });

        try {
            await provider.initializeAsync();
            assert.strictEqual(provider.alias, undefined);
            assert.deepStrictEqual(provider.environmentState, { status: 'not_internal' });

            accounts = [createAccount(`internal-user.${microsoftTenantId}`, 'User@microsoft.com')];
            sessionChanges.fire({ provider: { id: 'microsoft', label: 'Microsoft' } });
            await waitFor(() => provider.alias === 'user');
            assert.deepStrictEqual(provider.environmentState, { status: 'internal', alias: 'user' });

            accounts = [];
            sessionChanges.fire({ provider: { id: 'microsoft', label: 'Microsoft' } });
            await waitFor(() => provider.alias === undefined);
            assert.deepStrictEqual(provider.environmentState, { status: 'not_internal' });
        }
        finally {
            provider.dispose();
            sessionChanges.dispose();
        }
    });

    test('preserves the last known alias when account enumeration fails transiently', async () => {
        const sessionChanges = new vscode.EventEmitter<vscode.AuthenticationSessionsChangeEvent>();
        let shouldFail = false;
        const warnings: string[] = [];
        const provider = new MicrosoftAccountProvider({
            getAccounts: async () => {
                if (shouldFail) {
                    throw new Error('Simulated authentication failure.');
                }

                return [createAccount(`internal-user.${microsoftTenantId}`, 'User@microsoft.com')];
            },
            onDidChangeSessions: sessionChanges.event,
        }, warning => warnings.push(warning));

        try {
            assert.strictEqual(await provider.getAliasAsync(), 'user');

            shouldFail = true;
            sessionChanges.fire({ provider: { id: 'microsoft', label: 'Microsoft' } });

            await assert.rejects(
                () => provider.getAliasAsync(),
                /VS Code Microsoft accounts are unavailable/);
            assert.strictEqual(provider.alias, 'user');
            assert.deepStrictEqual(provider.environmentState, { status: 'unavailable' });
            assert.deepStrictEqual(warnings, ['Unable to query VS Code Microsoft accounts.']);

            shouldFail = false;
            assert.deepStrictEqual(provider.getEnvironmentState(), { status: 'unavailable' });
            await waitFor(() => provider.environmentState.status === 'internal');
            assert.deepStrictEqual(provider.environmentState, { status: 'internal', alias: 'user' });
        }
        finally {
            provider.dispose();
            sessionChanges.dispose();
        }
    });

    test('reports unavailable when the initial account enumeration fails', async () => {
        const sessionChanges = new vscode.EventEmitter<vscode.AuthenticationSessionsChangeEvent>();
        const provider = new MicrosoftAccountProvider({
            getAccounts: async () => {
                throw new Error('Simulated authentication failure.');
            },
            onDidChangeSessions: sessionChanges.event,
        }, () => { });

        try {
            await provider.initializeAsync();
            assert.deepStrictEqual(provider.environmentState, { status: 'unavailable' });
        }
        finally {
            provider.dispose();
            sessionChanges.dispose();
        }
    });

    test('waits for a superseding session refresh before returning an alias', async () => {
        const sessionChanges = new vscode.EventEmitter<vscode.AuthenticationSessionsChangeEvent>();
        let getAccountsCallCount = 0;
        let resolveFirst!: (accounts: readonly vscode.AuthenticationSessionAccountInformation[]) => void;
        let resolveSecond!: (accounts: readonly vscode.AuthenticationSessionAccountInformation[]) => void;
        const provider = new MicrosoftAccountProvider({
            getAccounts: async () => {
                getAccountsCallCount++;
                return await new Promise<readonly vscode.AuthenticationSessionAccountInformation[]>(resolve => {
                    if (getAccountsCallCount === 1) {
                        resolveFirst = resolve;
                    }
                    else {
                        resolveSecond = resolve;
                    }
                });
            },
            onDidChangeSessions: sessionChanges.event,
        });

        try {
            let settled = false;
            const aliasTask = provider.getAliasAsync().finally(() => { settled = true; });
            await waitFor(() => getAccountsCallCount === 1);

            sessionChanges.fire({ provider: { id: 'microsoft', label: 'Microsoft' } });
            await waitFor(() => getAccountsCallCount === 2);
            resolveFirst([createAccount(`old-user.${microsoftTenantId}`, 'old.user@microsoft.com')]);
            await new Promise(resolve => setTimeout(resolve, 10));
            assert.strictEqual(settled, false);

            resolveSecond([]);
            assert.strictEqual(await aliasTask, undefined);
        }
        finally {
            provider.dispose();
            sessionChanges.dispose();
        }
    });

    test('switches to a superseding refresh without waiting for the stale refresh to finish', async () => {
        const sessionChanges = new vscode.EventEmitter<vscode.AuthenticationSessionsChangeEvent>();
        let getAccountsCallCount = 0;
        let resolveFirst!: (accounts: readonly vscode.AuthenticationSessionAccountInformation[]) => void;
        let resolveSecond!: (accounts: readonly vscode.AuthenticationSessionAccountInformation[]) => void;
        const provider = new MicrosoftAccountProvider({
            getAccounts: async () => {
                getAccountsCallCount++;
                return await new Promise<readonly vscode.AuthenticationSessionAccountInformation[]>(resolve => {
                    if (getAccountsCallCount === 1) {
                        resolveFirst = resolve;
                    }
                    else {
                        resolveSecond = resolve;
                    }
                });
            },
            onDidChangeSessions: sessionChanges.event,
        });

        try {
            const aliasTask = provider.getAliasAsync();
            await waitFor(() => getAccountsCallCount === 1);

            sessionChanges.fire({ provider: { id: 'microsoft', label: 'Microsoft' } });
            await waitFor(() => getAccountsCallCount === 2);
            resolveSecond([]);

            assert.strictEqual(await aliasTask, undefined);
        }
        finally {
            resolveFirst([]);
            provider.dispose();
            sessionChanges.dispose();
        }
    });

    test('returns cached state without retaining refresh-change waiters', async () => {
        const sessionChanges = new vscode.EventEmitter<vscode.AuthenticationSessionsChangeEvent>();
        let getAccountsCallCount = 0;
        const provider = new MicrosoftAccountProvider({
            getAccounts: async () => {
                getAccountsCallCount++;
                return [createAccount(`internal-user.${microsoftTenantId}`, 'User@microsoft.com')];
            },
            onDidChangeSessions: sessionChanges.event,
        });

        try {
            assert.strictEqual(await provider.getAliasAsync(), 'user');
            for (let index = 0; index < 100; index++) {
                assert.strictEqual(await provider.getAliasAsync(), 'user');
            }

            assert.strictEqual(getAccountsCallCount, 1);
        }
        finally {
            provider.dispose();
            sessionChanges.dispose();
        }
    });
});

function createAccount(id: string, label: string): vscode.AuthenticationSessionAccountInformation {
    return { id, label };
}

async function waitFor(predicate: () => boolean): Promise<void> {
    for (let attempt = 0; attempt < 20; attempt++) {
        if (predicate()) {
            return;
        }

        await new Promise(resolve => setTimeout(resolve, 10));
    }

    assert.fail('Timed out waiting for Microsoft account provider update.');
}
