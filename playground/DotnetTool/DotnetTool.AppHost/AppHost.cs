// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using Microsoft.Extensions.DependencyInjection;
#pragma warning disable ASPIREDOTNETTOOL // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

var builder = DistributedApplication.CreateBuilder(args);

var simpleUsage = builder.AddDotnetTool("simpleUsage", "dotnet-ef");

var wildcardVersion = builder.AddDotnetTool("wildcard", "dotnet-ef")
    .WithToolVersion("10.0.*")
    .WithParentRelationship(simpleUsage);

var preRelease = builder.AddDotnetTool("prerelease", "dotnet-ef")
    .WithToolPrerelease()
    .WithParentRelationship(simpleUsage);

// Multiple versions
var differentVersion = builder.AddDotnetTool("sameToolDifferentVersion1", "dotnet-dump")
    .WithArgs("--version")
    .WithToolVersion("9.0.652701");
builder.AddDotnetTool("sameToolDifferentVersion2", "dotnet-dump")
    .WithToolVersion("9.0.621003")
    .WithArgs("--version")
    .WithParentRelationship(differentVersion);

// Concurrency
IResourceBuilder<DotnetToolResource>? concurrencyParent = null;
for (int i = 0; i < 5; i++)
{
    var concurrency = builder.AddDotnetTool($"sametoolconcurrency-{i}", "dotnet-trace")
        .WithArgs("--version");

    if (concurrencyParent == null)
    {
        concurrencyParent = concurrency;
    }
    else
    {
        concurrency.WithParentRelationship(concurrencyParent);
    }
}

// Substitution
var substituted = builder.AddDotnetTool("substituted", "dotnet-ef")
    .WithCommand("calc")
    .WithIconName("Calculator")
    .WithExplicitStart();
foreach(var toolAnnotation in substituted.Resource.Annotations.OfType<DotnetToolAnnotation>().ToList())
{
    substituted.Resource.Annotations.Remove(toolAnnotation);
}

// Fake Offline by using "empty" package feeds
var fakeSourcesPath = Path.Combine(Path.GetTempPath(), "does-not-exist", Guid.NewGuid().ToString());
var offline = builder.AddDotnetTool("offlineSimpleUsage", "dotnet-ef")
    .WithToolSource(fakeSourcesPath)
    .WithToolIgnoreExistingFeeds()
    .WithToolIgnoreFailedSources()
    ;

builder.AddDotnetTool("offlineWildcard", "dotnet-ef")
    .WithToolVersion("10.0.*")
    .WithParentRelationship(offline)
    .WithToolSource(fakeSourcesPath)
    .WithToolIgnoreExistingFeeds()
    .WithToolIgnoreFailedSources();

builder.AddDotnetTool("offlinePrerelease", "dotnet-ef")
    .WithToolPrerelease()
    .WithParentRelationship(offline)
    .WithToolSource(fakeSourcesPath)
    .WithToolIgnoreExistingFeeds()
    .WithToolIgnoreFailedSources();

var secret = builder.AddParameter("secret", "Shhhhhhh", secret: true);

// Secrets
builder.AddDotnetTool("secretArg", "dotnet-ef")
    .WithArgs("--version")
    .WithArgs(secret);

builder.AddDotnetTool("incompatibleSdk", "dotnet-ef")
    .WithWorkingDirectory(Path.Combine(Projects.DotnetTool_AppHost.ProjectPath, "IncompatibleSdk"));

// Some issues only show up when installing for first time, rather than using existing downloaded versions
// Use a specific NUGET_PACKAGES path for these playground tools, so we can easily reset them
builder.OnBeforeStart(async (evt, _) =>
{
    var nugetPackagesPath = Path.Join(evt.Services.GetRequiredService<IAspireStore>().BasePath, "nuget");

    foreach (var resource in builder.Resources.OfType<DotnetToolResource>())
    {
        builder.CreateResourceBuilder(resource)
            .WithEnvironment("NUGET_PACKAGES", nugetPackagesPath)
            .WithCommand("reset", "Reset Packages", ctx =>
            {
                try
                {
                    Directory.Delete(nugetPackagesPath, true);
                    return Task.FromResult(CommandResults.Success());
                }
                catch (Exception ex)
                {
                    return Task.FromResult(CommandResults.Failure(ex));
                }
            }, new CommandOptions
            {
                IconName = "Delete"
            });
    }
});

#if !SKIP_DASHBOARD_REFERENCE
// This project is only added in playground projects to support development/debugging
// of the dashboard. It is not required in end developer code. Comment out this code
// or build with `/p:SkipDashboardReference=true`, to test end developer
// dashboard launch experience, Refer to Directory.Build.props for the path to
// the dashboard binary (defaults to the Aspire.Dashboard bin output in the
// artifacts dir).
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();

