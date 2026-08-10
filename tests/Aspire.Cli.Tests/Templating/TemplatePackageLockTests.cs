// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Cli.Tests.Templating;

public class TemplatePackageLockTests
{
    [Theory]
    [InlineData("ts-starter")]
    [InlineData("py-starter")]
    [InlineData("java-starter")]
    public void StarterFrontendPackageJson_UsesNpm10CompatibleBraceExpansionOverride(string templateName)
    {
        var filePath = Path.Combine(
            GetRepoRoot(),
            "src",
            "Aspire.Cli",
            "Templating",
            "Templates",
            templateName,
            "frontend",
            "package.json");

        using var packageJson = JsonDocument.Parse(File.ReadAllText(filePath));
        var overrides = packageJson.RootElement.GetProperty("overrides");

        Assert.True(overrides.TryGetProperty("minimatch@3.1.5", out var minimatchOverride));
        Assert.Equal(
            "2.1.3",
            minimatchOverride.GetProperty("brace-expansion").GetString());
        Assert.False(overrides.TryGetProperty("brace-expansion@1", out _));
    }

    [Theory]
    [InlineData("ts-starter")]
    [InlineData("py-starter")]
    [InlineData("java-starter")]
    public void StarterFrontendPackageLock_UsesPublicNpmRegistry(string templateName)
    {
        var filePath = Path.Combine(
            GetRepoRoot(),
            "src",
            "Aspire.Cli",
            "Templating",
            "Templates",
            templateName,
            "frontend",
            "package-lock.json");

        using var packageLock = JsonDocument.Parse(File.ReadAllText(filePath));

        var registryHosts = packageLock.RootElement
            .GetProperty("packages")
            .EnumerateObject()
            .Where(package => package.Value.TryGetProperty("resolved", out _))
            .Select(package => new Uri(package.Value.GetProperty("resolved").GetString()!).Host)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["registry.npmjs.org"], registryHosts);
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
