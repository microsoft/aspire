# GitHub App hosting integration

Use this integration to model, configure, and orchestrate a GitHub App resource in an Aspire solution, and give its credentials to the resources that call GitHub as the app.

## Getting started

### Prerequisites

- A GitHub App registered on GitHub, with a generated private key. See [Registering a GitHub App](https://docs.github.com/apps/creating-github-apps/registering-a-github-app/registering-a-github-app).

### Add the integration

From your AppHost directory, add the `Aspire.Hosting.GitHub` integration with the Aspire CLI:

```bash
aspire add Aspire.Hosting.GitHub
aspire add Aspire.Hosting.Dotnet
```

## Usage example

Then, in the AppHost, add a GitHub App resource for an existing app and reference it from another resource:

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
