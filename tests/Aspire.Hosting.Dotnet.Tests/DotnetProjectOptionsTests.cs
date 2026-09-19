// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOTNETPROJECT001
#pragma warning disable ASPIREEXTENSION001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetProjectOptionsTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(null, false, "first", 5111)]
    [InlineData("second", false, "second", 5222)]
    [InlineData(null, true, "", null)]
    [InlineData("second", true, "", null)]
    public async Task PolyglotOptionsPreserveLaunchProfileSelection(
        string? requestedProfile,
        bool excludeLaunchProfile,
        string expectedProfile,
        int? expectedPort)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = await CreateProjectAsync(workspace.Path, includeKestrel: false);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var resource = builder.AddDotnetProjectForPolyglot("svc", projectPath, new DotnetProjectOptions
        {
            LaunchProfileName = requestedProfile,
            ExcludeLaunchProfile = excludeLaunchProfile
        });

        var callbackContext = LaunchConfigurationTestHelpers.CreateCallbackContext(resource.Resource, ExecutableLaunchMode.Debug);
        var launchConfig = Assert.IsType<ProjectLaunchConfiguration>(
            await resource.Resource.CreateLaunchConfigurationAsync(callbackContext));

        Assert.Equal(excludeLaunchProfile, launchConfig.DisableLaunchProfile);
        Assert.Equal(expectedProfile, launchConfig.LaunchProfile);
        AssertEndpointPort(resource.Resource, expectedPort);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OmittedAndEmptyPolyglotOptionsUseDefaultProfile(bool omitOptions)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = await CreateProjectAsync(workspace.Path, includeKestrel: false);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var resource = omitOptions
            ? builder.AddDotnetProjectForPolyglot("svc", projectPath)
            : builder.AddDotnetProjectForPolyglot("svc", projectPath, new DotnetProjectOptions());

        var endpoint = Assert.Single(resource.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal(5111, endpoint.Port);
    }

    [Theory]
    [InlineData(false, false, 5333)]
    [InlineData(true, false, 5111)]
    [InlineData(false, true, 5333)]
    [InlineData(true, true, null)]
    public async Task PolyglotOptionsControlKestrelAndLaunchProfileEndpointsIndependently(
        bool excludeKestrelEndpoints,
        bool excludeLaunchProfile,
        int? expectedPort)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectPath = await CreateProjectAsync(workspace.Path, includeKestrel: true);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var resource = builder.AddDotnetProjectForPolyglot("svc", projectPath, new DotnetProjectOptions
        {
            ExcludeKestrelEndpoints = excludeKestrelEndpoints,
            ExcludeLaunchProfile = excludeLaunchProfile
        });

        AssertEndpointPort(resource.Resource, expectedPort);
    }

    private static void AssertEndpointPort(IResource resource, int? expectedPort)
    {
        var endpoints = resource.Annotations.OfType<EndpointAnnotation>();
        if (expectedPort is { } port)
        {
            Assert.Equal(port, Assert.Single(endpoints).Port);
        }
        else
        {
            Assert.Empty(endpoints);
        }
    }

    private static async Task<string> CreateProjectAsync(string workspacePath, bool includeKestrel)
    {
        var projectDirectory = Directory.CreateDirectory(Path.Combine(workspacePath, "Service"));
        var projectPath = Path.Combine(projectDirectory.FullName, "Service.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        var propertiesDirectory = Directory.CreateDirectory(Path.Combine(projectDirectory.FullName, "Properties"));
        await File.WriteAllTextAsync(Path.Combine(propertiesDirectory.FullName, "launchSettings.json"), """
            {
              "profiles": {
                "first": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:5111"
                },
                "second": {
                  "commandName": "Project",
                  "applicationUrl": "http://localhost:5222"
                }
              }
            }
            """);
        if (includeKestrel)
        {
            await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "appsettings.json"), """
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
        }

        return projectPath;
    }
}
