// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectPathTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(".csproj", false, "CodeCsRectangle")]
    [InlineData(".fsproj", false, "CodeFsRectangle")]
    [InlineData(".vbproj", false, "CodeVbRectangle")]
    [InlineData(".CSPROJ", false, "CodeCsRectangle")]
    [InlineData(".FSPROJ", false, "CodeFsRectangle")]
    [InlineData(".VBPROJ", false, "CodeVbRectangle")]
    [InlineData(".cs", true, "CodeCsRectangle")]
    [InlineData(".CS", true, "CodeCsRectangle")]
    public async Task ExplicitSupportedFilesRetainTheirProjectOrFileAppCategory(string extension, bool fileBased, string iconName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = Path.Combine(workspace.Path, $"Service{extension}");
        File.WriteAllText(projectPath, "<Project />");
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var resource = builder.AddDotnetProject("service", projectPath, options => options.ExcludeLaunchProfile = true);
        var relativeResource = builder.AddDotnetProject("relative", Path.GetFileName(projectPath), options => options.ExcludeLaunchProfile = true);

        var metadata = resource.Resource.GetProjectMetadata();
        Assert.Equal(projectPath, metadata.ProjectPath);
        Assert.Equal(projectPath, relativeResource.Resource.GetProjectMetadata().ProjectPath);
        Assert.Equal(fileBased, metadata.IsFileBasedApp);
        Assert.True(metadata.SuppressBuild);
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource);
        Assert.Equal(["run", fileBased ? "--file" : "--project", projectPath, "--no-build"], arguments.Take(4));
        Assert.Equal(workspace.Path, resource.Resource.WorkingDirectory);
        Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>());
        Assert.Equal(iconName, Assert.Single(resource.Resource.Annotations.OfType<ResourceIconAnnotation>()).IconName);
        Assert.Equal(iconName, Assert.Single(relativeResource.Resource.Annotations.OfType<ResourceIconAnnotation>()).IconName);
    }

    [Theory]
    [InlineData(".csproj", "CodeCsRectangle")]
    [InlineData(".fsproj", "CodeFsRectangle")]
    [InlineData(".vbproj", "CodeVbRectangle")]
    [InlineData(".CSPROJ", "CodeCsRectangle")]
    [InlineData(".FSPROJ", "CodeFsRectangle")]
    [InlineData(".VBPROJ", "CodeVbRectangle")]
    public void DirectoryShorthandResolvesExactlyOneSupportedProjectAcrossLanguages(string extension, string iconName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = workspace.CreateDirectory("Service");
        var projectPath = Path.Combine(directory.FullName, $"Service{extension}");
        File.WriteAllText(projectPath, "<Project />");
        File.WriteAllText(Path.Combine(directory.FullName, "app.cs"), "");
        File.WriteAllText(Path.Combine(directory.FullName, "script.fsx"), "");
        var nested = directory.CreateSubdirectory("nested");
        File.WriteAllText(Path.Combine(nested.FullName, "Other.csproj"), "<Project />");
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);

        var absolute = builder.AddDotnetProject("absolute", directory.FullName, options => options.ExcludeLaunchProfile = true);
        var relative = builder.AddDotnetProject("relative", "Service", options => options.ExcludeLaunchProfile = true);

        Assert.Equal(projectPath, absolute.Resource.GetProjectMetadata().ProjectPath);
        Assert.Equal(projectPath, relative.Resource.GetProjectMetadata().ProjectPath);
        Assert.Equal(directory.FullName, absolute.Resource.WorkingDirectory);
        Assert.Equal(directory.FullName, relative.Resource.WorkingDirectory);
        Assert.False(absolute.Resource.GetProjectMetadata().IsFileBasedApp);
        Assert.Single(Assert.Single(builder.Resources.OfType<DotnetProjectBuildResource>()).ProjectPaths);
        Assert.Equal(iconName, Assert.Single(absolute.Resource.Annotations.OfType<ResourceIconAnnotation>()).IconName);
        Assert.Equal(iconName, Assert.Single(relative.Resource.Annotations.OfType<ResourceIconAnnotation>()).IconName);

        File.WriteAllText(Path.Combine(directory.FullName, "Later.vbproj"), "<Project />");
        Assert.Equal(projectPath, absolute.Resource.GetProjectMetadata().ProjectPath);
        Assert.Equal(projectPath, relative.Resource.GetProjectMetadata().ProjectPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyPathUsesTheAppHostDirectoryWithoutEagerlyRejectingUnresolvablePaths(bool includeProject)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "Service.fsproj");
        if (includeProject)
        {
            File.WriteAllText(path, "<Project />");
        }
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);

        var resource = builder.AddDotnetProject("service", "", options => options.ExcludeLaunchProfile = true);

        Assert.Equal(
            includeProject ? path : workspace.Path,
            Path.TrimEndingDirectorySeparator(resource.Resource.GetProjectMetadata().ProjectPath));
    }

    [Theory]
    [InlineData(".csproj", ".csproj")]
    [InlineData(".csproj", ".fsproj")]
    [InlineData(".csproj", ".vbproj")]
    [InlineData(".fsproj", ".vbproj")]
    [InlineData(".fsproj", ".fsproj")]
    [InlineData(".vbproj", ".vbproj")]
    [InlineData(".FSPROJ", ".VBPROJ")]
    [InlineData(null, null)]
    public async Task UnresolvableDirectoriesFailAtResourceStartAndRequireAnExplicitFilename(
        string? firstExtension,
        string? secondExtension)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = workspace.CreateDirectory("Service");
        if (firstExtension is not null)
        {
            File.WriteAllText(Path.Combine(directory.FullName, $"First{firstExtension}"), "<Project />");
            File.WriteAllText(Path.Combine(directory.FullName, $"Second{secondExtension}"), "<Project />");
        }
        File.WriteAllText(Path.Combine(directory.FullName, "app.cs"), "");
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);

        var resource = builder.AddDotnetProject("service", directory.FullName, options => options.ExcludeLaunchProfile = true);

        Assert.Equal(directory.FullName, resource.Resource.GetProjectMetadata().ProjectPath);
        Assert.False(resource.Resource.GetProjectMetadata().SuppressBuild);
        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
        using var app = builder.Build();
        await EventingTestHelpers.SubscribeEventingSubscribersAsync(app, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(resource.Resource, app.Services),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Path to .NET project for resource 'service' could not be determined. The directory '{directory.FullName}' must contain exactly one .csproj, .fsproj, or .vbproj file. Specify an explicit project filename when multiple supported projects exist.",
            exception.Message);
    }

    [Theory]
    [InlineData("Service.fs")]
    [InlineData("Service.fsx")]
    [InlineData("Service.vb")]
    [InlineData("Service.vcxproj")]
    [InlineData("Service.proj")]
    [InlineData("MissingDirectory")]
    public async Task UnsupportedAndMissingPathsFailAtResourceStart(string fileName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, fileName);
        if (Path.HasExtension(fileName))
        {
            File.WriteAllText(path, "");
        }
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);
        var resource = builder.AddDotnetProject("service", path, options => options.ExcludeLaunchProfile = true);
        using var app = builder.Build();
        await EventingTestHelpers.SubscribeEventingSubscribersAsync(app, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(resource.Resource, app.Services),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            $"The .NET app path '{path}' for resource 'service' is invalid. The path must be to a .cs file, an SDK-style .csproj, .fsproj, or .vbproj file, or a directory containing exactly one supported project.",
            exception.Message);
        Assert.Empty(builder.Resources.OfType<DotnetProjectBuildResource>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LegacyDirectoryResolutionRemainsCSharpOnly(bool includeCSharpProject)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = workspace.CreateDirectory("Service");
        var csharpPath = Path.Combine(directory.FullName, "Service.csproj");
        if (includeCSharpProject)
        {
            File.WriteAllText(csharpPath, "<Project />");
        }
        File.WriteAllText(Path.Combine(directory.FullName, "Service.fsproj"), "<Project />");
        File.WriteAllText(Path.Combine(directory.FullName, "Service.vbproj"), "<Project />");
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.ProjectDirectory = workspace.Path,
            outputHelper);

