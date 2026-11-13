// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Aspire.TestUtilities;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests.TemplateManifest;

public sealed class TemplateManifestCacheTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RebuiltPackageRefreshesEveryGraphWithoutChangingItsVersion()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var environment = new TemplateManifestTestEnvironment(workspace, output);
        // Distinct projects share the same package, just like independently restored template
        // options. Check each assets file: unioning them can conceal a stale sibling graph.
        const int graphCount = 2;
        for (var i = 0; i < graphCount; i++)
        {
            File.WriteAllText(Path.Combine(environment.TemplateDirectory, $"Project{i}.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <AssemblyName>Project{{i}}</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup><PackageReference Include="Local.CacheFixture" Version="1.0.0-dev" /></ItemGroup>
                </Project>
                """);
        }
        TemplateManifestTestEnvironment.CreatePackage(environment.ExternalFeed, "External.CacheFixture", "1.0.0");
        TemplateManifestTestEnvironment.CreatePackage(environment.ExternalFeed, "External.CacheFixture", "2.0.0");
        var staleSharedPackage = TemplateManifestTestEnvironment.CreatePackage(environment.SharedCache, "Local.CacheFixture", "1.0.0-dev", ("External.CacheFixture", "[1.0.0]"));
        var sharedPackageBytes = File.ReadAllBytes(staleSharedPackage);
        var configBefore = File.ReadAllText(environment.ConfigPath);

        using var command = environment.CreateCommand()
            // Restore one graph before checking its siblings for no-op eligibility, so
            // cache repopulation cannot depend on thread scheduling in this regression test.
            .WithEnvironmentVariable("RestoreDisableParallel", "true");

        TemplateManifestTestEnvironment.CreatePackage(environment.BuiltFeed, "Local.CacheFixture", "1.0.0-dev", ("External.CacheFixture", "[1.0.0]"));
        (await command.ExecuteAsync()).EnsureSuccessful();
        AssertGraphs(environment.RestoreDirectory, graphCount, "1.0.0");
        var firstManifest = JsonNode.Parse(File.ReadAllText(environment.ManifestPath));

        // Keep the private cache and all assets files. Only replace the same-version nupkg.
        TemplateManifestTestEnvironment.CreatePackage(environment.BuiltFeed, "Local.CacheFixture", "1.0.0-dev", ("External.CacheFixture", "[2.0.0]"));
        (await command.ExecuteAsync()).EnsureSuccessful();
        AssertGraphs(environment.RestoreDirectory, graphCount, "2.0.0");
        var secondManifest = JsonNode.Parse(File.ReadAllText(environment.ManifestPath));

        var manifests = new JsonObject
        {
            ["First"] = firstManifest,
            ["Second"] = secondManifest
        };
        await Verifier.Verify(manifests.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json").UseDirectory("Snapshots");
        Assert.Equal(sharedPackageBytes, File.ReadAllBytes(staleSharedPackage));
        Assert.Equal(configBefore, File.ReadAllText(environment.ConfigPath));
        Assert.Equal([Path.GetFileName(staleSharedPackage)], Directory.GetFiles(environment.SharedCache).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(environment.SharedCache));
    }

    private static void AssertGraphs(string restoreDirectory, int expectedCount, string dependencyVersion)
    {
        var solution = XDocument.Load(Path.Combine(restoreDirectory, "TemplateDependencies.slnx"));
        var projects = solution.Root!.Elements("Project").ToArray();
        Assert.Equal(expectedCount, projects.Length);
        foreach (var project in projects)
        {
            var path = project.Attribute("Path")!.Value.Replace('/', Path.DirectorySeparatorChar);
            var directory = Path.GetDirectoryName(Path.Combine(restoreDirectory, path))!;
            using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "obj", "project.assets.json")));
            Assert.Equal(
                [$"External.CacheFixture/{dependencyVersion}", "Local.CacheFixture/1.0.0-dev"],
                assets.RootElement.GetProperty("libraries").EnumerateObject().Select(p => p.Name).Order());
            var targets = assets.RootElement.GetProperty("targets").EnumerateObject().ToArray();
            var target = Assert.Single(targets);
            Assert.Equal($"[{dependencyVersion}]", target.Value.GetProperty("Local.CacheFixture/1.0.0-dev")
                .GetProperty("dependencies").GetProperty("External.CacheFixture").GetString());
        }
    }
}
