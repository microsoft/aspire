# Microsoft Entra ID hosting integration

Use this integration to model, configure, and orchestrate Microsoft Entra ID application registrations in an Aspire solution.

## Getting started

### Prerequisites

- A Microsoft Entra ID tenant
- For new registrations: an Azure subscription, permission to deploy to a resource group and create application registrations and service principals in its tenant, and Azure CLI with Bicep 0.36.1 or later
- For existing registrations: an app registration created in the [Microsoft Entra admin center](https://entra.microsoft.com)

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.Azure.EntraId` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.Azure.EntraId
```

## Usage example

Then, in the AppHost, add an Entra ID application resource and reference it from another resource.

### Create registrations

`AddEntraIdApplication` creates a registration and its service principal using the Microsoft Graph Bicep extension:

```csharp
var entraApi = builder.AddEntraIdApplication("entra-api");
var entraWeb = builder.AddEntraIdApplication("entra-web")
                      .WithRedirectUris(
                          "https://localhost:7123/signin-oidc",
                          "https://localhost:7123/signout-callback-oidc");

builder.AddProject<Projects.Api>("api")
       .WithReference(entraApi);

builder.AddProject<Projects.Web>("web")
       .WithReference(entraWeb);
```

For .NET program resources, `WithReference` injects environment variables in the form `AzureAd__{Key}` — for example `AzureAd__Instance`, `AzureAd__TenantId`, and `AzureAd__ClientId`. .NET's configuration system maps these to the `AzureAd` configuration section, so the referencing resource reads them as ordinary configuration with no glue code.

When the AppHost starts, the Entra ID resource validates its IDs, instance, and credentials, then reports **Running** and shows links to the app registration and its OpenID Connect discovery document in the dashboard. If a setting is invalid, the resource fails to start and its console logs explain why.

Configure [local Azure provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/) with your subscription and location, and sign in to Azure CLI. The registrations are created in **the subscription's tenant**, not necessarily your account's home tenant. Aspire does not explicitly assign application or service-principal owners; Microsoft Entra's default ownership behavior applies. If tenant policy prevents creation, use `AddExistingEntraApplication` for a configuration-only reference that requires no Azure or Graph access.

The module is generated in C# using `Azure.Provisioning`, including the Graph application, service principal, parameters, and outputs. Aspire emits the Microsoft Graph extension declaration from the infrastructure's `BicepExtensions` collection using a pinned inline registry reference. No `bicepconfig.json` is required, including when Aspire copies the module into temporary or published output. The first compilation downloads the extension from `mcr.microsoft.com`, which must be reachable.

The resource derives from `AzureProvisioningResource`, so `ConfigureInfrastructure` callbacks can customize the generated infrastructure or add provisioning outputs.

The client ID and home tenant ID are deployment outputs. For multi-tenant registrations, consumers receive the appropriate sign-in keyword as `TenantId` and the home tenant as `AppHomeTenantId`. `WithSignInAudience` sets the new registration's supported account types, and created applications request version 2 access tokens.

By default, the registration's display name and directory-wide `uniqueName` are the name passed to `AddEntraIdApplication`.
When the AppHost creates registrations, the Azure provisioning dialog includes an optional **Tenant-scoped resource suffix**.
For example, the resource `entra-web` with suffix `dev-a` creates `entra-web-dev-a`; leaving the field empty creates `entra-web`.
The logical Aspire resource name and consumer environment-variable prefixes remain unchanged.

The suffix is shared by participating registrations in the AppHost and saved with the Azure context. You can supply an initial
value through `Azure:TenantResourceSuffix`, and edit or clear the saved value through **Change Azure context**.
References created with `AddExistingEntraApplication` do not require this field or enable Azure provisioning.

Interactive `aspire deploy` also prompts for an optional suffix when `Azure:TenantResourceSuffix` is not configured.
Set that configuration value, including an empty string, to skip the prompt. Non-interactive deployment uses an empty
suffix when no value is configured.

Published Azure templates expose `tenantResourceSuffix` as a deployment parameter, defaulting to an empty string, rather than
embedding a developer's local suffix. An explicit module parameter overrides the shared value:

```csharp
var entraApi = builder.AddEntraIdApplication("entra-api")
                      .WithParameter("tenantResourceSuffix", "production");
```

The same tenant, resource name, and suffix target the same registration regardless of resource group. Use different names or
suffixes when developers or AppHosts should not share a registration; competing deployments can overwrite shared settings.
Changing the name or suffix targets another registration and leaves the previous application and service principal intact.
Existing registrations with different `uniqueName` values are not automatically adopted: reference their IDs with
`AddExistingEntraApplication` or explicitly clean them up when migrating.

Configure callback URIs with `WithRedirectUris`. To derive the scheme, host, and port from a resource's endpoint, pass the endpoint and the
callback paths. When running locally the allocated endpoint URL is used; when publishing or deploying, the host comes from the resource's
compute environment. The callback path can't be discovered, so it must match the path configured in the consuming app:

```csharp
entraWeb.WithRedirectUris(web.GetEndpoint("https"), "/signin-oidc", "/signout-callback-oidc");
```

When running locally, the endpoint's primary URL is used, so a friendly `*.dev.localhost` name is registered when the dashboard uses one.
That URL is read when the registration's URL callback runs, so a `WithUrlForEndpoint` or `WithUrls` callback added after this call that changes
the endpoint's host isn't reflected. Add such callbacks before calling `WithRedirectUris`.

When only the host name varies per environment (for example, a custom domain supplied at deployment time), pass a parameter for the host
and the fixed callback paths. Each URI is `https://{host}{path}`:

```csharp
var host = builder.AddParameter("web-host");
entraWeb.WithRedirectUris(host, "/signin-oidc", "/signout-callback-oidc");
```

To supply the externally visible
URIs per environment at deployment time (for example, behind a gateway or custom domain), pass parameters instead of literals.
Each parameter value may contain several URIs separated by commas (split in the generated Bicep, so URIs can't contain commas
and entries must not have surrounding whitespace). Literal and parameter-backed URIs can be mixed and are registered in call order:

```csharp
var redirectUris = builder.AddParameter("web-redirect-uris");
entraWeb.WithRedirectUris(redirectUris);
```

For OpenID Connect sign-in flows that request an ID token directly from the authorization endpoint, explicitly enable ID-token issuance:

```csharp
entraWeb.WithIdTokenIssuance();
```

ID-token issuance through implicit or hybrid flows is disabled by default. `WithIdTokenIssuance` enables only ID-token issuance, not implicit access-token issuance, and only affects registrations created by Aspire. For existing registrations, enable **ID tokens (used for implicit and hybrid flows)** in their **Authentication** settings instead. It is not needed for authorization-code-only flows. For new production applications, prefer authorization code flow with PKCE and configure a confidential-client credential when the application redeems codes.

Creating a registration does not protect a workload's routes or authorize API requests. Configure authentication and authorization in the consuming application using Microsoft.Identity.Web or the corresponding library for that language. A web-to-API flow additionally requires an API identifier URI, exposed scopes, client permissions/consent, a client credential, and scope or role validation in the API. These are not provisioned by this initial increment.

### Assign explicit owners

By default Aspire assigns no owners, so Microsoft Entra's default ownership behavior applies. To opt in, pass directory object IDs (users or service principals) as literals or parameters. Application and service principal ownership are configured separately:

```csharp
var ownerId = builder.AddParameter("entra-owner-id");

var entraWeb = builder.AddEntraIdApplication("entra-web")
                      .WithOwners(ownerId);
```

`WithOwners` sets both; use `WithApplicationOwners` / `WithServicePrincipalOwners` to configure them independently.

Owners are appended; existing owners are never removed. Owners can manage the registration, and the deploying identity needs permission to manage owners (for example `Application.ReadWrite.All`). Existing registrations are not modified.

### Declare application roles

Application roles (for example `User` and `Admin`) appear in the `roles` claim of tokens issued for the API. They are distinct from delegated scopes, consent, and Azure RBAC roles. `WithAppRole` only declares roles on registrations created by Aspire; it does not assign them to any user, group, or application:

```csharp
var entraApi = builder.AddEntraIdApplication("entra-api")
                      .WithAppRole(new EntraIdAppRole("User", "User", "Can use the API."))
                      .WithAppRole(new EntraIdAppRole("Admin", "Administrator", "Can administer the API.",
                          ["User", "Application"]));
```

Role IDs are derived from the resource name and role value (or supplied with the `id` argument), so they stay stable across deployments. Changing a role's value or ID creates a different role. A role's value can be at most 120 characters, can't start with a period, and can contain only ASCII letters, digits and the punctuation that [Microsoft Graph allows](https://learn.microsoft.com/graph/api/resources/approle), so spaces and quotation marks are rejected when you write the AppHost.

Whenever the application declares at least one role, Aspire sends the complete `appRoles` collection and Microsoft Graph replaces what is stored, so include every role you want to keep, including ones that were added outside the AppHost. A role that is left out fails the deployment if it is still enabled (`CannotDeleteOrUpdateEnabledEntitlement`) and is deleted if it is already disabled. When the application declares no roles, Aspire doesn't send `appRoles`, so roles that the AppHost never declared are left alone.

To retire a role, deploy it with `isEnabled: false` and leave it declared. A disabled role can't be assigned to anyone new, but Microsoft Entra [keeps issuing it in tokens for existing assignments](https://learn.microsoft.com/entra/identity-platform/howto-add-app-roles-in-apps#app-roles-ui), so disabling isn't access revocation; remove the existing assignments separately. A role can be removed from the AppHost only after a deployment has disabled it, and only while other roles remain declared. Existing registrations are never modified, and role assignments are not provisioned yet.

### Clean up created registrations

Registrations live in the tenant and **are not deleted when the resource group is deleted**. The deployment outputs include `objectId`, `servicePrincipalId`, and `uniqueName` for locating the objects.

The registration exposes deployment reprovisioning, cancellation, and local-state commands, not ARM resource inspection,
relocation, or deletion. The environment's **Delete Azure resources** command can still delete the resource group.
After deletion, the command reports their provider types, last-known object IDs, and tenant ID for manual cleanup.
Their cached identification outputs and dashboard links are retained; ordinary ARM resources' deployment state is reset.
The report does not query Microsoft Graph to verify live objects, and missing IDs are reported as unknown.

Delete only the registrations and service principals belonging to your application using the [Microsoft Entra admin center](https://entra.microsoft.com) or Microsoft Graph. Deleted applications remain soft-deleted for 30 days; restore or permanently delete the application before redeploying with the same `uniqueName`.

### Reference an existing registration

Call `AsExistingApplication` to reference an existing registration instead of provisioning one:

```csharp
var tenantId = builder.AddParameter("EntraTenantId");
var apiClientId = builder.AddParameter("EntraApiClientId");

var entraApi = builder.AddEntraIdApplication("entra-api")
                      .AsExistingApplication(tenantId: tenantId, clientId: apiClientId);

var api = builder.AddProject<Projects.Api>("api")
                 .WithReference(entraApi);
```

Pass the **Directory (tenant) ID** and **Application (client) ID** shown on the app registration's **Overview** page. When the parameters have no value, the dashboard prompts for them.

`AddEntraIdApplication` enables Azure provisioning before `AsExistingApplication` is called. The fluent API does not
undo that setup: deploying the AppHost can still require Azure authentication and provisioning configuration, even
though the existing registration itself is not deployed or queried through Microsoft Graph.

For an AppHost that only references existing registrations and does not need Azure provisioning, use the top-level
`AddExistingEntraApplication` instead:

```csharp
var entraApi = builder.AddExistingEntraApplication("entra-api", tenantId: tenantId, clientId: apiClientId);
```

This factory accepts parameters or literal IDs and does not enable Azure provisioning. Other Azure resources in the
same AppHost still enable their normal provisioning behavior.

### Custom environment variable names

`WithReference` automatically uses `"AzureAd"` with `"__"` separators for .NET program resources.
For a different .NET configuration section, set `connectionName`. Colons in nested section paths are converted
to double underscores; for example, `"Authentication:AzureAd"` produces `Authentication__AzureAd__ClientId`:

```csharp
var customApi = builder.AddProject<Projects.Api>("custom-api")
                       .WithReference(entraApi, connectionName: "Authentication:AzureAd");
```

Other resources use portable uppercase snake-case names with `"_"` separators, including the registration resource's
name and each configuration key. For example, referencing `"entra-api"` produces `ENTRA_API_TENANT_ID` and `ENTRA_API_CLIENT_ID`.
Set the optional `connectionName` to override the prefix:

```csharp
var worker = builder.AddContainer("worker", "my-worker-image")
                    .WithReference(entraApi, connectionName: "ENTRA");
```

This produces names such as `ENTRA_TENANT_ID` and `ENTRA_CLIENT_ID`. The naming convention applies throughout nested settings
and arrays, for example `ENTRA_CLIENT_CREDENTIALS_0_SOURCE_TYPE` when a credential is configured.
Custom connection names are normalized too: `"MyAuth"` produces `MY_AUTH_CLIENT_ID`.
An empty connection name omits both the prefix and its separator: `.WithReference(entraApi, connectionName: "")` produces `CLIENT_ID`
and `CLIENT_CREDENTIALS_0_CLIENT_SECRET`, without a leading underscore. The separator still applies between nested keys
and array indexes. For .NET program resources, the equivalent credential name is `ClientCredentials__0__ClientSecret`.
The .NET naming convention is unchanged, and configuration values retain their original casing for all consumers.
Each consumer chooses its own connection name without affecting other references to the
same registration. Non-.NET applications read these variables and map the values into their authentication library's
options; the integration does not automatically configure those libraries.

## Sign-in audiences

By default, only accounts in the app's home tenant can sign in. When the app registration's **Supported account types** setting allows other accounts, call `WithSignInAudience` with the matching value:

```csharp
var entraApi = builder.AddExistingEntraApplication("entra-api", tenantId: tenantId, clientId: apiClientId)
                      .WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs);
```

| Sign-in audience | Who can sign in | `AzureAd__TenantId` |
|------------------|-----------------|---------------------|
| `AzureADMyOrg` (default) | Accounts in the home tenant | The home tenant ID |
| `AzureADMultipleOrgs` | Accounts in any Microsoft Entra tenant | `organizations` |
| `AzureADandPersonalMicrosoftAccount` | Accounts in any Microsoft Entra tenant and personal Microsoft accounts | `common` |
| `PersonalMicrosoftAccount` | Personal Microsoft accounts only | `consumers` |

For the audiences other than `AzureADMyOrg`, the home tenant ID is injected as `AzureAd__AppHomeTenantId`, which Microsoft.Identity.Web needs when the app acquires tokens as itself. Always pass the home tenant ID to `AsExistingApplication` or `AddExistingEntraApplication`, not `organizations`, `common`, or `consumers`.

## Client credentials

An application that acquires tokens for itself needs a client credential. Each `With*` call appends an entry to the injected `AzureAd__ClientCredentials__{index}__*` variables, and multiple credentials can be configured as a fallback chain.

### Client secret

```csharp
var webSecret = builder.AddParameter("EntraWebClientSecret", secret: true);

var entraWeb = builder.AddExistingEntraApplication("entra-web", tenantId: tenantId, clientId: webClientId)
                      .WithClientSecret(webSecret);
```

The parameter must be created with `secret: true`.

### Federated identity credential with managed identity

For deployed applications, use a federated managed identity credential so that no secret is stored. Aspire creates the registration, and the registration trusts the managed identity of each workload that references it:

```csharp
var entraWeb = builder.AddEntraIdApplication("entra-web")
                      .PublishWithFederatedManagedIdentityCredential();

builder.AddProject<Projects.Web>("web")
       .WithReference(entraWeb);
```

Referencing the registration makes the workload need a user-assigned managed identity when published, the same way referencing an Azure resource does. The workload uses the identity it already has (for example from `WithAzureUserAssignedIdentity`), or the Azure environment creates one. The federated credential uses the tenant's v2.0 issuer, the identity's **object (principal) ID** as the subject, and the token-exchange audience of the application's cloud (Azure public, US Government and China are supported; other clouds fail with an error). The workload receives the identity's **client ID** as `ManagedIdentityClientId`. Deployment order is identity, then registration, then workload.

To trust only specific workloads, or a specific identity, pass them. A workload passed this way must also reference the registration, and only it receives the credential:

```csharp
var entraWeb = builder.AddEntraIdApplication("entra-web");
var web = builder.AddProject<Projects.Web>("web").WithReference(entraWeb);

entraWeb.PublishWithFederatedManagedIdentityCredential(web);
```

```csharp
var identity = builder.AddAzureUserAssignedIdentity("web-identity");
var entraWeb = builder.AddEntraIdApplication("entra-web")
                      .PublishWithFederatedManagedIdentityCredential(identity);

builder.AddProject<Projects.Web>("web")
       .WithAzureUserAssignedIdentity(identity)
       .WithReference(entraWeb);
```
The federated credential only changes how the app authenticates as itself. Interactive user sign-in still needs a redirect URI registered on the app registration. Derive it from the web app's endpoint with `WithRedirectUris(web.GetEndpoint("https"), "/signin-oidc")`, which uses the compute environment's host when deployed, so it does not make the registration depend on the web app.

Managed identities exist only in Azure, so `PublishWithFederatedManagedIdentityCredential` is not used when running locally; add another credential, such as a client secret, for local development.

#### Existing registrations

Aspire never modifies an existing registration, so `PublishWithFederatedManagedIdentityCredential` throws for `AddExistingEntraApplication`. Create the trust yourself, then use `WithFederatedManagedIdentityCredential` with the managed identity's client ID. It only configures the app, and applies when running locally as well as when publishing:

```csharp
var entraWeb = builder.AddExistingEntraApplication("entra-web", tenantId: tenantId, clientId: webClientId)
                      .WithFederatedManagedIdentityCredential("00001111-aaaa-2222-bbbb-3333cccc4444");
```

Add a federated credential to the registration with the tenant's v2.0 issuer (`https://login.microsoftonline.com/<tenant-id>/v2.0` in the Azure public cloud), the identity's **principal (object) ID** as the subject, and `api://AzureADTokenExchange` as the audience. Pass the identity's **client ID**, not its principal ID, to `WithFederatedManagedIdentityCredential`. See [Configure an application to trust a managed identity](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity).
Federation authenticates as the app registration using a managed identity assertion. It is different from acquiring a token directly as the managed identity. It also does not enable mTLS proof-of-possession: that requires downstream API `ProtocolScheme = "MTLS_POP"` and the transport/certificate support described in Microsoft's [bound-credentials guidance](https://github.com/AzureAD/microsoft-identity-web/tree/master/.github/skills/boundcredentials).

### Certificate from Azure Key Vault

```csharp
var entraWeb = builder.AddExistingEntraApplication("entra-web", tenantId: tenantId, clientId: webClientId)
                      .WithCertificateFromKeyVault("https://myvault.vault.azure.net", "MyCert");
```

### Certificate from the certificate store

```csharp
var entraWeb = builder.AddExistingEntraApplication("entra-web", tenantId: tenantId, clientId: webClientId)
                      .WithCertificateThumbprint("CurrentUser/My", "ABC123...");
```

Use `WithCertificateDistinguishedName` to locate the certificate by subject instead of thumbprint.

### Advanced credentials

For credential types without a dedicated method, use `WithCredential`:

```csharp
var entraWeb = builder.AddExistingEntraApplication("entra-web", tenantId: tenantId, clientId: webClientId)
                      .WithCredential(new EntraIdSignedAssertionFileCredential());
```

## Sovereign clouds

To target a sovereign cloud instance such as Azure Government:

```csharp
var entraApi = builder.AddExistingEntraApplication("entra-api", tenantId: tenantId, clientId: apiClientId)
                      .WithInstance("https://login.microsoftonline.us/");
```

## Current limitations

The integration can reference existing registrations or create registrations and service principals. It doesn't yet:

* Automatically add the referencing resources' URLs as redirect URIs.
* Expose API scopes or grant API permissions.
* Assign application roles to users, groups, or applications (not yet supported).
* Delete the last application role. When no roles are declared, Aspire doesn't send `appRoles`, so keep the last role declared with `isEnabled: false`, or delete it in the Microsoft Entra admin center.
* Create, store, or rotate client secrets and certificates.

Existing registrations are never modified. Continue configuring their redirect URIs, API permissions, and credentials in the [Microsoft Entra admin center](https://entra.microsoft.com).

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://learn.microsoft.com/entra/identity-platform/
* https://learn.microsoft.com/graph/templates/bicep/overview-bicep-templates-for-graph
* https://learn.microsoft.com/entra/msal/dotnet/microsoft-identity-web/
* https://github.com/AzureAD/microsoft-identity-web/tree/master/.github/skills
* https://devblogs.microsoft.com/aspire/securing-dotnet-aspire-apps-with-microsoft-entra-id/

## Feedback & contributing

https://github.com/microsoft/aspire
