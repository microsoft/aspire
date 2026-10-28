export interface DistributedApplicationBuilder {
    addEntraIdApplication(name: string): EntraIdApplicationResourcePromise;
    addExistingEntraApplication(name: string, tenantId: string | ParameterResource | Awaitable<ParameterResource>, clientId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
}

export interface DistributedApplicationBuilderPromise {
    addEntraIdApplication(name: string): EntraIdApplicationResourcePromise;
    addExistingEntraApplication(name: string, tenantId: string | ParameterResource | Awaitable<ParameterResource>, clientId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
}

export interface EntraIdTokenExchangeOptions {
    tokenExchangeUrl?: string | null;
    tokenExchangeAuthority?: string | null;
}

export enum EntraIdSignInAudience {
    AzureADMyOrg = "AzureADMyOrg",
    AzureADMultipleOrgs = "AzureADMultipleOrgs",
    AzureADandPersonalMicrosoftAccount = "AzureADandPersonalMicrosoftAccount",
    PersonalMicrosoftAccount = "PersonalMicrosoftAccount",
}

export interface EntraIdApplicationResource extends ResourceBuilderBase {
    toJSON(): MarshalledHandle;
    withClientSecret(clientSecret: Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withCertificateFromKeyVault(keyVaultUrl: string, certificateName: string): EntraIdApplicationResourcePromise;
    withCertificateThumbprint(storePath: string, thumbprint: string): EntraIdApplicationResourcePromise;
    withCertificateDistinguishedName(storePath: string, distinguishedName: string): EntraIdApplicationResourcePromise;
    withInstance(instance: string): EntraIdApplicationResourcePromise;
    withSignInAudience(signInAudience: EntraIdSignInAudience): EntraIdApplicationResourcePromise;
    withIdTokenIssuance(): EntraIdApplicationResourcePromise;
    withClientCapability(capability: string): EntraIdApplicationResourcePromise;
    withAllowWebApiToBeAuthorizedByACL(): EntraIdApplicationResourcePromise;
    withSendX5C(): EntraIdApplicationResourcePromise;
    withExtraQueryParameter(key: string, value: string): EntraIdApplicationResourcePromise;
    withAudience(audience: string): EntraIdApplicationResourcePromise;
    asExistingApplication(tenantId: string | ParameterResource | Awaitable<ParameterResource>, clientId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withRedirectUris(redirectUri: string | ParameterResource | EndpointReference | Awaitable<ParameterResource | EndpointReference>, options?: WithRedirectUrisOptions): EntraIdApplicationResourcePromise;
    withApplicationOwners(ownerObjectId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withServicePrincipalOwners(ownerObjectId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withOwners(ownerObjectId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withFederatedManagedIdentityCredential(managedIdentityClientId: string, options?: EntraIdTokenExchangeOptions): EntraIdApplicationResourcePromise;
    publishWithFederatedManagedIdentityCredential(options?: PublishWithFederatedManagedIdentityCredentialOptions): EntraIdApplicationResourcePromise;
    withAppRole(value: string, displayName: string, description: string, options?: WithAppRoleOptions): EntraIdApplicationResourcePromise;
    withCertificateFromFile(filePath: string, options?: WithCertificateFromFileOptions): EntraIdApplicationResourcePromise;
    withSignedAssertionFile(options?: WithSignedAssertionFileOptions): EntraIdApplicationResourcePromise;
}

export interface EntraIdApplicationResourcePromise extends PromiseLike<EntraIdApplicationResource> {
    withClientSecret(clientSecret: Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withCertificateFromKeyVault(keyVaultUrl: string, certificateName: string): EntraIdApplicationResourcePromise;
    withCertificateThumbprint(storePath: string, thumbprint: string): EntraIdApplicationResourcePromise;
    withCertificateDistinguishedName(storePath: string, distinguishedName: string): EntraIdApplicationResourcePromise;
    withInstance(instance: string): EntraIdApplicationResourcePromise;
    withSignInAudience(signInAudience: EntraIdSignInAudience): EntraIdApplicationResourcePromise;
    withIdTokenIssuance(): EntraIdApplicationResourcePromise;
    withClientCapability(capability: string): EntraIdApplicationResourcePromise;
    withAllowWebApiToBeAuthorizedByACL(): EntraIdApplicationResourcePromise;
    withSendX5C(): EntraIdApplicationResourcePromise;
    withExtraQueryParameter(key: string, value: string): EntraIdApplicationResourcePromise;
    withAudience(audience: string): EntraIdApplicationResourcePromise;
    asExistingApplication(tenantId: string | ParameterResource | Awaitable<ParameterResource>, clientId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withRedirectUris(redirectUri: string | ParameterResource | EndpointReference | Awaitable<ParameterResource | EndpointReference>, options?: WithRedirectUrisOptions): EntraIdApplicationResourcePromise;
    withApplicationOwners(ownerObjectId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withServicePrincipalOwners(ownerObjectId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withOwners(ownerObjectId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
    withFederatedManagedIdentityCredential(managedIdentityClientId: string, options?: EntraIdTokenExchangeOptions): EntraIdApplicationResourcePromise;
    publishWithFederatedManagedIdentityCredential(options?: PublishWithFederatedManagedIdentityCredentialOptions): EntraIdApplicationResourcePromise;
    withAppRole(value: string, displayName: string, description: string, options?: WithAppRoleOptions): EntraIdApplicationResourcePromise;
    withCertificateFromFile(filePath: string, options?: WithCertificateFromFileOptions): EntraIdApplicationResourcePromise;
    withSignedAssertionFile(options?: WithSignedAssertionFileOptions): EntraIdApplicationResourcePromise;
}

export interface PublishWithFederatedManagedIdentityCredentialOptions {
    identity?: CSharpAppResource | ComputeResource | ContainerResource | DotnetToolResource | ExecutableResource | ProjectResource | AzureUserAssignedIdentityResource | Awaitable<CSharpAppResource | ComputeResource | ContainerResource | DotnetToolResource | ExecutableResource | ProjectResource | AzureUserAssignedIdentityResource>;
}

export interface WithAppRoleOptions {
    allowedMemberTypes?: string[];
    id?: string;
    isEnabled?: boolean | null;
}

export interface WithCertificateFromFileOptions {
    password?: Awaitable<ParameterResource>;
}

export interface WithRedirectUrisOptions {
    callbackPaths?: string[];
}

export interface WithSignedAssertionFileOptions {
    filePath?: string;
}