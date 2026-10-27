# Microsoft Entra ID playground

This playground uses the `Aspire.Hosting.Azure.EntraId` integration to sign in users to two independent web front ends:
the .NET app, `webfrontend`, and the Node.js app, `nodefrontend`.

Both apps use the same application registration. The .NET app uses Microsoft.Identity.Web,
and Node uses `openid-client` with the individual environment variables supplied by `WithReference`.
Like HashR, both use ID-token-only OpenID Connect implicit sign-in: Entra returns an ID token through a browser form POST,
and the app validates its signature, issuer, audience, lifetime, and nonce before creating a local login session.
Neither app exchanges an authorization code or requires a client secret.
Neither app calls the other or a downstream API.
The two front ends have separate local sessions, although Entra ID can reuse your browser's single-sign-on session.

## Choose a mode in the AppHost

Change `useExistingApplication` near the top of `EntraId.AppHost/AppHost.cs`:

```csharp
var useExistingApplication = true;
```

| Value | Mode | Setup |
| --- | --- | --- |
| `true` | Existing | Supply an existing registration's tenant/client IDs and configure its callbacks and ID-token issuance. No Azure provisioning configuration or Graph access is needed at runtime. |
| `false` | Create new | Aspire provisions a registration and service principal using Bicep. Supply Azure provisioning settings and permissions; the AppHost configures both front ends' callbacks and enables ID-token issuance. |

The switch is an AppHost code choice, not a command-line option or configuration setting. Stop the AppHost before changing it,
then rebuild and run. Both front ends use `WithReference` to receive the same `entra-web` configuration. In creation mode,
environment resolution awaits the provisioned outputs without an implicit or explicit `WaitFor`.

## Mode 1: Use an existing registration

Set `useExistingApplication = true`. Use a registration in your chosen Entra tenant; it need not be the tenant of an Azure
subscription. Aspire does not modify the registration, so configure it before running the playground.

### Configure the shared web application

