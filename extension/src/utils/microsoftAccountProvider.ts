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
    private _refreshChanged: Promise<void>;
    private _resolveRefreshChanged: () => void = () => { };
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
        this._refreshChanged = this.createRefreshChangedPromise();
    }

    private initialize(): void {
        if (this._authenticationChangeRegistration) {
            if (!this._refreshTask && !this._latestRefreshSucceeded) {
                this.setRefreshTask(this.refresh());
            }
            return;
        }

        this._authenticationChangeRegistration = this._authentication.onDidChangeSessions(event => {
            if (event.provider.id === microsoftAuthenticationProviderId) {
                this.setRefreshTask(this.refresh());
            }
        });
        if (!this._refreshTask && !this._latestRefreshSucceeded) {
            this.setRefreshTask(this.refresh());
        }
    }

    async getAliasAsync(): Promise<string | undefined> {
        while (true) {
            this.initialize();
            const refreshTask = this._refreshTask;
            if (!refreshTask) {
                if (!this._latestRefreshSucceeded) {
                    throw new Error('VS Code Microsoft accounts are unavailable.');
                }

                return this._alias;
            }
            const refreshChanged = this._refreshChanged;
            await Promise.race([refreshTask, refreshChanged]);

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
                this.setRefreshTask(undefined);
                this._logWarning('Unable to query VS Code Microsoft accounts.');
            }
            return;
        }

        if (this._disposed || generation !== this._refreshGeneration || alias === this._alias) {
            if (!this._disposed && generation === this._refreshGeneration) {
                this._latestRefreshSucceeded = true;
                this.setRefreshTask(undefined);
            }
            return;
        }

        this._latestRefreshSucceeded = true;
        this._alias = alias;
        this.setRefreshTask(undefined);
    }

    dispose(): void {
        this._disposed = true;
        this._refreshGeneration++;
        this.setRefreshTask(undefined);
        this._authenticationChangeRegistration?.dispose();
    }

    private setRefreshTask(refreshTask: Promise<void> | undefined): void {
        if (refreshTask === this._refreshTask) {
            return;
        }

        this._refreshTask = refreshTask;
        this._resolveRefreshChanged();
        this._refreshChanged = this.createRefreshChangedPromise();
    }

    private createRefreshChangedPromise(): Promise<void> {
        return new Promise(resolve => {
            this._resolveRefreshChanged = resolve;
        });
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
