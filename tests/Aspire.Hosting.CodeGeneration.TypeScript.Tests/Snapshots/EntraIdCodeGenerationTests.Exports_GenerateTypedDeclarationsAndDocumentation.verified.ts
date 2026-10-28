export interface CSharpAppResource {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): CSharpAppResourcePromise;
}

export interface CSharpAppResourcePromise {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): CSharpAppResourcePromise;
}

export interface ContainerResource {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): ContainerResourcePromise;
}

export interface ContainerResourcePromise {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): ContainerResourcePromise;
}

export interface DistributedApplicationBuilder {
    addEntraIdApplication(name: string): EntraIdApplicationResourcePromise;
    addExistingEntraApplication(name: string, tenantId: string | ParameterResource | Awaitable<ParameterResource>, clientId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
}

export interface DistributedApplicationBuilderPromise {
    addEntraIdApplication(name: string): EntraIdApplicationResourcePromise;
    addExistingEntraApplication(name: string, tenantId: string | ParameterResource | Awaitable<ParameterResource>, clientId: string | ParameterResource | Awaitable<ParameterResource>): EntraIdApplicationResourcePromise;
}

export interface DotnetToolResource {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): DotnetToolResourcePromise;
}

export interface DotnetToolResourcePromise {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): DotnetToolResourcePromise;
}

export interface ExecutableResource {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): ExecutableResourcePromise;
}

export interface ExecutableResourcePromise {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): ExecutableResourcePromise;
}

export interface ProjectResource {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): ProjectResourcePromise;
}

export interface ProjectResourcePromise {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): ProjectResourcePromise;
}

export interface ResourceWithEnvironment {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): ResourceWithEnvironmentPromise;
}

export interface ResourceWithEnvironmentPromise {
    withEntraIdReference(source: Awaitable<EntraIdApplicationResource>, options?: WithEntraIdReferenceOptions): ResourceWithEnvironmentPromise;
}

export interface EntraIdCustomApi {
    displayName?: string;
    applicationId?: string;
    resourceUri?: string;
    baseUrl?: string | null;
}

export interface EntraIdExternalApiPermission {
    name?: string;
    type?: EntraIdPermissionType | null;
    id?: string | null;
}

export interface EntraIdScopeOptions {
    displayName?: string | null;
    userConsentDisplayName?: string | null;
    userConsentDescription?: string | null;
    requiresAdminConsent?: boolean | null;
    isEnabled?: boolean | null;
    id?: string | null;
}

export interface EntraIdTokenExchangeOptions {
    tokenExchangeUrl?: string | null;
    tokenExchangeAuthority?: string | null;
}

export enum EntraIdPermissionType {
    Delegated = "Delegated",
    Application = "Application",
}

export enum EntraIdSignInAudience {
    AzureADMyOrg = "AzureADMyOrg",
    AzureADMultipleOrgs = "AzureADMultipleOrgs",
    AzureADandPersonalMicrosoftAccount = "AzureADandPersonalMicrosoftAccount",
    PersonalMicrosoftAccount = "PersonalMicrosoftAccount",
}

export enum EntraIdWellKnownApi {
    MicrosoftGraph = "MicrosoftGraph",
    AzureResourceManager = "AzureResourceManager",
    AzureStorage = "AzureStorage",
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
    withPreAuthorizedApplication(client: Awaitable<EntraIdApplicationResource>, scopeNames: string[]): EntraIdApplicationResourcePromise;
    withApiPermissions(api: Awaitable<EntraIdApplicationResource>, scopeNames: string[]): EntraIdApplicationResourcePromise;
    withAdminConsent(api: Awaitable<EntraIdApplicationResource>, scopeNames: string[]): EntraIdApplicationResourcePromise;
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
    withApplicationIdUri(options?: WithApplicationIdUriOptions): EntraIdApplicationResourcePromise;
    withScope(name: string, description: string, options?: EntraIdScopeOptions): EntraIdApplicationResourcePromise;
    getScopeUri(scopeName: string): Promise<ReferenceExpression>;
    withExternalApiPermissions(api: EntraIdWellKnownApi | EntraIdCustomApi, permissions: EntraIdExternalApiPermission[]): EntraIdApplicationResourcePromise;
    withExternalApiAdminConsent(api: EntraIdWellKnownApi | EntraIdCustomApi, permissions: EntraIdExternalApiPermission[]): EntraIdApplicationResourcePromise;
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
    withPreAuthorizedApplication(client: Awaitable<EntraIdApplicationResource>, scopeNames: string[]): EntraIdApplicationResourcePromise;
    withApiPermissions(api: Awaitable<EntraIdApplicationResource>, scopeNames: string[]): EntraIdApplicationResourcePromise;
    withAdminConsent(api: Awaitable<EntraIdApplicationResource>, scopeNames: string[]): EntraIdApplicationResourcePromise;
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
    withApplicationIdUri(options?: WithApplicationIdUriOptions): EntraIdApplicationResourcePromise;
    withScope(name: string, description: string, options?: EntraIdScopeOptions): EntraIdApplicationResourcePromise;
    getScopeUri(scopeName: string): Promise<ReferenceExpression>;
    withExternalApiPermissions(api: EntraIdWellKnownApi | EntraIdCustomApi, permissions: EntraIdExternalApiPermission[]): EntraIdApplicationResourcePromise;
    withExternalApiAdminConsent(api: EntraIdWellKnownApi | EntraIdCustomApi, permissions: EntraIdExternalApiPermission[]): EntraIdApplicationResourcePromise;
}

export interface PublishWithFederatedManagedIdentityCredentialOptions {
    identity?: CSharpAppResource | ComputeResource | ContainerResource | DotnetToolResource | ExecutableResource | ProjectResource | AzureUserAssignedIdentityResource | Awaitable<CSharpAppResource | ComputeResource | ContainerResource | DotnetToolResource | ExecutableResource | ProjectResource | AzureUserAssignedIdentityResource>;
}

export interface WithAppRoleOptions {
    allowedMemberTypes?: string[];
    id?: string;
    isEnabled?: boolean | null;
}

export interface WithApplicationIdUriOptions {
    applicationIdUri?: string;
}

export interface WithCertificateFromFileOptions {
    password?: Awaitable<ParameterResource>;
}

export interface WithEntraIdReferenceOptions {
    connectionName?: string;
    downstreamApisPrefix?: string;
}

export interface WithRedirectUrisOptions {
    callbackPaths?: string[];
}

export interface WithSignedAssertionFileOptions {
    filePath?: string;
}