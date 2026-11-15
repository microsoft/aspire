# GitHub App playground

This playground references an existing GitHub App from a .NET API. The API authenticates as the app with a JWT signed by the app's private key, and lists the app's installations.

## Setup

1. [Register a GitHub App](https://docs.github.com/apps/creating-github-apps/registering-a-github-app/registering-a-github-app) on your account or an organization. It needs no permissions, callback URL, or webhook for this playground; deactivate the webhook if GitHub asks for a URL.
2. On the app's settings page, note the **App ID** and **Client ID**, and generate a private key.
3. From `playground/GitHubApp`, store the values:

   ```bash
   aspire secret set Parameters:github-app-id "<app id>"
   aspire secret set Parameters:github-client-id "<client id>"
   aspire secret set Parameters:github-private-key "$(cat <path to the downloaded .pem file>)"
   ```

   You can instead enter the values when the dashboard prompts for them.
4. Optionally, install the app on your account or a repository so that the API has an installation to list.

## Run

```bash
aspire run
```

The `github-app` resource shows as running once the values are well formed, or as failed to start with the problems in its console logs. Open the `api` resource's endpoint to see the app's installations.

To link the dashboard to the app's own settings page, uncomment the `WithAppSlug` call in `AppHost.cs` and set the app's slug, and its organization if an organization owns it.

## Cleanup

Delete the app from its settings page under **Advanced** when you no longer need it.
