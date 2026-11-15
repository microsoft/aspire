// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

var builder = DistributedApplication.CreateBuilder(args);

// On the first run, the dashboard links to GitHub to register the app. Its credentials are then saved to the AppHost's
// user secrets, and later runs reuse the app.
var gitHubApp = builder.AddGitHubApp("github-app")
    .WithPermission("metadata", GitHubAppPermissionLevel.Read);

// To use an app that is already registered instead, replace the resource above with:
// var gitHubApp = builder.AddExistingGitHubApp(
//     "github-app",
//     builder.AddParameter("github-app-id"),
//     builder.AddParameter("github-client-id"),
//     builder.AddParameter("github-private-key", secret: true));

// WithReference waits for the app to be registered before the API starts.
builder.AddDotnetProject("api", "../GitHubApp.Api/GitHubApp.Api.csproj")
    .WithReference(gitHubApp);

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
