// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Packaging;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Configuration;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Packaging;

// Initial focused snapshot tests for NuGetConfigMerger. These allow us to feed a literal NuGet.config
// then exercise CreateOrUpdateAsync and verify the resulting XML in a stable, readable snapshot.
public class NuGetConfigMergerSnapshotTests
{
    private readonly ITestOutputHelper _output;

    public NuGetConfigMergerSnapshotTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private sealed class FakeNuGetPackageCache : INuGetPackageCache
    {
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetTemplatePackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetIntegrationPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetCliPackagesAsync(DirectoryInfo workingDirectory, bool prerelease, FileInfo? nugetConfigFile, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
        public Task<IEnumerable<Aspire.Shared.NuGetPackageCli>> GetPackagesAsync(DirectoryInfo workingDirectory, string packageId, Func<string, bool>? filter, bool prerelease, FileInfo? nugetConfigFile, bool useCache, CancellationToken cancellationToken) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
    }

    private sealed class FakeFeatures : IFeatures
    {
        public bool IsFeatureEnabled(string featureFlag, bool defaultValue) => defaultValue;
    }

    private static PackagingService CreatePackagingService(CliExecutionContext executionContext)
    {
        var features = new FakeFeatures();
        var configuration = new ConfigurationBuilder().Build();
        return new PackagingService(executionContext, new FakeNuGetPackageCache(), features, configuration);
    }

    private static async Task<FileInfo> WriteConfigAsync(DirectoryInfo dir, string content)
    {
        var path = Path.Combine(dir.FullName, "nuget.config");
        await File.WriteAllTextAsync(path, content);
        return new FileInfo(path);
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("daily")]
    [InlineData("pr-1234")]
    public async Task Merge_WithSimpleNuGetConfig_ProducesExpectedXml(string channelName)
    {
        using var workspace = TemporaryWorkspace.Create(_output);
        var root = workspace.WorkspaceRoot;

        // Empty hives directory ensures deterministic channel set (no PR channels)
        var hivesDir = root.CreateSubdirectory("hives");
        // Add a deterministic PR hive for testing realistic PR channel mappings.
        hivesDir.CreateSubdirectory("pr-1234");
        var cacheDir = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(root, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        var packagingService = CreatePackagingService(executionContext);

        // Existing config purposely minimal (no packageSourceMapping yet)
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                </packageSources>
            </configuration>
            """);

        var channels = await packagingService.GetChannelsAsync();
        // Filter to explicit channels here so we never select the implicit ("default") channel
        // which has no mappings and would produce a no-op merge (nothing meaningful to snapshot).
        var channel = channels.First(c => c.Type is PackageChannelType.Explicit && string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var updated = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var xmlString = updated.ToString();

        // Normalize machine-specific absolute hive paths in PR channel snapshots for stability
        if (channelName.StartsWith("pr-", StringComparison.OrdinalIgnoreCase))
        {
            var hivePath = Path.Combine(hivesDir.FullName, channelName);
            xmlString = xmlString.Replace(hivePath, "{PR_HIVE}");
        }

        await Verify(xmlString, extension: "xml")
            .UseFileName($"Merge_WithSimpleNuGetConfig_ProducesExpectedXml.{channelName}");
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("daily")]
    [InlineData("pr-1234")]
    public async Task Merge_WithBrokenSdkState_ProducesExpectedXml(string channelName)
    {
        using var workspace = TemporaryWorkspace.Create(_output);
        var root = workspace.WorkspaceRoot;

        // Empty hives directory ensures deterministic channel set (no PR channels)
        var hivesDir = root.CreateSubdirectory("hives");
        // Add a deterministic PR hive for testing realistic PR channel mappings.
        hivesDir.CreateSubdirectory("pr-1234");
        var cacheDir2 = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(root, hivesDir, cacheDir2, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        var packagingService = CreatePackagingService(executionContext);

        // Existing config purposely minimal (no packageSourceMapping yet)
        await WriteConfigAsync(root,
            """
            <configuration>
                <packageSources>
                    <clear />
                    <add key="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" />
                    <add key="https://api.nuget.org/v3/index.json" value="https://api.nuget.org/v3/index.json" />
                    <add key="C:\Users\davifowl\.aspire\hives\pr-11227" value="C:\Users\davifowl\.aspire\hives\pr-11227" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="https://api.nuget.org/v3/index.json">
                        <package pattern="*" />
                    </packageSource>
                    <packageSource key="C:\Users\davifowl\.aspire\hives\pr-11227">
                        <package pattern="Aspire*" />
                    </packageSource>
                    <packageSource key="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json">
                        <package pattern="*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var channels = await packagingService.GetChannelsAsync();
        // Filter to explicit channels here so we never select the implicit ("default") channel
        // which has no mappings and would produce a no-op merge (nothing meaningful to snapshot).
        var channel = channels.First(c => c.Type is PackageChannelType.Explicit && string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var updated = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var xmlString = updated.ToString();

        // Normalize machine-specific absolute hive paths in PR channel snapshots for stability
        if (channelName.StartsWith("pr-", StringComparison.OrdinalIgnoreCase))
        {
            var hivePath = Path.Combine(hivesDir.FullName, channelName);
            xmlString = xmlString.Replace(hivePath, "{PR_HIVE}");
        }

        await Verify(xmlString, extension: "xml")
            .UseFileName($"Merge_WithBrokenSdkState_ProducesExpectedXml.{channelName}");
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("daily")]
    [InlineData("pr-1234")]
    public async Task Merge_WithDailyFeedWithExtraMappingsIsPreserved_ProducesExpectedXml(string channelName)
    {
        using var workspace = TemporaryWorkspace.Create(_output);
        var root = workspace.WorkspaceRoot;

        // Empty hives directory ensures deterministic channel set (no PR channels)
        var hivesDir = root.CreateSubdirectory("hives");
        // Add a deterministic PR hive for testing realistic PR channel mappings.
        hivesDir.CreateSubdirectory("pr-1234");
        var cacheDir3 = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(root, hivesDir, cacheDir3, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        var packagingService = CreatePackagingService(executionContext);

        // Existing config purposely minimal (no packageSourceMapping yet)
        await WriteConfigAsync(root,
            """
            <?xml version="1.0"?>
            <configuration>
                <packageSources>
                    <clear />
                    <add key="NuGet" value="https://api.nuget.org/v3/index.json" />
                    <add key="hexesoft" value="https://example.com/hexesoft/nuget/v3/index.json" />
                    <add key="dotnet9" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="dotnet9">
                        <package pattern="Aspire*" />
                        <package pattern="Microsoft.Extensions.SpecialPackage*" />
                    </packageSource>
                    <packageSource key="https://api.nuget.org/v3/index.json">
                        <package pattern="*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var channels = await packagingService.GetChannelsAsync();
        // Filter to explicit channels here so we never select the implicit ("default") channel
        // which has no mappings and would produce a no-op merge (nothing meaningful to snapshot).
        var channel = channels.First(c => c.Type is PackageChannelType.Explicit && string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var updated = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var xmlString = updated.ToString();

        // Normalize machine-specific absolute hive paths in PR channel snapshots for stability
        if (channelName.StartsWith("pr-", StringComparison.OrdinalIgnoreCase))
        {
            var hivePath = Path.Combine(hivesDir.FullName, channelName);
            xmlString = xmlString.Replace(hivePath, "{PR_HIVE}");
        }

        await Verify(xmlString, extension: "xml")
            .UseFileName($"Merge_WithDailyFeedWithExtraMappingsIsPreserved_ProducesExpectedXml.{channelName}");
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("daily")]
    [InlineData("pr-1234")]
    public async Task Merge_WithExtraInternalFeedIncorrectlyMapped_ProducesExpectedXml(string channelName)
    {
        using var workspace = TemporaryWorkspace.Create(_output);
        var root = workspace.WorkspaceRoot;

        // Empty hives directory ensures deterministic channel set (no PR channels)
        var hivesDir = root.CreateSubdirectory("hives");
        // Add a deterministic PR hive for testing realistic PR channel mappings.
        hivesDir.CreateSubdirectory("pr-1234");
        var cacheDir4 = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(root, hivesDir, cacheDir4, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        var packagingService = CreatePackagingService(executionContext);

        // Existing config purposely minimal (no packageSourceMapping yet)
        await WriteConfigAsync(root,
            """
            <configuration>
                <packageSources>
                    <clear />
                    <add key="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" />
                    <add key="https://api.nuget.org/v3/index.json" value="https://api.nuget.org/v3/index.json" />
                    <add key="companyfeed" value="https://companyfeed/v3/index.json" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="https://api.nuget.org/v3/index.json">
                        <package pattern="*" />
                    </packageSource>
                    <packageSource key="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json">
                        <package pattern="Aspire*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var channels = await packagingService.GetChannelsAsync();
        // Filter to explicit channels here so we never select the implicit ("default") channel
        // which has no mappings and would produce a no-op merge (nothing meaningful to snapshot).
        var channel = channels.First(c => c.Type is PackageChannelType.Explicit && string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var updated = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var xmlString = updated.ToString();

        // Normalize machine-specific absolute hive paths in PR channel snapshots for stability
        if (channelName.StartsWith("pr-", StringComparison.OrdinalIgnoreCase))
        {
            var hivePath = Path.Combine(hivesDir.FullName, channelName);
            xmlString = xmlString.Replace(hivePath, "{PR_HIVE}");
        }

        await Verify(xmlString, extension: "xml")
            .UseFileName($"Merge_WithExtraInternalFeedIncorrectlyMapped_ProducesExpectedXml.{channelName}");
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("daily")]
    [InlineData("pr-1234")]
    public async Task Merge_ExtraPatternOnDailyFeedWhenOnPrFeedGetsConsolidatedWithOtherPatterns_ProducesExpectedXml(string channelName)
    {
        using var workspace = TemporaryWorkspace.Create(_output);
        var root = workspace.WorkspaceRoot;

        // Empty hives directory ensures deterministic channel set (no PR channels)
        var hivesDir = root.CreateSubdirectory("hives");
        // Add a deterministic PR hive for testing realistic PR channel mappings.
        hivesDir.CreateSubdirectory("pr-1234");
        var cacheDir5 = new DirectoryInfo(Path.Combine(root.FullName, ".aspire", "cache"));
        var executionContext = new CliExecutionContext(root, hivesDir, cacheDir5, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")));
        var packagingService = CreatePackagingService(executionContext);

        // Existing config purposely minimal (no packageSourceMapping yet)
        await WriteConfigAsync(root,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
                <packageSources>
                    <clear />
                    <add key="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" value="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json" />
                    <add key="https://api.nuget.org/v3/index.json" value="https://api.nuget.org/v3/index.json" />
                    <add key="mycompany" value="http://mycompany.com/feed" />
                    <add key="C:\Users\midenn\.aspire\hives\pr-11275" value="C:\Users\midenn\.aspire\hives\pr-11275" />
                </packageSources>
                <packageSourceMapping>
                    <packageSource key="https://api.nuget.org/v3/index.json">
                        <package pattern="*" />
                    </packageSource>
                    <packageSource key="https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet9/nuget/v3/index.json">
                        <package pattern="Microsoft.Extensions.HelperStuff*" />
                    </packageSource>
                    <packageSource key="C:\Users\midenn\.aspire\hives\pr-11275">
                        <package pattern="Aspire*" />
                    </packageSource>
                </packageSourceMapping>
            </configuration>
            """);

        var channels = await packagingService.GetChannelsAsync();
        // Filter to explicit channels here so we never select the implicit ("default") channel
        // which has no mappings and would produce a no-op merge (nothing meaningful to snapshot).
        var channel = channels.First(c => c.Type is PackageChannelType.Explicit && string.Equals(c.Name, channelName, StringComparison.OrdinalIgnoreCase));

        await NuGetConfigMerger.CreateOrUpdateAsync(root, channel);

        var updated = XDocument.Load(Path.Combine(root.FullName, "nuget.config"));
        var xmlString = updated.ToString();

        // Normalize machine-specific absolute hive paths in PR channel snapshots for stability
        if (channelName.StartsWith("pr-", StringComparison.OrdinalIgnoreCase))
        {
            var hivePath = Path.Combine(hivesDir.FullName, channelName);
            xmlString = xmlString.Replace(hivePath, "{PR_HIVE}");
        }

        await Verify(xmlString, extension: "xml")
            .UseFileName($"Merge_ExtraPatternOnDailyFeedWhenOnPrFeedGetsConsolidatedWithOtherPatterns_ProducesExpectedXml.{channelName}");
    }
}
