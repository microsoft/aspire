// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Managed.NuGet.Commands;
using Microsoft.DotNet.RemoteExecutor;
using NuGet.Configuration;
using NuGet.Frameworks;
using Xunit;

namespace Aspire.Managed.Tests.NuGet;

public class RestoreCommandTests(ITestOutputHelper outputHelper) : IDisposable
{
    private readonly TemporaryWorkspace _workspace = TemporaryWorkspace.Create(outputHelper);

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void RestoreCommand_RespectsNuGetConfigGlobalPackagesFolder()
    {
        var customPackagesDir = Path.GetFullPath(Path.Combine(_workspace.Path, "custom-packages"));
        var nugetConfigPath = Path.Combine(_workspace.Path, "NuGet.config");

        File.WriteAllText(nugetConfigPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="{customPackagesDir}" />
              </config>
            </configuration>
            """);

        // Run in a separate process so NUGET_PACKAGES env var from the parent
        // doesn't interfere. The env var takes precedence over config files
        // in NuGet's resolution order.
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment.Remove("NUGET_PACKAGES");

        RemoteExecutor.Invoke(static async (tempDirPath) =>
        {
            var command = RestoreCommand.Create();
            var outputDir = Path.Combine(tempDirPath, "obj");

            await command.Parse(["--package", "Fake.Package,1.0.0", "--no-nuget-org", "--output", outputDir, "--working-dir", tempDirPath]).InvokeAsync();
        }, _workspace.Path, options).Dispose();

        // NuGet writes packageFolders into project.assets.json with the resolved packages directory.
        var assetsContent = File.ReadAllText(Path.Combine(_workspace.Path, "obj", "project.assets.json"));
        Assert.Contains(JsonEncodedPath(customPackagesDir), assetsContent);
    }

    [Fact]
    public void RestoreCommand_RespectsNuGetPackagesEnvironmentVariable()
    {
        var customPackagesDir = Path.GetFullPath(Path.Combine(_workspace.Path, "env-packages"));

        // Run in a separate process with NUGET_PACKAGES set to the custom directory.
        // The env var takes priority over all config file settings.
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment["NUGET_PACKAGES"] = customPackagesDir;

        RemoteExecutor.Invoke(static async (tempDirPath) =>
        {
            var command = RestoreCommand.Create();
            var outputDir = Path.Combine(tempDirPath, "obj");

            await command.Parse(["--package", "Fake.Package,1.0.0", "--no-nuget-org", "--output", outputDir, "--working-dir", tempDirPath]).InvokeAsync();
        }, _workspace.Path, options).Dispose();

        // NuGet writes packageFolders into project.assets.json with the resolved packages directory.
        var assetsContent = File.ReadAllText(Path.Combine(_workspace.Path, "obj", "project.assets.json"));
        Assert.Contains(JsonEncodedPath(customPackagesDir), assetsContent);
    }

    [Fact]
    public void RestoreCommand_CliSourcesAreAppendedToConfigSources()
    {
        var nugetConfigPath = Path.Combine(_workspace.Path, "NuGet.config");
        var configSourcePath = Path.Combine(_workspace.Path, "config-source");
        var cliSourcePath = Path.Combine(_workspace.Path, "cli-source");

        File.WriteAllText(nugetConfigPath, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="ConfigSource" value="{configSourcePath}" />
              </packageSources>
            </configuration>
            """);

        // Run in a separate process so the parent's NuGet config doesn't interfere.
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment.Remove("NUGET_PACKAGES");

        RemoteExecutor.Invoke(static async (nugetConfig, cliSourcePath, tempDirPath) =>
        {
            var command = RestoreCommand.Create();
            var outputDir = Path.Combine(tempDirPath, "obj");

            // Pass --source in addition to the config source. Both should be used.
            await command.Parse([
                "--package", "Fake.Package,1.0.0",
                "--no-nuget-org",
                "--nuget-config", nugetConfig,
                "--source", cliSourcePath,
                "--output", outputDir,
                "--working-dir", tempDirPath]).InvokeAsync();
        }, nugetConfigPath, cliSourcePath, _workspace.Path, options).Dispose();

        // NuGet writes the resolved sources into project.assets.json regardless of
        // whether the restore succeeds. Verify both sources are present.
        var assetsContent = File.ReadAllText(Path.Combine(_workspace.Path, "obj", "project.assets.json"));
        Assert.Contains(JsonEncodedPath(configSourcePath), assetsContent);
        Assert.Contains(JsonEncodedPath(cliSourcePath), assetsContent);
    }

