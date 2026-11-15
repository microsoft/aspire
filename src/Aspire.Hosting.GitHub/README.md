# GitHub App hosting integration

Use this integration to model, configure, and orchestrate a GitHub App resource in an Aspire solution, and give its credentials to the resources that call GitHub as the app.

## Getting started

### Prerequisites

- A GitHub account, or an organization where you can register apps.
- To register the app from the AppHost, AppHost user secrets, which Aspire templates enable with a `UserSecretsId`.
- To use an app that is already registered, its private key. See [Registering a GitHub App](https://docs.github.com/apps/creating-github-apps/registering-a-github-app/registering-a-github-app).

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.GitHub` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.GitHub
aspire add Aspire.Hosting.Dotnet
```

## Usage example

### Register a new app

In the AppHost, add a GitHub App resource and reference it from another resource:

**C#**

```csharp
var api = builder.AddDotnetProject("api", "../Api/Api.csproj");

var gitHubApp = builder.AddGitHubApp("github-app")
                       .WithCallback(api.GetEndpoint("https"), "/signin-github")
                       .WithPermission("issues", GitHubAppPermissionLevel.Read)
                       .WithPermission("pull_requests", GitHubAppPermissionLevel.Write);

api.WithReference(gitHubApp);
```

On the first run, the resource shows as **Waiting for registration**, and the resources that reference it wait for it. Select **Register on GitHub** on the resource or in the dashboard's notification, in a browser on the machine running the AppHost. The AppHost sends GitHub a [manifest](https://docs.github.com/apps/sharing-github-apps/registering-a-github-app-from-a-manifest) describing the app, and GitHub shows it for you to review. Once you create the app, GitHub returns to the AppHost, which retrieves the app's credentials, saves them to the AppHost's user secrets, and continues to the page that installs the app. Later runs reuse the saved app without calling GitHub.

Pass an organization to register the app in that organization rather than your personal account:

```csharp
var gitHubApp = builder.AddGitHubApp("github-app", organization: "contoso");
```

- `WithCallback` adds a URL that GitHub redirects users to after they authorize the app, its OAuth callback. Up to 10 are allowed. The URL that GitHub returns to after registering the app is separate and handled by the AppHost.
- `WithPermission` requests a [permission](https://docs.github.com/rest/apps/apps#create-an-installation-access-token-for-an-app) by its GitHub name, at `Read`, `Write` or `Admin` level.
- The app's name is the resource name with a random suffix, and can be changed on GitHub's registration page.

The manifest applies only when the app is registered. To change the callbacks or permissions of a registered app, change them on its settings page, or delete the app and its saved credentials to register a new one. Callbacks built from endpoints should use fixed ports. The saved credentials are under `GitHub:Apps:{hash}:{resource name}` in the user secrets, where the hash identifies the AppHost's path, so that worktrees of the same repository, which share a `UserSecretsId`, register their own apps.

When publishing, `AddGitHubApp` adds an existing app with the parameters `{name}-app-id`, `{name}-client-id`, `{name}-private-key` and `{name}-client-secret`, because a deployed application can't register an app.

> [!NOTE]
> The registration listens on `127.0.0.1` on a random port, so the browser must run on the same machine as the AppHost. It isn't reachable from remote environments such as GitHub Codespaces or dev containers. Register the app elsewhere and use `AddExistingGitHubApp` there.

### Use an existing app

Add a GitHub App resource for an app that is already registered, and reference it from another resource:

**C#**

```csharp
var appId = builder.AddParameter("github-app-id");
var clientId = builder.AddParameter("github-client-id");
var privateKey = builder.AddParameter("github-private-key", secret: true);

var gitHubApp = builder.AddExistingGitHubApp("github-app", appId, clientId, privateKey)
                       .WithAppSlug("my-app");

var api = builder.AddDotnetProject("api", "../Api/Api.csproj")
                 .WithReference(gitHubApp);
```

The App ID and Client ID are shown on the app's settings page, where you can also generate the private key. Set the parameters from your AppHost directory. Store the private key with its line breaks, for example by reading the downloaded `.pem` file:

```bash
aspire secret set Parameters:github-app-id "123456"
aspire secret set Parameters:github-client-id "Iv23liAbCdEf01234567"
aspire secret set Parameters:github-private-key "$(cat my-app.private-key.pem)"
```

Pass a client secret when the app signs users in, and a webhook secret when it receives webhooks. Both must be secret parameters:

```csharp
var gitHubApp = builder.AddExistingGitHubApp("github-app", appId, clientId, privateKey,
    clientSecret: builder.AddParameter("github-client-secret", secret: true),
    webhookSecret: builder.AddParameter("github-webhook-secret", secret: true));
```

When the AppHost starts, the resource checks that the values are well formed without calling GitHub. It shows as running in the dashboard with a link to the app's settings page, or as failed to start with the problems in its console logs. `WithAppSlug` sets the app's slug, and the organization that owns it, if any, so the dashboard can link to the app's own pages.

### Environment variables

`WithReference` gives the referencing resource these environment variables:

| Variable | Value |
| --- | --- |
| `GITHUB_APP_ID` | The App ID |
| `GITHUB_CLIENT_ID` | The Client ID |
| `GITHUB_PRIVATE_KEY` | The PEM-encoded private key |
| `GITHUB_CLIENT_SECRET` | The client secret, when the app has one |
| `GITHUB_WEBHOOK_SECRET` | The webhook secret, when the app has one |
| `GITHUB_API_URL` | The base URL of the GitHub REST API, `https://api.github.com` |
| `APP_ID`, `PRIVATE_KEY`, `WEBHOOK_SECRET` | The names that [Probot](https://probot.github.io/docs/configuration/) apps read |

.NET projects also receive the values as the `GitHub:App` configuration section, through `GitHub__App__AppId`, `GitHub__App__ClientId`, `GitHub__App__PrivateKey`, `GitHub__App__ClientSecret`, `GitHub__App__WebhookSecret`, and `GitHub__App__ApiUrl`.

When a resource references more than one app, give each reference a connection name. The name replaces the `GITHUB` prefix in upper snake case and names the .NET configuration section. The Probot names can hold only one app, so they hold the app referenced without a connection name, or otherwise the app referenced first:

```csharp
builder.AddDotnetProject("worker", "../Worker/Worker.csproj")
       .WithReference(ordersBot, connectionName: "orders-bot")    // ORDERS_BOT_APP_ID, orders-bot:AppId, APP_ID
       .WithReference(releaseBot, connectionName: "release-bot"); // RELEASE_BOT_APP_ID, release-bot:AppId
```

## Additional documentation

* https://aspire.dev/integrations/gallery/
* https://docs.github.com/apps/creating-github-apps/about-creating-github-apps/about-creating-github-apps
* https://docs.github.com/apps/creating-github-apps/authenticating-with-a-github-app/about-authentication-with-a-github-app

## Feedback & contributing

https://github.com/microsoft/aspire
