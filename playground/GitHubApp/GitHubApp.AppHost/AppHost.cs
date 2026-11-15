// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

var appId = builder.AddParameter("github-app-id")
    .WithDescription("The numeric **App ID** shown on the GitHub App's settings page.", enableMarkdown: true);
var clientId = builder.AddParameter("github-client-id")
    .WithDescription("The **Client ID** shown on the GitHub App's settings page. It starts with `Iv`.", enableMarkdown: true);
var privateKey = builder.AddParameter("github-private-key", secret: true)
    .WithDescription("The contents of a **private key** `.pem` file generated on the GitHub App's settings page, with its line breaks.", enableMarkdown: true);

var gitHubApp = builder.AddExistingGitHubApp("github-app", appId, clientId, privateKey);

// Set the slug, and the owning organization if any, to link the dashboard to the app's own settings page.
// gitHubApp.WithAppSlug("my-app", organization: "my-org");

builder.AddDotnetProject("api", "../GitHubApp.Api/GitHubApp.Api.csproj")
    .WithReference(gitHubApp)
    .WaitFor(gitHubApp);

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddDotnetProject(KnownResourceNames.AspireDashboard, "../../../src/Aspire.Dashboard/Aspire.Dashboard.csproj");
#endif

builder.Build().Run();
