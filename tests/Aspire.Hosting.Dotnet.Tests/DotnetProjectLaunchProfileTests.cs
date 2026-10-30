// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001 // Verify the IDE launch metadata produced by the shared defaults.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectLaunchProfileTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(true, false, 5111, "My Project")]
    [InlineData(true, true, 5111, "My Project")]
    [InlineData(false, true, 5222, "Properties")]
    [InlineData(false, false, null, null)]
    public async Task VisualBasicUsesMyProjectBeforeProperties(
        bool includeMyProject,
        bool includeProperties,
        int? expectedPort,
        string? expectedSource)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = CreateProject(workspace.Path, ".VBPROJ");
        if (includeMyProject)
        {
            WriteLaunchSettings(path, "My Project", 5111);
        }
        if (includeProperties)
        {
            WriteLaunchSettings(path, "Properties", 5222);
        }
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddDotnetProject("service", Path.GetDirectoryName(path)!);
        var metadata = resource.Resource.GetProjectMetadata();

        var launchConfiguration = Assert.IsType<ProjectLaunchConfiguration>(
            await resource.Resource.CreateLaunchConfigurationAsync(
                LaunchConfigurationTestHelpers.CreateCallbackContext(resource.Resource, ExecutableLaunchMode.Debug)));

        Assert.False(metadata.IsFileBasedApp);
        Assert.Same(metadata, resource.Resource.GetProjectMetadata());
        Assert.Single(resource.Resource.Annotations.OfType<IProjectMetadata>());
        if (expectedPort is { } port)
        {
            var endpoint = Assert.Single(resource.Resource.Annotations.OfType<EndpointAnnotation>());
            Assert.Equal(port, endpoint.Port);
            endpoint.AllocatedEndpoint = new(endpoint, "localhost", port, targetPortExpression: "p0");
            var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                resource.Resource, serviceProvider: TestServiceProvider.Instance);
            Assert.Equal(expectedSource, environment["PROFILE_SOURCE"]);
            Assert.Equal($"{expectedSource}-first", launchConfiguration.LaunchProfile);
            var arguments = await ArgumentEvaluator.GetArgumentListAsync(resource.Resource);
            Assert.Equal(["--", "--profile-source", expectedSource], arguments.TakeLast(3));
        }
        else
        {
            Assert.Empty(resource.Resource.Annotations.OfType<EndpointAnnotation>());
            Assert.Equal("", launchConfiguration.LaunchProfile);
        }
    }

    [Theory]
    [InlineData(null, false, "My Project-first", 5111)]
    [InlineData("My Project-second", false, "My Project-second", 5112)]
    [InlineData(null, true, "", null)]
    [InlineData("My Project-second", true, "", null)]
    public async Task VisualBasicOptionsPreserveDefaultNamedAndExcludedProfiles(
        string? requestedProfile,
        bool excludeProfile,
        string expectedProfile,
        int? expectedPort)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = CreateProject(workspace.Path, ".vbproj");
        WriteLaunchSettings(path, "My Project", 5111);
        WriteLaunchSettings(path, "Properties", 5222);
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddDotnetProjectForPolyglot("service", path, new DotnetProjectOptions
        {
            LaunchProfileName = requestedProfile,
            ExcludeLaunchProfile = excludeProfile
        });

        var launchConfiguration = Assert.IsType<ProjectLaunchConfiguration>(
            await resource.Resource.CreateLaunchConfigurationAsync(
                LaunchConfigurationTestHelpers.CreateCallbackContext(resource.Resource, ExecutableLaunchMode.Debug)));

        Assert.Equal(expectedProfile, launchConfiguration.LaunchProfile);
        Assert.Equal(excludeProfile, launchConfiguration.DisableLaunchProfile);
        if (expectedPort is { } port)
        {
            Assert.Equal(port, Assert.Single(resource.Resource.Annotations.OfType<EndpointAnnotation>()).Port);
        }
        else
        {
            Assert.Empty(resource.Resource.Annotations.OfType<EndpointAnnotation>());
        }
    }

    [Theory]
    [InlineData(false, false, 5333)]
    [InlineData(true, false, 5111)]
    [InlineData(false, true, 5333)]
    [InlineData(true, true, null)]
    public void VisualBasicKestrelAndLaunchProfileExclusionRemainIndependent(
        bool excludeKestrel,
        bool excludeProfile,
        int? expectedPort)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = CreateProject(workspace.Path, ".vbproj");
        WriteLaunchSettings(path, "My Project", 5111);
        WriteLaunchSettings(path, "Properties", 5222);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "appsettings.json"), """
            {
              "Kestrel": {
                "Endpoints": {
                  "Http": {
                    "Url": "http://localhost:5333"
                  }
                }
              }
            }
            """);
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddDotnetProjectForPolyglot("service", path, new DotnetProjectOptions
        {
            ExcludeKestrelEndpoints = excludeKestrel,
            ExcludeLaunchProfile = excludeProfile
        });

        if (expectedPort is { } port)
        {
            Assert.Equal(port, Assert.Single(resource.Resource.Annotations.OfType<EndpointAnnotation>()).Port);
        }
        else
        {
            Assert.Empty(resource.Resource.Annotations.OfType<EndpointAnnotation>());
        }
    }

    [Fact]
    public void NamedVisualBasicProfileDoesNotFallBackToLowerPrioritySettings()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = CreateProject(workspace.Path, ".vbproj");
        WriteLaunchSettings(path, "My Project", 5111);
        WriteLaunchSettings(path, "Properties", 5222);
        using var builder = TestDistributedApplicationBuilder.Create();

        var exception = Assert.Throws<DistributedApplicationException>(() =>
            builder.AddDotnetProject("service", path, options => options.LaunchProfileName = "Properties-second"));

        Assert.Contains("Properties-second", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyVisualBasicServiceProfileLoadingRemainsPropertiesOnly(bool includeProperties)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = CreateProject(workspace.Path, ".vbproj");
        WriteLaunchSettings(path, "My Project", 5111);
        if (includeProperties)
        {
            WriteLaunchSettings(path, "Properties", 5222);
        }
        using var builder = TestDistributedApplicationBuilder.Create();

#pragma warning disable CS0618 // The legacy service loader deliberately retains its Properties-only behavior.
        var legacy = builder.AddProject("legacy", path);
#pragma warning restore CS0618
        var modern = builder.AddDotnetProject("modern", path);

        Assert.Equal(5111, Assert.Single(modern.Resource.Annotations.OfType<EndpointAnnotation>()).Port);
        if (includeProperties)
        {
            Assert.Equal(5222, Assert.Single(legacy.Resource.Annotations.OfType<EndpointAnnotation>()).Port);
        }
        else
        {
            Assert.Empty(legacy.Resource.Annotations.OfType<EndpointAnnotation>());
        }
    }

    [Theory]
    [InlineData(".csproj", false)]
    [InlineData(".csproj", true)]
    [InlineData(".fsproj", false)]
    [InlineData(".fsproj", true)]
    public void OtherProjectLanguagesIgnoreMyProjectAndProjectRunJson(string extension, bool includeProperties)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = CreateProject(workspace.Path, extension);
        var preferredSettings = WriteLaunchSettings(path, "My Project", 5111);
        File.Copy(preferredSettings, Path.ChangeExtension(path, ".run.json"));
        if (includeProperties)
        {
            WriteLaunchSettings(path, "Properties", 5222);
        }
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddDotnetProject("service", path);

        if (includeProperties)
        {
            Assert.Equal(5222, Assert.Single(resource.Resource.Annotations.OfType<EndpointAnnotation>()).Port);
        }
        else
        {
            Assert.Empty(resource.Resource.Annotations.OfType<EndpointAnnotation>());
        }
    }

    [Theory]
    [InlineData(".vbproj", false)]
    [InlineData(".cs", true)]
    public void RunJsonFallbackRemainsRestrictedToFileApps(string extension, bool expectEndpoint)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = CreateProject(workspace.Path, extension);
        var settings = WriteLaunchSettings(path, "Properties", 5222);
        File.Move(settings, Path.ChangeExtension(path, ".run.json"));
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddDotnetProject("service", path);

        if (expectEndpoint)
        {
            Assert.Equal(5222, Assert.Single(resource.Resource.Annotations.OfType<EndpointAnnotation>()).Port);
        }
        else
        {
            Assert.Empty(resource.Resource.Annotations.OfType<EndpointAnnotation>());
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"profiles":{}}""")]
    public void EmptyPreferredVisualBasicSettingsDoNotExposePropertiesProfiles(string content)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = CreateProject(workspace.Path, ".vbproj");
        var preferredSettings = WriteLaunchSettings(path, "My Project", 5111);
        File.WriteAllText(preferredSettings, content);
        WriteLaunchSettings(path, "Properties", 5222);
        using var builder = TestDistributedApplicationBuilder.Create();

        var resource = builder.AddDotnetProject("service", path);

        Assert.Empty(resource.Resource.Annotations.OfType<EndpointAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedPreferredVisualBasicSettingsFailUnlessProfilesAreExcluded(bool excludeProfiles)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = CreateProject(workspace.Path, ".vbproj");
        var preferredSettings = WriteLaunchSettings(path, "My Project", 5111);
        File.WriteAllText(preferredSettings, "{ malformed");
        WriteLaunchSettings(path, "Properties", 5222);
        using var builder = TestDistributedApplicationBuilder.Create();

        if (excludeProfiles)
        {
            var resource = builder.AddDotnetProject("service", path, options => options.ExcludeLaunchProfile = true);
            Assert.Empty(resource.Resource.Annotations.OfType<EndpointAnnotation>());
        }
        else
        {
            var exception = Assert.Throws<DistributedApplicationException>(() => builder.AddDotnetProject("service", path));
            Assert.Equal(
                $"Failed to get effective launch profile for project resource 'service'. There is malformed JSON in the project's launch settings file at '{preferredSettings}'.",
                exception.Message);
        }
    }

    private static string CreateProject(string root, string extension)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "Service"));
        var path = Path.Combine(directory.FullName, $"Service{extension}");
        File.WriteAllText(path, "<Project />");

        return path;
    }

    private static string WriteLaunchSettings(string projectPath, string directoryName, int port)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(projectPath)!, directoryName));
        var path = Path.Combine(directory.FullName, "launchSettings.json");
        var settings = new LaunchSettings
        {
            Profiles = new()
            {
                [$"{directoryName}-first"] = new()
                {
                    CommandName = "Project",
                    ApplicationUrl = $"http://localhost:{port}",
                    CommandLineArgs = $"--profile-source \"{directoryName}\"",
                    EnvironmentVariables = new() { ["PROFILE_SOURCE"] = directoryName }
                },
                [$"{directoryName}-second"] = new()
                {
                    CommandName = "Project",
                    ApplicationUrl = $"http://localhost:{port + 1}"
                }
            }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(settings));

        return path;
    }
}
