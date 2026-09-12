// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Managed.NuGet.Commands;
using Aspire.Shared;
using Microsoft.DotNet.RemoteExecutor;
using NuGet.Configuration;
using NuGet.Frameworks;
using Xunit;

namespace Aspire.Managed.Tests.NuGet;

public class RestoreCommandTests(ITestOutputHelper outputHelper) : IDisposable
{
    private static readonly byte[] s_sourceIdentityKey = new byte[NuGetSourceIdentity.KeySizeInBytes];
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

    [Fact]
    public void RestoreCommand_LoadsExplicitConfigsFromHighestToLowestPrecedence()
    {
        var lowerConfigPath = Path.Combine(_workspace.Path, "lower.config");
        var higherConfigPath = Path.Combine(_workspace.Path, "higher.config");
        var lowerSourcePath = Path.Combine(_workspace.Path, "lower-source");
        var higherSourcePath = Path.Combine(_workspace.Path, "higher-source");
        File.WriteAllText(
            lowerConfigPath,
            $"""
            <configuration>
              <packageSources>
                <add key="lower" value="{lowerSourcePath}" />
              </packageSources>
            </configuration>
            """);
        File.WriteAllText(
            higherConfigPath,
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="higher" value="{higherSourcePath}" />
              </packageSources>
            </configuration>
            """);

        RemoteExecutor.Invoke(static async (higherConfig, lowerConfig, tempDirPath) =>
        {
            var command = RestoreCommand.Create();
            await command.Parse([
                "--package", "Fake.Package,1.0.0",
                "--no-nuget-org",
                "--nuget-config", higherConfig,
                "--nuget-config", lowerConfig,
                "--output", Path.Combine(tempDirPath, "obj"),
                "--working-dir", tempDirPath]).InvokeAsync();
        }, higherConfigPath, lowerConfigPath, _workspace.Path).Dispose();

        using var assets = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_workspace.Path, "obj", "project.assets.json")));
        var sources = assets.RootElement
            .GetProperty("project")
            .GetProperty("restore")
            .GetProperty("sources")
            .EnumerateObject()
            .Select(static source => source.Name)
            .ToArray();

        Assert.Equal([higherSourcePath], sources);
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
    public void RestoreCommand_ReusesConfiguredSourceIdentityAndCredentials()
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
        Assert.Equal("private", source.Name);
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
    public void NuGetPackageAssetResolver_ResolvesAssetsFromFallbackPackageFolder()
    {
        var globalPackagesPath = Path.Combine(_workspace.Path, "global-packages");
        var fallbackPackagesPath = Path.Combine(_workspace.Path, "fallback-packages");
        var packageAssetPath = Path.Combine(
            fallbackPackagesPath,
            "fake.package",
            "1.0.0",
            "lib",
            "net10.0",
            "Fake.Package.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(packageAssetPath)!);
        File.Copy(typeof(RestoreCommandTests).Assembly.Location, packageAssetPath);

        var outputPath = Path.Combine(_workspace.Path, "obj");
        Directory.CreateDirectory(outputPath);
        var assetsPath = Path.Combine(outputPath, "project.assets.json");
        File.WriteAllText(
            assetsPath,
            $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Fake.Package/1.0.0": {
                    "type": "package",
                    "runtime": {
                      "lib/net10.0/Fake.Package.dll": {}
                    }
                  }
                }
              },
              "libraries": {
                "Fake.Package/1.0.0": {
                  "type": "package",
                  "path": "fake.package/1.0.0",
                  "files": [
                    "lib/net10.0/Fake.Package.dll"
                  ]
                }
              },
              "projectFileDependencyGroups": {
                "net10.0": [
                  "Fake.Package >= 1.0.0"
                ]
              },
              "packageFolders": {
                "{{JsonEncodedPath(globalPackagesPath)}}": {},
                "{{JsonEncodedPath(fallbackPackagesPath)}}": {}
              },
              "project": {
                "frameworks": {
                  "net10.0": {}
                }
              }
            }
            """);

        var resolution = NuGetPackageAssetResolver.Resolve(
            assetsPath,
            "net10.0",
            runtimeIdentifier: null);

        Assert.Equal(globalPackagesPath, resolution.PackagesPath);
        Assert.Equal(0, resolution.SkippedPackageCount);
        var asset = Assert.Single(resolution.Assets);
        Assert.Equal(packageAssetPath, asset.SourcePath);
    }

