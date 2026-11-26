# GitHub App playground

This playground registers a GitHub App from a manifest and references it from a .NET API. The API authenticates as the app with a JWT signed by the app's private key, and lists the app's installations.

## Run

```bash
aspire run
```

On the first run, the `github-app` resource shows as **Waiting for registration**:

1. Select **Register on GitHub** on the resource, in the dashboard's notification, or in the URL logged to the resource's console. The link must be opened in a browser on the machine running the AppHost.
2. GitHub shows the app's name, permissions and callback URLs. Change the name if you like, and select **Create GitHub App**.
3. GitHub returns to the AppHost, which retrieves the app's credentials and saves them to the AppHost's user secrets. The browser continues to the page that installs the app on your account or repositories.

The resource then shows as running, and the `api` resource starts. Open its endpoint to see the app's installations. Later runs reuse the saved app.

To use an app that is already registered instead, follow the comment in `AppHost.cs`.

## Cleanup

Delete the app from its settings page under **Advanced**. Then delete its saved credentials, the keys that start with `GitHub:Apps:`, from the AppHost's secrets, so that the next run registers a new app:

```bash
aspire secret list
aspire secret delete "GitHub:Apps:<hash>:github-app:AppId"
```

Repeat the delete for each of the app's keys.
