// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETTOOL

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.EntityFrameworkCore.Tests;

public class EFMigrationToolConfigurationTests
{
    [Fact]
    public async Task ConfigureToolResourceRetainsSupportedLegacyFluentMethods()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = builder.AddDotnetProject(
            "api",
            "Api.csproj",
            options => options.ExcludeLaunchProfile = true);

        project.AddEFMigrations(
            "migrations",
            configureToolResource: tool => tool
                .WithToolPackage("custom-dotnet-ef")
                .WithToolVersion("10.0.0")
                .WithToolPrerelease()
                .WithToolSource("https://example.invalid/feed")
                .WithToolIgnoreExistingFeeds()
                .WithToolIgnoreFailedSources());

        var toolResource = Assert.IsAssignableFrom<ExecutableResource>(
            Assert.Single(builder.Resources, resource => resource.Name == "ef-tool-migrations"));
        Assert.Equal(
            ["tool", "exec", "custom-dotnet-ef", "--source", "https://example.invalid/feed",
             "--ignore-failed-sources", "--version", "10.0.0", "--yes", "--"],
            await ArgumentEvaluator.GetArgumentListAsync(toolResource));
    }
}