    [Fact]
    public void SettingsCommand_DiscoversWorkspaceNuGetConfigAndSourceNames()
    {
        var nugetConfigPath = Path.Combine(_workspace.Path, "NuGet.Config");
        var sourcePath = Path.Combine(_workspace.Path, "packages");
        File.WriteAllText(
            nugetConfigPath,
            $"""
            <configuration>
              <packageSources>
                <add key="private" value="{sourcePath}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var settings = SettingsCommand.GetSettings(_workspace.Path, s_sourceIdentityKey);

        Assert.Contains(nugetConfigPath, settings.ConfigPaths, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            settings.Sources,
            source => source.Name == "private" &&
                source.Identity == NuGetSourceIdentity.Compute(sourcePath, s_sourceIdentityKey) &&
                source.IsEnabled);
        Assert.Empty(settings.SensitiveSourceValues);
        Assert.True(settings.PackageSourceMappingEnabled);
        Assert.NotEmpty(settings.CacheIdentity);
    }

    [Fact]
    public void SettingsCommand_CacheIdentityUsesNuGetEnvironmentExpansion()
    {
        const string environmentVariableName = "ASPIRE_TEST_SETTINGS_SOURCE";
        File.WriteAllText(
            Path.Combine(_workspace.Path, "NuGet.Config"),
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="environment" value="%{environmentVariableName}%" />
              </packageSources>
            </configuration>
            """);

        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment[environmentVariableName] = "https://example.invalid/feed-a";

        RemoteExecutor.Invoke(static (workingDirectory, variableName) =>
        {
            var first = SettingsCommand.GetSettings(workingDirectory, s_sourceIdentityKey);
            var unchanged = SettingsCommand.GetSettings(workingDirectory, s_sourceIdentityKey);

            Environment.SetEnvironmentVariable(variableName, "https://example.invalid/feed-b");
            var changed = SettingsCommand.GetSettings(workingDirectory, s_sourceIdentityKey);

            Assert.Equal(first.CacheIdentity, unchanged.CacheIdentity);
            Assert.NotEqual(first.CacheIdentity, changed.CacheIdentity);
        }, _workspace.Path, environmentVariableName, options).Dispose();
    }

    [Fact]
    public void SettingsCommand_CacheIdentityIncludesSignatureValidationMode()
    {
        var configPath = Path.Combine(_workspace.Path, "NuGet.Config");
        File.WriteAllText(
            configPath,
            """
            <configuration>
              <config>
                <add key="signatureValidationMode" value="accept" />
              </config>
            </configuration>
            """);

        var first = SettingsCommand.GetSettings(_workspace.Path, s_sourceIdentityKey);

        File.WriteAllText(
            configPath,
            """
            <configuration>
              <config>
                <add key="signatureValidationMode" value="require" />
              </config>
            </configuration>
            """);

        var changed = SettingsCommand.GetSettings(_workspace.Path, s_sourceIdentityKey);

        Assert.NotEqual(first.CacheIdentity, changed.CacheIdentity);
    }

    [Fact]
    public void SettingsCommand_ReturnsCredentialBearingSourceLocationsForExactRedaction()
    {
        const string credential = "fake-sas-token";
        const string source = $"https://example.invalid/v3/index.json?sig={credential}";
        File.WriteAllText(
            Path.Combine(_workspace.Path, "NuGet.Config"),
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="{source}" />
              </packageSources>
            </configuration>
            """);

        var settings = SettingsCommand.GetSettings(_workspace.Path, s_sourceIdentityKey);
        var serializedSettings = JsonSerializer.Serialize(
            settings,
            SettingsJsonContext.Default.NuGetSettingsResponse);

        var packageSource = Assert.Single(settings.Sources);
        Assert.Equal("private", packageSource.Name);
        Assert.Equal(NuGetSourceIdentity.Compute(source, s_sourceIdentityKey), packageSource.Identity);
        Assert.Equal([source], settings.SensitiveSourceValues);
        Assert.Contains(credential, serializedSettings);
        Assert.Contains(source, serializedSettings);
    }

    [Fact]
    public void SettingsCommand_ReturnsCredentialBearingAuditSourceLocationsForExactRedaction()
    {
        const string credential = "fake-audit-token";
        const string packageSource = "https://packages.example.invalid/v3/index.json";
        const string auditSource = $"https://audit.example.invalid/v3/index.json?sig={credential}";
        File.WriteAllText(
            Path.Combine(_workspace.Path, "NuGet.Config"),
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="packages" value="{packageSource}" />
              </packageSources>
              <auditSources>
                <clear />
                <add key="audit" value="{auditSource}" />
              </auditSources>
            </configuration>
            """);

        var settings = SettingsCommand.GetSettings(_workspace.Path, s_sourceIdentityKey);
        var serializedSettings = JsonSerializer.Serialize(
            settings,
            SettingsJsonContext.Default.NuGetSettingsResponse);

        var source = Assert.Single(settings.Sources);
        Assert.Equal("packages", source.Name);
        Assert.Equal(NuGetSourceIdentity.Compute(packageSource, s_sourceIdentityKey), source.Identity);
        Assert.Equal([auditSource], settings.SensitiveSourceValues);
        Assert.Contains(credential, serializedSettings);
        Assert.Contains(auditSource, serializedSettings);
    }

