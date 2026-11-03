// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using GenerateTemplateManifest;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests.TemplateManifest;

public sealed class TemplateManifestNuGetTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RegistersDownloadedPackagesThatAreAbsentFromLibraries()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var environment = new TemplateManifestTestEnvironment(workspace, output);
        TemplateManifestTestEnvironment.CreatePackage(environment.BuiltFeed, "Local.DownloadFixture", "1.0.0");
        TemplateManifestTestEnvironment.CreatePackage(environment.ExternalFeed, "External.DownloadFixture", "1.0.0");
        TemplateManifestTestEnvironment.CreatePackage(environment.ExternalFeed, "External.DownloadFixture", "2.0.0");
        File.WriteAllText(Path.Combine(environment.TemplateDirectory, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageDownload Include="Local.DownloadFixture" Version="[1.0.0]" />
                <PackageDownload Include="External.DownloadFixture" Version="[1.0.0];[2.0.0]" />
              </ItemGroup>
            </Project>
            """);
        using var command = environment.CreateCommand();

        (await command.ExecuteAsync()).EnsureSuccessful();

        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(environment.RestoreDirectory, "Graph000", "obj", "project.assets.json")));
        Assert.Empty(assets.RootElement.GetProperty("libraries").EnumerateObject());
        Assert.Equal(2, assets.RootElement.GetProperty("project").GetProperty("frameworks").GetProperty("net11.0")
            .GetProperty("downloadDependencies").GetArrayLength());
        await Verifier.Verify(File.ReadAllText(environment.ManifestPath), "json").UseDirectory("Snapshots");
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("[1.0.0,)")]
    [InlineData("[1.0.0,2.0.0]")]
    [InlineData("(1.0.0,1.0.0]")]
    [InlineData("1.*")]
    [InlineData("not-a-version")]
    public void RejectsDownloadsWithoutAnExactVersion(string version)
    {
        using var assets = JsonDocument.Parse($$"""
            {
              "libraries": {},
              "project": {
                "frameworks": {
                  "net11.0": {
                    "downloadDependencies": [{"name": "Example", "version": "{{version}}"}]
                  }
                }
              }
            }
            """);
        var manifest = new ComponentManifest(new HashSet<string>());

        var exception = Assert.Throws<InvalidDataException>(() => manifest.RegisterNuGetAssets(assets.RootElement, "fixture.assets.json"));

        Assert.Contains("an exact version is required", exception.Message);
        Assert.Equal(0, manifest.Count);
    }
}
