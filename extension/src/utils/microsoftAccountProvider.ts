import * as vscode from 'vscode';
import { extensionLogOutputChannel } from './logging';

const microsoftAuthenticationProviderId = 'microsoft';
const microsoftTenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47';
const validAliasPattern = /^[A-Za-z0-9._-]+$/;

type AuthenticationApi = Pick<typeof vscode.authentication, 'getAccounts' | 'onDidChangeSessions'>;

export class MicrosoftAccountProvider implements vscode.Disposable {
    private _authenticationChangeRegistration: vscode.Disposable | undefined;
    private _refreshGeneration = 0;
    private _refreshTask: Promise<void> | undefined;
    private _alias: string | undefined;
    private _latestRefreshSucceeded = false;
    private _disposed = false;

    get alias(): string | undefined {
        return this._alias;
    }

    constructor(
        private readonly _authentication: AuthenticationApi = vscode.authentication,
        private readonly _logWarning: (message: string) => void = message => extensionLogOutputChannel.warn(message),
    ) {
    }

    private initialize(): void {
        if (this._authenticationChangeRegistration) {
            this._refreshTask ??= this.refresh();
            return;
        }

        this._authenticationChangeRegistration = this._authentication.onDidChangeSessions(event => {
            if (event.provider.id === microsoftAuthenticationProviderId) {
                this._refreshTask = this.refresh();
            }
        });
        this._refreshTask ??= this.refresh();
    }

    async getAliasAsync(): Promise<string | undefined> {
        while (true) {
            this.initialize();
            const refreshTask = this._refreshTask;
            await refreshTask;

            if (refreshTask !== this._refreshTask) {
                if (!this._latestRefreshSucceeded && this._refreshTask === undefined) {
                    throw new Error('VS Code Microsoft accounts are unavailable.');
                }
                continue;
            }

            if (!this._latestRefreshSucceeded) {
                throw new Error('VS Code Microsoft accounts are unavailable.');
            }

            return this._alias;
        }
    }

    async refresh(): Promise<void> {
        const generation = ++this._refreshGeneration;
        let alias: string | undefined;

        try {
            const accounts = await this._authentication.getAccounts(microsoftAuthenticationProviderId);
            alias = getInternalMicrosoftAlias(accounts);
        }
        catch {
            if (!this._disposed && generation === this._refreshGeneration) {
                this._latestRefreshSucceeded = false;
                this._refreshTask = undefined;
                this._logWarning('Unable to query VS Code Microsoft accounts.');
            }
            return;
        }

        if (this._disposed || generation !== this._refreshGeneration || alias === this._alias) {
            if (!this._disposed && generation === this._refreshGeneration) {
                this._latestRefreshSucceeded = true;
            }
            return;
        }

        this._latestRefreshSucceeded = true;
        this._alias = alias;
    }

    dispose(): void {
        this._disposed = true;
        this._refreshGeneration++;
        this._authenticationChangeRegistration?.dispose();
    }
}

export function getInternalMicrosoftAlias(accounts: readonly vscode.AuthenticationSessionAccountInformation[]): string | undefined {
    // VS Code's built-in Microsoft provider exposes MSAL's homeAccountId as account.id.
    // MSAL defines homeAccountId as the dot-separated uniqueId.tenantId pair.
    // https://github.com/microsoft/vscode/blob/main/extensions/microsoft-authentication/src/node/authProvider.ts
    // https://github.com/AzureAD/microsoft-authentication-library-for-js/blob/dev/lib/msal-common/docs/Accounts.md
    const aliases = accounts
        .filter(account => getHomeTenantId(account.id)?.toLowerCase() === microsoftTenantId)
        .map(account => getAlias(account.label))
        .filter(alias => alias !== undefined)
        .sort();

    return aliases[0];
}

function getHomeTenantId(accountId: string): string | undefined {
    const separatorIndex = accountId.lastIndexOf('.');
    return separatorIndex > 0 && separatorIndex < accountId.length - 1
        ? accountId.slice(separatorIndex + 1)
        : undefined;
}

function getAlias(accountLabel: string): string | undefined {
    const atIndex = accountLabel.lastIndexOf('@');
    if (atIndex <= 0) {
        return undefined;
    }

    const domain = accountLabel.slice(atIndex + 1).toLowerCase();
    if (domain !== 'microsoft.com' && !domain.endsWith('.microsoft.com')) {
        return undefined;
    }

    const alias = accountLabel.slice(0, atIndex);
    return validAliasPattern.test(alias) ? alias.toLowerCase() : undefined;
}
