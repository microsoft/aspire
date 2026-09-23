// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROJECTS001

using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ProjectRebuilderResourceTests
{
    [Fact]
    public async Task AddProjectRebuilderUsesConfiguredBuildConfiguration()
    {
        using var builder = CreateBuilder();
        var project = builder.AddProject<Projects.ServiceA>("servicea", options => options.ExcludeLaunchProfile = true);
        var launchDefaults = Assert.Single(project.Resource.Annotations.OfType<ProjectLaunchDefaultsAnnotation>());
        launchDefaults.BuildConfiguration = "Release";

        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(rebuilder);

        Assert.Equal(
            [
                "build",
                project.Resource.GetProjectMetadata().ProjectPath,
                "--configuration",
                "Release"
            ],
            args);
    }

    [Fact]
    public async Task AddProjectRebuilderOmitsConfigurationWhenAppHostConfigurationIsUnavailable()
    {
        using var builder = CreateBuilder();
        var project = builder.AddProject<Projects.ServiceA>("servicea", options => options.ExcludeLaunchProfile = true);
        var launchDefaults = Assert.Single(project.Resource.Annotations.OfType<ProjectLaunchDefaultsAnnotation>());
        launchDefaults.BuildConfiguration = null;

        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        var args = await ArgumentEvaluator.GetArgumentListAsync(rebuilder);

        Assert.Equal(
            [
                "build",
                project.Resource.GetProjectMetadata().ProjectPath
            ],
            args);
    }

    private static IDistributedApplicationTestingBuilder CreateBuilder()
    {
        return TestDistributedApplicationBuilder.Create();
    }
}