1. In the [Microsoft Entra admin center](https://entra.microsoft.com), open **App registrations** and select your existing registration. If you don't have one, select **New registration**, name it `weather-web`, choose **Accounts in this organizational directory only**, and select **Register**.
1. From the **Overview** page, copy the **Application (client) ID** and the **Directory (tenant) ID**.
1. Open **Authentication** and add a **Web** platform if needed. Add `https://localhost:7251/signin-oidc` and `https://localhost:7251/signout-callback-oidc`, so that Entra ID sends you back to the .NET app after sign-in and sign-out.
1. Under the same **Web** platform, also add `https://localhost:7252/auth/redirect` and `https://localhost:7252/auth/signout-callback` for the Node front end. Keep the .NET redirect URIs; both front ends use this registration.
1. Under **Implicit grant and hybrid flows**, enable **ID tokens (used for implicit and hybrid flows)** and save. Access tokens are not needed.

No exposed API, weather API permission, or API client ID is needed. If you already configured the previous version of the
playground, keep using its web registration, IDs, and redirect URIs; enable the ID-token setting above.
The old API registration and client secret are no longer used. Existing registrations and saved secrets are not deleted.

### Provide the IDs

The AppHost declares a parameter for each value:

| Parameter | Value |
| --- | --- |
| `entra-tenant-id` | Directory (tenant) ID |
| `entra-web-client-id` | Application (client) ID of the shared web registration |

When you run the AppHost, the dashboard prompts for any value that's missing and can save it to user secrets. To set the values ahead of time instead, run these commands from the `EntraId.AppHost` directory:

```bash
dotnet user-secrets set "Parameters:entra-tenant-id" "<tenant-id>"
dotnet user-secrets set "Parameters:entra-web-client-id" "<web-client-id>"
```

## Mode 2: Create a new registration

Set `useExistingApplication = false`. You do not need to create a registration manually or supply `entra-tenant-id` and
`entra-web-client-id`; these parameters are only declared in existing mode. Previously saved values are ignored, not deleted.

### Configure Azure provisioning

You need an Azure subscription, permission to deploy to its resource group, permission to create application registrations
and service principals in its tenant, and Azure CLI with Bicep 0.36.1 or later. Tenant policy may restrict registration creation.
The first Bicep compilation downloads the pinned Microsoft Graph extension from `mcr.microsoft.com`.

Run the AppHost and follow the Azure provisioning prompts in the dashboard to select the tenant, subscription,
resource group, and location. You do not need to set these values manually beforehand.

The dialog also includes an optional **Tenant-scoped resource suffix**, saved with the Azure context.
Use a developer-specific suffix such as `dev-a` to create `entra-web-dev-a`, or leave it empty to create `entra-web`.
The logical Aspire resource name remains `entra-web` in either case. Change or clear the suffix through **Change Azure context**;
`Azure:TenantResourceSuffix` can supply an initial value. Existing mode does not need this setting.

Use the tenant associated with the subscription. Creation mode does not support choosing a separate registration tenant
from the Azure deployment's tenant. See [local Azure provisioning](https://aspire.dev/integrations/cloud/azure/local-provisioning/)
and the [hosting integration README](../../src/Aspire.Hosting.Azure.EntraId/README.md#create-registrations).

### What Aspire creates

On startup, Aspire creates the shared `entra-web` application registration and its service principal, using the
single-tenant audience. Aspire does not explicitly assign owners; Microsoft Entra's default ownership behavior applies.
The AppHost registers these **Web** callback URIs:

| Front end | Sign-in callback | Sign-out callback |
| --- | --- | --- |
| .NET | `https://localhost:7251/signin-oidc` | `https://localhost:7251/signout-callback-oidc` |
| Node | `https://localhost:7252/auth/redirect` | `https://localhost:7252/auth/signout-callback` |

The AppHost calls `WithIdTokenIssuance()` because both front ends use ID-token-only sign-in. It does not create a
client secret, expose API scopes, or grant API permissions. Both front ends wait for `entra-web` and receive its provisioned
tenant/client IDs through `WithReference`.

Unchanged restarts reuse deployment state. Configuration updates preserve the registration's identity while the tenant,
resource name, and suffix stay unchanged, independently of the resource group. Different developers should choose distinct
suffixes to avoid sharing and overwriting the same registration. Changing the name or suffix targets another registration
without deleting the old one. Previously created registrations with different `uniqueName` values are not automatically
reused; reference them in existing mode or explicitly clean them up when migrating.

### Clean up provisioned registrations

Stopping the AppHost or switching back to existing mode does not delete the registration. Deleting its Azure resource group
also does **not** delete the application or service principal, because they live in the Entra directory.

Use the `entra-web` dashboard link and deployment outputs (`objectId`, `servicePrincipalId`, and `uniqueName`) to locate
the objects. Delete only the playground's application and service principal using the Entra admin center or Microsoft Graph,
and remove its dedicated Azure resource group when no longer needed. Deleted applications are retained for 30 days;
restore or permanently delete the playground's soft-deleted application before reprovisioning with the same stable key.

## Run the playground

This playground intentionally matches HashR's sign-in-only flow. For new production applications, prefer
[authorization code flow with PKCE](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-auth-code-flow).

Entra ID redirects only to URIs that exactly match a registered one, so the web front end has a single `https` launch profile
that pins it to `https://localhost:7251`, whichever profile the AppHost runs with. Aspire's proxy listens on that port and
forwards to the app. The Node front end uses `https://localhost:7252` for the same reason; Aspire supplies its HTTPS
certificate and private key.

Use a non-isolated run in existing mode. `--isolated` randomizes endpoint ports, so it requires updating the callbacks in the
existing registration. In creation mode the AppHost derives the callback hosts and ports from each front end's endpoint, so
isolated runs register the actual URLs; the callback paths remain fixed in the AppHost's `WithRedirectUris` calls.

From this directory, run `aspire run`, or `dotnet run --project EntraId.AppHost`. In the dashboard, open
`webfrontend` at `https://localhost:7251`, select **Sign in with Microsoft Entra ID**, and view your signed-in identity.

Node.js 20 or later and npm must be installed. Aspire installs the Node front end's dependencies automatically using npm.
Open `nodefrontend` at `https://localhost:7252`, then select **Sign in with Microsoft Entra ID** to see your name and username.
Both apps only sign users in; neither requests weather API scopes or calls Microsoft Graph.
The Node app's **Sign out** button clears its session and ends the Entra browser session, without deleting the .NET app's local cookie.
The Node session store and cookie signing key are in memory; restarting Node signs you out of that app.
This session setup is for local experimentation, not production deployment.

## What to look for

- In existing mode, `entra-web` reports **Running** once its IDs are valid; in creation mode, it waits for provisioning first. It links to the shared app registration in the Azure portal and to the tenant's OpenID Connect discovery document.
- The environment variables of `webfrontend` show the `AzureAd__*` tenant and client settings that `WithReference` adds. No client credentials are configured.
- `nodefrontend` references `entra-web` independently using the default connection name and receives `ENTRA_WEB_INSTANCE`, `ENTRA_WEB_TENANT_ID`, and `ENTRA_WEB_CLIENT_ID`. Its code maps these values to OpenID Connect options.
- If a value is malformed, such as a tenant ID that's neither a GUID nor a domain name, the Entra ID resource reports **FailedToStart** and its console logs say what's wrong.

## Troubleshooting

| Error | Likely cause |
| --- | --- |
| AADSTS50011 | The redirect URI doesn't match. Check that the shared registration has the callback for the front end you opened: `https://localhost:7251/signin-oidc` for .NET or `https://localhost:7252/auth/redirect` for Node. |
| AADSTS700054 or `response_type 'id_token' is not enabled` | Enable **ID tokens (used for implicit and hybrid flows)** under the web registration's **Authentication** settings. |
| **Need admin approval** | Your tenant restricts user consent. Ask an administrator to approve sign-in for the shared web registration. |
| Creation fails with missing Azure configuration or permission denied | Verify the subscription/tenant/location settings and the provisioning identity's Azure deployment and Entra registration-creation permissions, or switch to existing mode. |
| Creation fails because `uniqueName` already exists | Restore or permanently delete the playground's soft-deleted application before reprovisioning with the same key. |
