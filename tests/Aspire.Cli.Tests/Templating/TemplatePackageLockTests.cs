// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Cli.Tests.Templating;

public class TemplatePackageLockTests
{
    [Fact]
    public void TypeScriptStarterAppHostPackageJson_UsesPackageManagerNeutralAliases()
    {
        var filePath = Path.Combine(
            GetRepoRoot(),
            "src",
            "Aspire.Cli",
            "Templating",
            "Templates",
            "ts-starter",
            "package.json");

        using var packageJson = JsonDocument.Parse(File.ReadAllText(filePath));
        var scripts = packageJson.RootElement.GetProperty("scripts");

        Assert.Equal("eslint apphost.mts", scripts.GetProperty("lint").GetString());
        Assert.Equal("eslint apphost.mts && aspire run", scripts.GetProperty("dev").GetString());
        Assert.Equal("eslint apphost.mts && tsc -p tsconfig.apphost.json", scripts.GetProperty("build").GetString());
        Assert.Equal("tsc --watch -p tsconfig.apphost.json", scripts.GetProperty("watch").GetString());
    }

    [Theory]
    [InlineData("Aspire.Cli", "ts-starter")]
    [InlineData("Aspire.Cli", "py-starter")]
    [InlineData("Aspire.Cli", "java-starter")]
    [InlineData("Aspire.ProjectTemplates", "aspire-ts-cs-starter")]
    public void StarterFrontendPackageJson_UsesNpm10CompatibleBraceExpansionOverride(string projectName, string templateName)
    {
        var filePath = Path.Combine(
            GetTemplateDirectory(projectName, templateName),
            "frontend",
            "package.json");

        using var packageJson = JsonDocument.Parse(File.ReadAllText(filePath));
        var overrides = packageJson.RootElement.GetProperty("overrides");

        Assert.True(overrides.TryGetProperty("minimatch@3.1.5", out var minimatchOverride));
        Assert.Equal(
            "2.1.7",
            minimatchOverride.GetProperty("brace-expansion").GetString());
        Assert.Equal("5.0.12", overrides.GetProperty("brace-expansion@>=5").GetString());
        Assert.False(overrides.TryGetProperty("brace-expansion@1", out _));
    }

    [Theory]
    [InlineData("Aspire.Cli", "ts-starter", "frontend")]
    [InlineData("Aspire.Cli", "py-starter", "frontend")]
    [InlineData("Aspire.Cli", "java-starter", "frontend")]
    [InlineData("Aspire.Cli", "ts-starter", "")]
    [InlineData("Aspire.Cli", "py-starter", "")]
    [InlineData("Aspire.ProjectTemplates", "aspire-ts-cs-starter", "frontend")]
    public void StarterPackageLock_UsesPatchedBraceExpansion(string projectName, string templateName, string subdirectory)
    {
        var filePath = Path.Combine(
            GetTemplateDirectory(projectName, templateName),
            subdirectory,
            "package-lock.json");

        using var packageLock = JsonDocument.Parse(File.ReadAllText(filePath));
        var packages = packageLock.RootElement.GetProperty("packages").EnumerateObject()
            .Where(package => package.Name.EndsWith("/brace-expansion", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(packages);
        Assert.All(packages, package =>
        {
            var version = Version.Parse(package.Value.GetProperty("version").GetString()!);
            var minimum = version.Major switch
            {
                2 => new Version(2, 1, 6),
                5 => new Version(5, 0, 11),
                _ => throw new InvalidOperationException($"Unexpected brace-expansion major version: {version}")
            };

            Assert.True(version >= minimum, $"brace-expansion {version} predates the security fixes in {minimum}.");
        });
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

    [Theory]
    [InlineData("ts-starter")]
    [InlineData("py-starter")]
    [InlineData("java-starter")]
    public void StarterFrontendPackageLock_DoesNotIncludeLegacyEslintYamlLoader(string templateName)
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
        var packages = packageLock.RootElement.GetProperty("packages");

        Assert.False(packages.TryGetProperty("node_modules/@eslint/eslintrc", out _));
        Assert.False(packages.TryGetProperty("node_modules/js-yaml", out _));
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

    private static string GetTemplateDirectory(string projectName, string templateName)
        => projectName switch
        {
            "Aspire.Cli" => Path.Combine(GetRepoRoot(), "src", projectName, "Templating", "Templates", templateName),
            "Aspire.ProjectTemplates" => Path.Combine(GetRepoRoot(), "src", projectName, "templates", templateName),
            _ => throw new ArgumentException($"Unexpected template project: {projectName}", nameof(projectName))
        };

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