    [Theory]
    [InlineData("https://example.invalid/Feed/index.json", "https://example.invalid/feed/index.json")]
    [InlineData("https://example.invalid/feed?token=A", "https://example.invalid/feed?token=a")]
    public void RestoreCommand_PreservesCaseDistinctUriComponents(string firstSource, string secondSource)
    {
        var nugetConfigPath = Path.Combine(_workspace.Path, "NuGet.config");
        File.WriteAllText(
            nugetConfigPath,
            "<configuration><packageSources><clear /></packageSources></configuration>");
        var settings = Settings.LoadSpecificSettings(_workspace.Path, Path.GetFileName(nugetConfigPath));

        var sources = RestoreCommand.ResolvePackageSources(
            settings,
            [firstSource, secondSource],
            noNugetOrg: true);

        Assert.Equal(
            [firstSource, secondSource],
            sources.Select(static source => source.Source));
    }

    [Fact]
    public void RestoreCommand_UsesPlatformPathComparisonForLocalSources()
    {
        var nugetConfigPath = Path.Combine(_workspace.Path, "NuGet.config");
        File.WriteAllText(
            nugetConfigPath,
            "<configuration><packageSources><clear /></packageSources></configuration>");
        var settings = Settings.LoadSpecificSettings(_workspace.Path, Path.GetFileName(nugetConfigPath));
        var firstSource = Path.Combine(_workspace.Path, "Feed");
        var secondSource = Path.Combine(_workspace.Path, "feed");

        var sources = RestoreCommand.ResolvePackageSources(
            settings,
            [firstSource, secondSource],
            noNugetOrg: true);

        string[] expectedSources = OperatingSystem.IsWindows() ? [firstSource] : [firstSource, secondSource];
        Assert.Equal(expectedSources, sources.Select(static source => source.Source));
    }

    [Fact]
    public void RestoreCommand_DeduplicatesUriSchemeAndHostCasingWhileRetainingConfiguredCredentials()
    {
        var nugetConfigPath = Path.Combine(_workspace.Path, "NuGet.config");
        File.WriteAllText(
            nugetConfigPath,
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="HTTPS://HOST.example/Feed/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """);
        var settings = Settings.LoadSpecificSettings(_workspace.Path, Path.GetFileName(nugetConfigPath));

        var sources = RestoreCommand.ResolvePackageSources(
            settings,
            ["https://host.example/Feed/index.json"],
            noNugetOrg: true);

        var source = Assert.Single(sources);
        Assert.Equal("HTTPS://HOST.example/Feed/index.json", source.Source);
        Assert.NotNull(source.Credentials);
    }

    [Fact]
    public void RestoreCommand_IncludesNuGetConfigFallbackFoldersInRestoreMetadata()
    {
        var fallbackPackagesPath = Path.Combine(_workspace.Path, "fallback-packages");
        var nugetConfigPath = Path.Combine(_workspace.Path, "NuGet.config");
        File.WriteAllText(nugetConfigPath, $"""
            <configuration>
              <fallbackPackageFolders>
                <clear />
                <add key="fallback" value="{fallbackPackagesPath}" />
              </fallbackPackageFolders>
            </configuration>
            """);
        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment.Remove("NUGET_FALLBACK_PACKAGES");

        RemoteExecutor.Invoke(static (tempDirPath) =>
        {
            var settings = Settings.LoadSpecificSettings(tempDirPath, "NuGet.config");
            var packageSpec = RestoreCommand.BuildPackageSpec(
                [("Fake.Package", "1.0.0")],
                NuGetFramework.Parse("net10.0"),
                runtimeIdentifier: null,
                Path.Combine(tempDirPath, "obj"),
                [],
                settings);

            Assert.Equal(
                [Path.Combine(tempDirPath, "fallback-packages")],
                packageSpec.RestoreMetadata.FallbackFolders);
        }, _workspace.Path, options).Dispose();
    }

    [Fact]
    public void ConfigPathsCommand_DiscoversWorkspaceNuGetConfig()
    {
        var nugetConfigPath = Path.Combine(_workspace.Path, "NuGet.Config");
        File.WriteAllText(nugetConfigPath, "<configuration />");

        var configPaths = ConfigPathsCommand.GetConfigFilePaths(_workspace.Path);

        Assert.Contains(nugetConfigPath, configPaths, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Converts a file path to its JSON-escaped representation (e.g. backslashes doubled).
    /// </summary>
    private static string JsonEncodedPath(string path) =>
        path.Replace(@"\", @"\\");
}
