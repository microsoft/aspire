// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Templates.Tests;
using Xunit;

namespace Aspire.Acquisition.Tests;

/// <summary>
/// Tests for checked-in source-of-truth sidecar JSON files and packager template invariants.
/// These are build-time / repo-state assertions that don't require running any scripts.
/// </summary>
public class SidecarSourceFileTests
{
    private static string RepoRoot => TestUtils.FindRepoRoot()?.FullName
        ?? throw new InvalidOperationException("Could not find repository root");

    [Fact]
    public async Task DotnetToolSidecar_ExistsWithCorrectRouteAndUpdateCommand()
    {
        var sidecarPath = Path.Combine(RepoRoot, "src", "Aspire.Cli", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Dotnet-tool sidecar not found at: {sidecarPath}");

        var json = await File.ReadAllTextAsync(sidecarPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("dotnet-tool", doc.RootElement.GetProperty("route").GetString());

        var updateCommand = doc.RootElement.GetProperty("updateCommand").GetString();
        Assert.NotNull(updateCommand);
        Assert.Contains("dotnet tool update", updateCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Aspire.Cli", updateCommand);
    }

    [Fact]
    public async Task WingetSidecar_ExistsWithCorrectRouteAndUpdateCommand()
    {
        var sidecarPath = Path.Combine(RepoRoot, "eng", "winget", "microsoft.aspire", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Winget sidecar not found at: {sidecarPath}");

        var json = await File.ReadAllTextAsync(sidecarPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("winget", doc.RootElement.GetProperty("route").GetString());

        var updateCommand = doc.RootElement.GetProperty("updateCommand").GetString();
        Assert.NotNull(updateCommand);
        Assert.Contains("winget", updateCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft.Aspire", updateCommand);
    }

    [Fact]
    public async Task BrewSidecar_ExistsWithCorrectRouteAndUpdateCommand()
    {
        var sidecarPath = Path.Combine(RepoRoot, "eng", "homebrew", ".aspire-install.json");
        Assert.True(File.Exists(sidecarPath), $"Brew sidecar not found at: {sidecarPath}");

        var json = await File.ReadAllTextAsync(sidecarPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("brew", doc.RootElement.GetProperty("route").GetString());

        var updateCommand = doc.RootElement.GetProperty("updateCommand").GetString();
        Assert.NotNull(updateCommand);
        Assert.Contains("brew", updateCommand, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aspire", updateCommand, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrewCaskTemplate_HasNoStaleZapTarget()
    {
        var templatePath = Path.Combine(RepoRoot, "eng", "homebrew", "aspire.rb.template");
        Assert.True(File.Exists(templatePath), $"Brew cask template not found at: {templatePath}");

        var content = File.ReadAllText(templatePath);
        Assert.DoesNotContain("brew-stable", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("installs/brew", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrewCaskTemplate_WritesRouteSidecarInPostflight()
    {
        var templatePath = Path.Combine(RepoRoot, "eng", "homebrew", "aspire.rb.template");
        Assert.True(File.Exists(templatePath), $"Brew cask template not found at: {templatePath}");

        var content = File.ReadAllText(templatePath);
        Assert.Contains("postflight do", content);
        Assert.Contains(".aspire-install.json", content);
        Assert.Contains("brew upgrade aspire", content);
    }

    [Fact]
    public void WingetInstallerTemplate_HasScopeUser()
    {
        var templatePath = Path.Combine(RepoRoot, "eng", "winget", "microsoft.aspire", "Aspire.installer.yaml.template");
        Assert.True(File.Exists(templatePath), $"Winget installer template not found at: {templatePath}");

        var content = File.ReadAllText(templatePath);
        Assert.Contains("Scope: user", content);
    }

    [Fact]
    public void PrereleaseWingetManifestDirectory_IsAbsent()
    {
        // AC#10: the microsoft.aspire.prerelease winget manifest directory must not exist.
        // All prerelease installation goes through the script route rather than a separate winget package.
        var prereleaseDir = Path.Combine(RepoRoot, "eng", "winget", "microsoft.aspire.prerelease");
        Assert.False(
            Directory.Exists(prereleaseDir),
            $"Prerelease winget manifest directory must not exist: {prereleaseDir}");
    }
}
