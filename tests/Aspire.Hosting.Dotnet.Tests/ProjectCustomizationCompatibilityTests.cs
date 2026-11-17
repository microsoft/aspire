// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class ProjectCustomizationCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CustomIconReplacesFactoryDefault(bool v2)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = AddProject(builder, v2);

        var customized = project.WithIconName("CodeCircle", IconVariant.Regular);

        Assert.Same(project, customized);
        var icon = Assert.Single(project.Resource.Annotations.OfType<ResourceIconAnnotation>());
        Assert.Equal("CodeCircle", icon.IconName);
        Assert.Equal(IconVariant.Regular, icon.IconVariant);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImagePushOptionsPreserveBuilderIdentityAndConfiguration(bool v2)
    {
#pragma warning disable ASPIREPIPELINES003 // Image-push authoring APIs remain independently experimental for both factories.
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = AddProject(builder, v2);
        static void Configure(ContainerImagePushOptionsCallbackContext context) => context.Options.RemoteImageTag = "custom";

        var customized = project.WithImagePushOptions(Configure);

        Assert.Same(project, customized);
        var annotation = Assert.Single(project.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "repository/project",
            RemoteImageTag = "original"
        };
        await annotation.Callback(new ContainerImagePushOptionsCallbackContext
        {
            Resource = project.Resource,
            Options = options,
            CancellationToken = CancellationToken.None
        });

        Assert.Equal("repository/project", options.RemoteImageName);
        Assert.Equal("custom", options.RemoteImageTag);
#pragma warning restore ASPIREPIPELINES003
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalForcesProcessExecution(bool v2)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var project = AddProject(builder, v2);

#pragma warning disable ASPIRETERMINAL001 // Terminal support remains independently experimental for both factories.
        var customized = project.WithTerminal();
#pragma warning restore ASPIRETERMINAL001

        Assert.Same(project, customized);
        Assert.Single(project.Resource.Annotations.OfType<ForceProcessExecutionAnnotation>());
    }

    private static IResourceBuilder<IComputeResource> AddProject(IDistributedApplicationBuilder builder, bool v2)
    {
        if (v2)
        {
            return builder.AddDotnetProject("project", "Service.csproj", options => options.ExcludeLaunchProfile = true);
        }

#pragma warning disable CS0618 // Compare the same consumer customizations against the still-supported legacy factory.
        return builder.AddProject("project", "Service.csproj", options => options.ExcludeLaunchProfile = true);
#pragma warning restore CS0618
    }
}