#pragma warning disable CS0618 // The legacy default resolver must not adopt V2's broader directory policy.
        var legacy = builder.AddProject("legacy", directory.FullName, options => options.ExcludeLaunchProfile = true);
        var legacyFileApp = builder.AddCSharpApp("legacy-file", directory.FullName, options => options.ExcludeLaunchProfile = true);
#pragma warning restore CS0618
        var modern = builder.AddDotnetProject("modern", directory.FullName, options => options.ExcludeLaunchProfile = true);

        Assert.Equal(includeCSharpProject ? csharpPath : directory.FullName, legacy.Resource.GetProjectMetadata().ProjectPath);
        Assert.Equal(includeCSharpProject ? csharpPath : directory.FullName, legacyFileApp.Resource.GetProjectMetadata().ProjectPath);
        Assert.Equal(directory.FullName, modern.Resource.GetProjectMetadata().ProjectPath);
    }

    [Theory]
    [InlineData(".fsproj")]
    [InlineData(".vbproj")]
    public void LegacyDirectoryResolutionDoesNotSelectASingleNonCSharpProject(string extension)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, $"Service{extension}");
        File.WriteAllText(path, "<Project />");
        using var builder = TestDistributedApplicationBuilder.Create();

#pragma warning disable CS0618 // The legacy default resolver remains C#-only even when V2 has a unique candidate.
        var legacy = builder.AddProject("legacy", workspace.Path, options => options.ExcludeLaunchProfile = true);
