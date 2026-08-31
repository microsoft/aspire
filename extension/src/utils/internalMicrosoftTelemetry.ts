import * as vscode from 'vscode';
import { extensionLogOutputChannel } from './logging';
import { CommonTelemetryProperties, setCommonTelemetryProperties } from './telemetry';

const microsoftAuthenticationProviderId = 'microsoft';
const microsoftTenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47';
const validAliasPattern = /^[A-Za-z0-9._-]+$/;
const initialRefreshTimeoutMs = 1_000;

type AuthenticationApi = Pick<typeof vscode.authentication, 'getAccounts' | 'onDidChangeSessions'>;
type CommonPropertiesSetter = (properties: CommonTelemetryProperties) => void;

export interface InternalMicrosoftTelemetryIdentity {
    readonly isInternal: boolean;
    readonly alias?: string;
    readonly domain?: string;
}

export class InternalMicrosoftTelemetryProvider implements vscode.Disposable {
    private _authenticationChangeRegistration: vscode.Disposable | undefined;
    private _refreshGeneration = 0;
    private _disposed = false;

    constructor(
        private readonly _authentication: AuthenticationApi = vscode.authentication,
        private readonly _setCommonProperties: CommonPropertiesSetter = setCommonTelemetryProperties,
        private readonly _logWarning: (message: string) => void = message => extensionLogOutputChannel.warn(message),
        private readonly _initialRefreshTimeoutMs = initialRefreshTimeoutMs,
    ) {
        this.publish({ isInternal: false });
    }

    async initializeAsync(): Promise<void> {
        if (!this._authenticationChangeRegistration) {
            this._authenticationChangeRegistration = this._authentication.onDidChangeSessions(event => {
                if (event.provider.id === microsoftAuthenticationProviderId) {
                    void this.refreshAsync();
                }
            });
        }

        const refreshTask = this.refreshAsync();
        let timeout: NodeJS.Timeout | undefined;
        try {
            await Promise.race([
                refreshTask,
                new Promise<void>(resolve => {
                    timeout = setTimeout(resolve, this._initialRefreshTimeoutMs);
                }),
            ]);
        }
        finally {
            if (timeout) {
                clearTimeout(timeout);
            }
        }
    }

    dispose(): void {
        this._disposed = true;
        this._refreshGeneration++;
        this._authenticationChangeRegistration?.dispose();
    }

    private async refreshAsync(): Promise<void> {
        const generation = ++this._refreshGeneration;

        // Do not emit a stale identity while VS Code is resolving a sign-in, sign-out, or account switch.
        this.publish({ isInternal: false });

        let accounts: readonly vscode.AuthenticationSessionAccountInformation[];
        try {
            accounts = await this._authentication.getAccounts(microsoftAuthenticationProviderId);
        }
        catch {
            if (!this._disposed && generation === this._refreshGeneration) {
                this._logWarning('Unable to query VS Code Microsoft accounts for telemetry enrichment.');
            }
            return;
        }

        if (!this._disposed && generation === this._refreshGeneration) {
            this.publish(getInternalMicrosoftTelemetryIdentity(accounts));
        }
    }

    private publish(identity: InternalMicrosoftTelemetryIdentity): void {
        this._setCommonProperties({
            is_microsoft_internal: identity.isInternal ? 'true' : 'false',
            microsoft_internal_alias: identity.alias,
            microsoft_internal_domain: identity.domain,
        });
    }
}

export function getInternalMicrosoftTelemetryIdentity(
    accounts: readonly vscode.AuthenticationSessionAccountInformation[],
): InternalMicrosoftTelemetryIdentity {
    const internalAccounts = accounts.filter(account =>
        getHomeTenantId(account.id)?.toLowerCase() === microsoftTenantId);
    if (internalAccounts.length === 0) {
        return { isInternal: false };
    }

    const identities = internalAccounts
        .map(account => getLoginIdentity(account.label))
        .filter(identity => identity !== undefined)
        .sort((left, right) =>
            left.alias.localeCompare(right.alias) || left.domain.localeCompare(right.domain));

    return identities[0]
        ? { isInternal: true, ...identities[0] }
        : { isInternal: true };
}

function getHomeTenantId(accountId: string): string | undefined {
    // VS Code's built-in Microsoft provider exposes MSAL's homeAccountId as the account ID:
    //   <uniqueId>.<tenantId>
    // https://github.com/microsoft/vscode/blob/main/extensions/microsoft-authentication/src/node/authProvider.ts
    // https://github.com/AzureAD/microsoft-authentication-library-for-js/blob/dev/lib/msal-common/docs/Accounts.md
    const separatorIndex = accountId.lastIndexOf('.');
    return separatorIndex > 0 && separatorIndex < accountId.length - 1
        ? accountId.slice(separatorIndex + 1)
        : undefined;
}

function getLoginIdentity(accountLabel: string): { alias: string; domain: string } | undefined {
    const atIndex = accountLabel.lastIndexOf('@');
    if (atIndex <= 0 || atIndex === accountLabel.length - 1) {
        return undefined;
    }

    const alias = accountLabel.slice(0, atIndex);
    const domain = accountLabel.slice(atIndex + 1).toLowerCase();
    if (!validAliasPattern.test(alias) ||
        (domain !== 'microsoft.com' && !domain.endsWith('.microsoft.com'))) {
        return undefined;
    }

    return {
        alias: alias.toLowerCase(),
        domain,
    };
}
