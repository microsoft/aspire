// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Maui;

namespace Aspire.Hosting.Tests;

/// <summary>
/// Tests for AddMauiProject resource creation and input validation.
/// </summary>
public class MauiProjectResourceValidationTests
{
    [Fact]
    public void AddMauiProject_NullBuilder_ThrowsArgumentNullException()
    {
        IDistributedApplicationBuilder builder = null!;

        Assert.Throws<ArgumentNullException>(() =>
            builder.AddMauiProject("mauiapp", "path/to/project.csproj"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AddMauiProject_NullOrEmptyName_ThrowsArgumentException(string? name)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        Assert.ThrowsAny<ArgumentException>(() =>
            appBuilder.AddMauiProject(name!, "path/to/project.csproj"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AddMauiProject_NullOrEmptyProjectPath_ThrowsArgumentException(string? projectPath)
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        Assert.ThrowsAny<ArgumentException>(() =>
            appBuilder.AddMauiProject("mauiapp", projectPath!));
    }

    [Fact]
    public void AddMauiProject_SetsProjectPathOnResource()
    {
        var projectPath = "path/to/MyMauiApp.csproj";
        var appBuilder = DistributedApplication.CreateBuilder();

        var maui = appBuilder.AddMauiProject("mauiapp", projectPath);

        Assert.Equal(projectPath, maui.Resource.ProjectPath);
    }

    [Fact]
    public void AddMauiProject_SetsResourceName()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var maui = appBuilder.AddMauiProject("my-maui-app", "path/to/project.csproj");

        Assert.Equal("my-maui-app", maui.Resource.Name);
    }

    [Fact]
    public void AddMauiProject_ResourceIsNotAddedToAppModelResources()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        appBuilder.AddMauiProject("mauiapp", "path/to/project.csproj");

        // The MauiProjectResource should not be in the top-level resources
        // (it uses CreateResourceBuilder, not AddResource, so it stays invisible in the dashboard)
        Assert.DoesNotContain(appBuilder.Resources, r => r is MauiProjectResource);
    }

    [Fact]
    public void MauiProjectResource_NullProjectPath_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new MauiProjectResource("name", null!));
    }
}