#pragma warning restore CS0618
        var modern = builder.AddDotnetProject("modern", workspace.Path, options => options.ExcludeLaunchProfile = true);

        Assert.Equal(workspace.Path, legacy.Resource.GetProjectMetadata().ProjectPath);
        Assert.Equal(path, modern.Resource.GetProjectMetadata().ProjectPath);
    }

    [Theory]
    [InlineData(".csproj")]
    [InlineData(".fsproj")]
    [InlineData(".vbproj")]
    [InlineData(".cs")]
    public void ExplicitIconOverridesTheLanguageDefault(string extension)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, $"Service{extension}");
        File.WriteAllText(path, "");
        using var builder = TestDistributedApplicationBuilder.Create();

        var resource = builder.AddDotnetProject("service", path)
            .WithIconName("CodeCircle", IconVariant.Regular);

        var icon = Assert.Single(resource.Resource.Annotations.OfType<ResourceIconAnnotation>());
        Assert.Equal("CodeCircle", icon.IconName);
        Assert.Equal(IconVariant.Regular, icon.IconVariant);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(".csproj", ".csproj")]
    [InlineData(".csproj", ".fsproj")]
    [InlineData(".csproj", ".vbproj")]
    [InlineData(".fsproj", ".vbproj")]
    [InlineData(".FSPROJ", ".VBPROJ")]
    public void UnresolvableDirectoriesUseActionableErrorsWithDefaultOptions(
        string? firstExtension,
        string? secondExtension)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var directory = workspace.CreateDirectory("Service");
        if (firstExtension is not null)
        {
            File.WriteAllText(Path.Combine(directory.FullName, $"First{firstExtension}"), "<Project />");
            File.WriteAllText(Path.Combine(directory.FullName, $"Second{secondExtension}"), "<Project />");
        }
        File.WriteAllText(Path.Combine(directory.FullName, "app.cs"), "");
        using var builder = TestDistributedApplicationBuilder.Create();

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddDotnetProject("service", directory.FullName));

        Assert.Equal(
            $"Path to .NET project for resource 'service' could not be determined. The directory '{directory.FullName}' must contain exactly one .csproj, .fsproj, or .vbproj file. Specify an explicit project filename when multiple supported projects exist.",
            exception.Message);
    }

    [Theory]
    [InlineData("Service.fs")]
    [InlineData("Service.fsx")]
    [InlineData("Service.vb")]
    [InlineData("Service.vcxproj")]
    [InlineData("Service.proj")]
    public async Task ExistingUnsupportedFilesStillFailAtResourceStartWithDefaultOptions(string fileName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, fileName);
        File.WriteAllText(path, "");
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddDotnetProject("service", path);
        using var app = builder.Build();
        await EventingTestHelpers.SubscribeEventingSubscribersAsync(app, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(resource.Resource, app.Services),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            $"The .NET app path '{path}' for resource 'service' is invalid. The path must be to a .cs file, an SDK-style .csproj, .fsproj, or .vbproj file, or a directory containing exactly one supported project.",
            exception.Message);
    }

    [Fact]
    public void MissingDirectoryUsesActionableErrorWithDefaultOptions()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "MissingDirectory");
        using var builder = TestDistributedApplicationBuilder.Create();

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddDotnetProject("service", path));

        Assert.Equal(
            $"The .NET app path '{path}' for resource 'service' is invalid. The path must be to a .cs file, an SDK-style .csproj, .fsproj, or .vbproj file, or a directory containing exactly one supported project.",
            exception.Message);
    }

    [Theory]
    [InlineData(".csproj")]
    [InlineData(".fsproj")]
    [InlineData(".vbproj")]
    [InlineData(".cs")]
    public void MissingSupportedFilesKeepTheirExistingValidationBoundary(string extension)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, $"Missing{extension}");
        using var builder = TestDistributedApplicationBuilder.Create();

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddDotnetProject("default", path));

        Assert.Equal($"Project file '{path}' was not found.", exception.Message);
        var excluded = builder.AddDotnetProject("excluded", path, options => options.ExcludeLaunchProfile = true);
        Assert.Equal(path, excluded.Resource.GetProjectMetadata().ProjectPath);
    }
}