    [Fact]
    public void SettingsCommand_ReportsSourceCredentialCapabilityWithoutReturningCredentials()
    {
        var credentialMarker = $"credential-marker-{Guid.NewGuid():N}";
        File.WriteAllText(
            Path.Combine(_workspace.Path, "NuGet.Config"),
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="https://packages.example.invalid/v3/index.json" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="test-user" />
                  <add key="ClearTextPassword" value="{credentialMarker}" />
                </private>
              </packageSourceCredentials>
            </configuration>
            """);

        var settings = SettingsCommand.GetSettings(_workspace.Path, s_sourceIdentityKey);
        var serializedSettings = JsonSerializer.Serialize(
            settings,
            SettingsJsonContext.Default.NuGetSettingsResponse);

        var source = Assert.Single(settings.Sources);
        Assert.True(source.HasCredentials);
        Assert.False(source.HasClientCertificates);
        Assert.DoesNotContain(credentialMarker, serializedSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsCommand_ReturnsMalformedCredentialBearingSourceForExactRedaction()
    {
        const string source = "https://user:p#word@packages.example.com/private";
        File.WriteAllText(
            Path.Combine(_workspace.Path, "NuGet.Config"),
            $"""
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="{source}" />
              </packageSources>
            </configuration>
            """);

        var settings = SettingsCommand.GetSettings(_workspace.Path, s_sourceIdentityKey);

        Assert.Single(settings.Sources);
        Assert.Equal([source], settings.SensitiveSourceValues);
    }

    [Fact]
    public void SettingsCommand_ReportsWhenHigherPrecedenceConfigClearsPackageSourceMapping()
    {
        var projectDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "AppHost"));
        File.WriteAllText(
            Path.Combine(_workspace.Path, "NuGet.Config"),
            """
            <configuration>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        File.WriteAllText(
            Path.Combine(projectDirectory.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSourceMapping>
                <clear />
              </packageSourceMapping>
            </configuration>
            """);

        var settings = SettingsCommand.GetSettings(projectDirectory.FullName, s_sourceIdentityKey);

        Assert.False(settings.PackageSourceMappingEnabled);
    }

    [Fact]
    public void WriteConfigCommand_OverlaysEvaluatedMappingsWithoutFlatteningAmbientConfiguration()
    {
        var appHostDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "AppHost"));
        var privateSource = Path.Combine(appHostDirectory.FullName, "private-packages");
        var rootSource = Path.Combine(_workspace.Path, "root-packages");
        var unrelatedSource = Path.Combine(appHostDirectory.FullName, "unrelated-packages");
        var globalPackagesFolder = Path.Combine(_workspace.Path, "global-packages");
        File.WriteAllText(
            Path.Combine(_workspace.Path, "NuGet.Config"),
            """
            <configuration>
              <packageSources>
                <clear />
                <add key="private" value="root-private-packages" />
                <add key="root-only" value="root-packages" />
              </packageSources>
              <packageSourceCredentials>
                <private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </private>
                <credential-only>
                  <add key="Username" value="unused" />
                  <add key="ClearTextPassword" value="credential-only-secret" />
                </credential-only>
              </packageSourceCredentials>
              <clientCertificates>
                <fileCert packageSource="certificate-only" path="client.pfx" />
              </clientCertificates>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="Aspire*" />
                </packageSource>
                <packageSource key="root-only">
                  <package pattern="Root.*" />
                </packageSource>
              </packageSourceMapping>
              <config>
                <add key="globalPackagesFolder" value="global-packages" />
              </config>
            </configuration>
            """);
        File.WriteAllText(
            Path.Combine(appHostDirectory.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSources>
                <add key="private" value="private-packages" />
                <add key="unrelated" value="unrelated-packages" />
              </packageSources>
              <disabledPackageSources>
                <add key="private" value="true" />
                <add key="unrelated" value="true" />
                <add key="dormant" value="true" />
              </disabledPackageSources>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="Aspire.Hosting.*" />
                  <package pattern="Contoso.*" />
                </packageSource>
                <packageSource key="unrelated">
                  <package pattern="Other.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var ambient = SettingsCommand.GetSettings(appHostDirectory.FullName, s_sourceIdentityKey);
        Assert.Contains("credential-only", ambient.ReservedPackageSourceKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("certificate-only", ambient.ReservedPackageSourceKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("dormant", ambient.ReservedPackageSourceKeys, StringComparer.OrdinalIgnoreCase);
        var transformedMappings = ambient.PackageSourceMappings
            .Select(mapping => new NuGetPackageSourceMapping(
                mapping.SourceKey,
                mapping.Patterns
                    .Where(static pattern => !pattern.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase))
                    .ToArray()))
            .Where(static mapping => mapping.Patterns.Length > 0)
            .Append(new NuGetPackageSourceMapping("private", ["Aspire*"]))
            .GroupBy(static mapping => mapping.SourceKey, StringComparer.OrdinalIgnoreCase)
            .Select(static group => new NuGetPackageSourceMapping(
                group.Key,
                group.SelectMany(static mapping => mapping.Patterns).ToArray()))
            .ToArray();
        var policyDirectory = Directory.CreateDirectory(
            Path.Combine(appHostDirectory.FullName, ".aspire", "integration-restore"));
        var overlayPath = Path.Combine(policyDirectory.FullName, "NuGet.Config");

        WriteConfigCommand.Write(
            new NuGetConfigOverlayRequest(
                Sources: [],
                PackageSourceMappings: transformedMappings,
                ClearDisabledPackageSources: true,
                DisabledPackageSourceKeys: ["unrelated", "dormant"],
                GlobalPackagesFolder: null),
            overlayPath);

        var overlayContent = File.ReadAllText(overlayPath);
        Assert.DoesNotContain("secret", overlayContent, StringComparison.Ordinal);
        Assert.DoesNotContain("NuGet.org", overlayContent, StringComparison.OrdinalIgnoreCase);

        var options = new RemoteInvokeOptions();
        options.StartInfo.Environment.Remove("NUGET_PACKAGES");

        RemoteExecutor.Invoke(static (policyDirectoryPath) =>
        {
            var appHostDirectory = Directory.GetParent(Directory.GetParent(policyDirectoryPath)!.FullName)!;
            var workspaceDirectory = appHostDirectory.Parent!;
            var effective = Settings.LoadDefaultSettings(
                policyDirectoryPath,
                configFileName: null,
                new XPlatMachineWideSetting());
            var sources = new PackageSourceProvider(effective)
                .LoadPackageSources()
                .ToDictionary(static source => source.Name, StringComparer.OrdinalIgnoreCase);

            Assert.Equal(
                Path.Combine(appHostDirectory.FullName, "private-packages"),
                sources["private"].Source);
            Assert.True(sources["private"].IsEnabled);
            Assert.Equal("user", sources["private"].Credentials?.Username);
            Assert.Equal(
                Path.Combine(workspaceDirectory.FullName, "root-packages"),
                sources["root-only"].Source);
            Assert.Equal(
                Path.Combine(appHostDirectory.FullName, "unrelated-packages"),
                sources["unrelated"].Source);
            Assert.False(sources["unrelated"].IsEnabled);
            Assert.Equal(
                Path.Combine(workspaceDirectory.FullName, "global-packages"),
                SettingsUtility.GetGlobalPackagesFolder(effective));

            var mappings = new PackageSourceMappingProvider(effective)
                .GetPackageSourceMappingItems()
                .ToDictionary(
                    static mapping => mapping.Key,
                    static mapping => mapping.Patterns.Select(static pattern => pattern.Pattern).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            Assert.Equal(["Contoso.*", "Aspire*"], mappings["private"]);
            Assert.Equal(["Root.*"], mappings["root-only"]);
            Assert.Equal(["Other.*"], mappings["unrelated"]);
        }, policyDirectory.FullName, options).Dispose();
    }

    /// <summary>
    /// Converts a file path to its JSON-escaped representation (e.g. backslashes doubled).
    /// </summary>
    private static string JsonEncodedPath(string path) =>
        path.Replace(@"\", @"\\");
}
