# Microsoft Entra ID playground

This playground uses the `Aspire.Hosting.Azure.EntraId` integration to sign in users to two independent web front ends:
the .NET app, `webfrontend`, and the Node.js app, `nodefrontend`.

Both apps use the same `weather-web` registration. The .NET app uses Microsoft.Identity.Web,
and Node uses `openid-client` with the individual environment variables supplied by `WithReference`.
Like HashR, both use ID-token-only OpenID Connect implicit sign-in: Entra returns an ID token through a browser form POST,
and the app validates its signature, issuer, audience, lifetime, and nonce before creating a local login session.
Neither app exchanges an authorization code or requires a client secret.
Neither app calls the other or a downstream API.
The two front ends have separate local sessions, although Entra ID can reuse your browser's single-sign-on session.

The integration doesn't create app registrations yet, so you create one registration in your own tenant and give the AppHost
its IDs. You need a tenant where you can create app registrations, such as the default directory of an Azure subscription.

## Register the shared web application

1. In the [Microsoft Entra admin center](https://entra.microsoft.com), open **App registrations** and select **New registration**.
1. Name it `weather-web`, choose **Accounts in this organizational directory only**, and under **Redirect URI**, choose **Web**, enter `https://localhost:7251/signin-oidc`, and select **Register**.
1. From the **Overview** page, copy the **Application (client) ID** and the **Directory (tenant) ID**.
1. Open **Authentication** and add `https://localhost:7251/signout-callback-oidc` as a second redirect URI, so that Entra ID sends you back to the app after you sign out.
1. Under the same **Web** platform, also add `https://localhost:7252/auth/redirect` and `https://localhost:7252/auth/signout-callback` for the Node front end. Keep the .NET redirect URIs; both front ends use this registration.
1. Under **Implicit grant and hybrid flows**, enable **ID tokens (used for implicit and hybrid flows)** and save. Access tokens are not needed.

No exposed API, weather API permission, or API client ID is needed. If you already configured the previous version of the
playground, keep using its web registration, IDs, and redirect URIs; enable the ID-token setting above.
The old API registration and client secret are no longer used. Existing registrations and saved secrets are not deleted.

This playground intentionally matches HashR's sign-in-only flow. For new production applications, prefer
[authorization code flow with PKCE](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-auth-code-flow).

Entra ID redirects only to URIs that exactly match a registered one, so the web front end has a single `https` launch profile that pins it to `https://localhost:7251`, whichever profile the AppHost runs with. Aspire's proxy listens on that port and forwards to the app.

The Node front end uses `https://localhost:7252` for the same reason. Aspire supplies its HTTPS certificate and private key.
Use a non-isolated run for these registered callback URLs. `--isolated` randomizes endpoint ports, so an isolated run
requires registering its actual callback URLs instead.

## Provide the IDs

The AppHost declares a parameter for each value:

| Parameter | Value |
| --- | --- |
| `entra-tenant-id` | Directory (tenant) ID |
| `entra-web-client-id` | Application (client) ID of `weather-web` |

When you run the AppHost, the dashboard prompts for any value that's missing and can save it to user secrets. To set the values ahead of time instead, run these commands from the `EntraId.AppHost` directory:

```bash
dotnet user-secrets set "Parameters:entra-tenant-id" "<tenant-id>"
dotnet user-secrets set "Parameters:entra-web-client-id" "<web-client-id>"
```

## Run the playground

From this directory, run `aspire run`, or `dotnet run --project EntraId.AppHost`. In the dashboard, open
`webfrontend` at `https://localhost:7251`, select **Sign in with Microsoft Entra ID**, and view your signed-in identity.

Node.js 20 or later and npm must be installed. Aspire installs the Node front end's dependencies automatically using npm.
Open `nodefrontend` at `https://localhost:7252`, then select **Sign in with Microsoft Entra ID** to see your name and username.
Both apps only sign users in; neither requests weather API scopes or calls Microsoft Graph.
The Node app's **Sign out** button clears its session and ends the Entra browser session, without deleting the .NET app's local cookie.
The Node session store and cookie signing key are in memory; restarting Node signs you out of that app.
This session setup is for local experimentation, not production deployment.

## What to look for

- `entra-web` reports **Running** once its IDs are valid. It links to the shared app registration in the Azure portal and to the tenant's OpenID Connect discovery document.
- The environment variables of `webfrontend` show the `AzureAd__*` tenant and client settings that `WithReference` adds. No client credentials are configured.
- `nodefrontend` references `entra-web` independently using the default connection name and receives `ENTRA_WEB_INSTANCE`, `ENTRA_WEB_TENANT_ID`, and `ENTRA_WEB_CLIENT_ID`. Its code maps these values to OpenID Connect options.
- If a value is malformed, such as a tenant ID that's neither a GUID nor a domain name, the Entra ID resource reports **FailedToStart** and its console logs say what's wrong.

## Troubleshooting

| Error | Likely cause |
| --- | --- |
| AADSTS50011 | The redirect URI doesn't match. Check that `weather-web` has the callback for the front end you opened: `https://localhost:7251/signin-oidc` for .NET or `https://localhost:7252/auth/redirect` for Node. |
| AADSTS700054 or `response_type 'id_token' is not enabled` | Enable **ID tokens (used for implicit and hybrid flows)** under the web registration's **Authentication** settings. |
| **Need admin approval** | Your tenant restricts user consent. Ask an administrator to approve sign-in for the shared web registration. |
