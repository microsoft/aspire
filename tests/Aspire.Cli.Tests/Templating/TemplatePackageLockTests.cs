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

    [Theory]
    [InlineData("ts-starter")]
    [InlineData("py-starter")]
    public void StarterAppHostPackageLock_UsesPublicNpmRegistry(string templateName)
    {
        // Guards the top-level (AppHost) lockfile in addition to the frontend one covered above.
        // A shipped lockfile pins the `resolved` registry for every dependency npm restores in a
        // generated starter, so these must resolve from the public npm registry — otherwise restore
        // fails for customers who cannot reach a private feed. See https://github.com/microsoft/aspire/issues/19370.
        var filePath = Path.Combine(
            GetRepoRoot(),
            "src",
            "Aspire.Cli",
            "Templating",
            "Templates",
            templateName,
            "package-lock.json");

        AssertPackageLockResolvesToPublicNpmRegistry(filePath);
    }

    [Fact]
    public void ProjectTemplateFrontendPackageLock_UsesPublicNpmRegistry()
    {
        var filePath = Path.Combine(
            GetRepoRoot(),
            "src",
            "Aspire.ProjectTemplates",
            "templates",
            "aspire-ts-cs-starter",
            "frontend",
            "package-lock.json");

        AssertPackageLockResolvesToPublicNpmRegistry(filePath);
    }

    private static void AssertPackageLockResolvesToPublicNpmRegistry(string filePath)
    {
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
