// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using System.Xml.Linq;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Channel-resolution behavior for <see cref="TemplateNuGetConfigService"/>.
/// None of the channel-resolving entry points
/// (<see cref="TemplateNuGetConfigService.PromptToCreateOrUpdateNuGetConfigAsync(string?, string, CancellationToken)"/>,
/// <see cref="TemplateNuGetConfigService.CreateOrUpdateNuGetConfigWithoutPromptAsync(string?, string, CancellationToken)"/>,
/// <see cref="TemplateNuGetConfigService.ResolveTemplatePackageAsync(TemplatePackageQuery, CancellationToken)"/>)
/// may resolve a channel by reading from a global identity-channel source; channel input
/// must come from the caller-supplied argument or fall back to the implicit channel only.
/// </summary>
public class TemplateNuGetConfigServiceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_CreatesSelfContainedConfigWithoutAmbientSources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = workspace.WorkspaceRoot.CreateSubdirectory("output");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="ambient-private" value="https://private.example/v3/index.json" />
              </packageSources>
              <disabledPackageSources>
                <add key="ambient-private" value="true" />
              </disabledPackageSources>
              <packageSourceCredentials>
                <ambient-private>
                  <add key="Username" value="user" />
                  <add key="ClearTextPassword" value="secret" />
                </ambient-private>
              </packageSourceCredentials>
              <packageSourceMapping>
                <packageSource key="ambient-private">
                  <package pattern="Contoso.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var service = CreateService();
        const string sourceOverride = "/tmp/aspire-pr-hive/packages";

        Assert.True(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride, channelName: null, outputDirectory.FullName, CancellationToken.None));

        var doc = XDocument.Load(Path.Combine(outputDirectory.FullName, "nuget.config"));
        Assert.Contains(doc.Root!.Element("packageSources")!.Elements("clear"), _ => true);
        Assert.Contains(doc.Root!.Element("packageSources")!.Elements("add"), e => (string?)e.Attribute("value") == sourceOverride);
        Assert.Contains(doc.Root!.Element("packageSources")!.Elements("add"), e => (string?)e.Attribute("value") == PackageSourceOverrideMappings.NuGetOrgSource);
        Assert.DoesNotContain(doc.Descendants("add"), e => (string?)e.Attribute("value") == "https://private.example/v3/index.json");
        Assert.Null(doc.Root!.Element("disabledPackageSources"));
        Assert.Null(doc.Root!.Element("packageSourceCredentials"));
        Assert.Empty(GetPackagePatternsForSource(doc, "ambient-private"));
    }

    [Fact]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_PreservesRequestedChannelFallbackMappings()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = workspace.WorkspaceRoot.CreateSubdirectory("output");
        const string sourceOverride = "/tmp/aspire-pr-hive/packages";
        const string channelAspireSource = "https://example.invalid/aspire";
        const string communitySource = "https://example.invalid/community";
        const string fallbackSource = "https://example.invalid/fallback";

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var channel = PackageChannel.CreateExplicitChannel(
                    "daily",
                    PackageChannelQuality.Both,
                    [
                        new PackageMapping("Aspire*", channelAspireSource),
                        new PackageMapping("CommunityToolkit*", communitySource),
                        new PackageMapping(PackageMapping.AllPackages, fallbackSource),
                    ],
                    new FakeNuGetPackageCache());
                return Task.FromResult<IEnumerable<PackageChannel>>([channel]);
            }
        };
        var service = CreateService(packagingService: packagingService);

        Assert.True(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride, channelName: "daily", outputDirectory.FullName, CancellationToken.None));

        var doc = XDocument.Load(Path.Combine(outputDirectory.FullName, "nuget.config"));
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, sourceOverride));
        Assert.Equal(["CommunityToolkit*"], GetPackagePatternsForSource(doc, communitySource));
        Assert.Equal([PackageMapping.AllPackages], GetPackagePatternsForSource(doc, fallbackSource));
        Assert.Empty(GetPackagePatternsForSource(doc, channelAspireSource));
        Assert.Empty(GetPackagePatternsForSource(doc, PackageSourceOverrideMappings.NuGetOrgSource));
    }

    [Fact]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_UpdatesOnlyProjectLocalConfig()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var outputDirectory = workspace.WorkspaceRoot.CreateSubdirectory("output");
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="parent-private" value="https://parent.example/v3/index.json" />
              </packageSources>
            </configuration>
            """);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory.FullName, "nuget.config"),
            """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="project-local" value="https://project.example/v3/index.json" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="project-local">
                  <package pattern="Project.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var service = CreateService();
        const string sourceOverride = "/tmp/aspire-pr-hive/packages";

        Assert.True(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride, channelName: null, outputDirectory.FullName, CancellationToken.None));

        var doc = XDocument.Load(Path.Combine(outputDirectory.FullName, "nuget.config"));
        Assert.Contains(doc.Root!.Element("packageSources")!.Elements("add"), e => (string?)e.Attribute("value") == "https://project.example/v3/index.json");
        Assert.DoesNotContain(doc.Root!.Element("packageSources")!.Elements("add"), e => (string?)e.Attribute("value") == "https://parent.example/v3/index.json");
        Assert.Equal(["Aspire*"], GetPackagePatternsForSource(doc, sourceOverride));
        Assert.Equal(["Project.*", PackageMapping.AllPackages], GetPackagePatternsForSource(doc, "project-local"));
    }

    [Fact]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_NullSourceShortCircuits()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Channel lookup should not run without a source override.")
        };
        var service = CreateService(packagingService: packagingService);

        Assert.False(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride: null, channelName: "daily", workspace.WorkspaceRoot.FullName, CancellationToken.None));
        Assert.False(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride: "", channelName: "daily", workspace.WorkspaceRoot.FullName, CancellationToken.None));
        Assert.False(await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride: "   ", channelName: "daily", workspace.WorkspaceRoot.FullName, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://user:token@example.invalid/v3/index.json")]
    [InlineData("https://example.invalid/v3/index.json?sig=token")]
    [InlineData("https://example.invalid/v3/index.json#token")]
    public async Task CreateOrUpdateNuGetConfigForSourceOverrideAsync_CredentialBearingHttpSourceThrows(string sourceOverride)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await service.CreateOrUpdateNuGetConfigForSourceOverrideAsync(sourceOverride, channelName: null, workspace.WorkspaceRoot.FullName, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "nuget.config")));
    }

    [Fact]
    public async Task PromptToCreateOrUpdateNuGetConfigAsync_NullChannelName_ShortCircuits()
    {
        // Null/whitespace channelName must short-circuit without consulting any
        // ambient channel source. No exception, no implicit-channel work requested.
        var service = CreateService();

        await service.PromptToCreateOrUpdateNuGetConfigAsync(channelName: null, outputPath: Directory.CreateTempSubdirectory().FullName, CancellationToken.None);
        await service.PromptToCreateOrUpdateNuGetConfigAsync(channelName: "", outputPath: Directory.CreateTempSubdirectory().FullName, CancellationToken.None);
        await service.PromptToCreateOrUpdateNuGetConfigAsync(channelName: "   ", outputPath: Directory.CreateTempSubdirectory().FullName, CancellationToken.None);
    }

    [Fact]
    public async Task CreateOrUpdateNuGetConfigWithoutPromptAsync_NullChannelName_ShortCircuits()
    {
        var service = CreateService();

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            // Null/whitespace inputs must short-circuit and return false without
            // resolving a channel from any ambient source.
            Assert.False(await service.CreateOrUpdateNuGetConfigWithoutPromptAsync(channelName: null, outputPath: dir.FullName, CancellationToken.None));
            Assert.False(await service.CreateOrUpdateNuGetConfigWithoutPromptAsync(channelName: "", outputPath: dir.FullName, CancellationToken.None));
            Assert.False(await service.CreateOrUpdateNuGetConfigWithoutPromptAsync(channelName: "   ", outputPath: dir.FullName, CancellationToken.None));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ResolveTemplatePackageAsync_NullRequestedChannel_UsesImplicitChannelOnly()
    {
        // No explicit RequestedChannel: the resolver picks the implicit channel only.
        // We exercise the production codepath with a tracking packaging service so the
        // assertion is that the resolver completes (no exception is thrown by
        // an unexpected channel-lookup path) and only the implicit channel is in play.
        var requestedChannels = new List<PackageChannelType>();
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var implicitCh = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache
                {
                    GetIntegrationPackagesAsyncCallback = (_, _, _, _) => Task.FromResult(Enumerable.Empty<Aspire.Shared.NuGetPackageCli>())
                });
                return Task.FromResult<IEnumerable<PackageChannel>>([implicitCh]);
            }
        };

        var service = CreateService(packagingService: packagingService);

        var query = new TemplatePackageQuery(
            RequestedChannel: null,
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: false);

        // No packages were staged on the implicit channel, so the resolver throws
        // EmptyChoicesException — that's the expected terminal state for "implicit was
        // tried, nothing matched". The assertion is that this exception is the one that
        // surfaces (not ChannelNotFoundException from a different lookup path).
        await Assert.ThrowsAsync<Aspire.Cli.Interaction.EmptyChoicesException>(
            async () => await service.ResolveTemplatePackageAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveTemplatePackage_RequestedChannel_NotFound_Throws()
    {
        // The resolver no longer special-cases "local". A request for any unrecognized
        // channel name — including "local" with no matching named channel — must
        // throw ChannelNotFoundException. The local-identity fallback that previously
        // lived here has moved to InitCommand so that an explicit `aspire new --channel local`
        // without the hive still errors cleanly instead of silently switching feeds.
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var implicitCh = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache());
                return Task.FromResult<IEnumerable<PackageChannel>>([implicitCh]);
            }
        };

        var service = CreateService(packagingService: packagingService);

        var query = new TemplatePackageQuery(
            RequestedChannel: PackageChannelNames.Local,
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: false);

        await Assert.ThrowsAsync<Aspire.Cli.Exceptions.ChannelNotFoundException>(
            async () => await service.ResolveTemplatePackageAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveTemplatePackage_RequestedChannel_Matches_ReturnsThatChannel()
    {
        // Positive parity: a request for a registered named channel must resolve to that
        // exact channel. Verifies the rename + collapsed selection block didn't regress
        // the matching path.
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var implicitCh = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache());
                var stableCh = PackageChannel.CreateExplicitChannel(
                    "stable",
                    PackageChannelQuality.Both,
                    [new PackageMapping("Aspire*", "stable-src")],
                    new FakeNuGetPackageCache
                    {
                        GetTemplatePackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                        [
                            new Aspire.Shared.NuGetPackageCli { Id = TemplateNuGetConfigService.TemplatesPackageName, Version = "13.3.0", Source = "stable-src" }
                        ])
                    });
                return Task.FromResult<IEnumerable<PackageChannel>>([implicitCh, stableCh]);
            }
        };

        var service = CreateService(packagingService: packagingService);

        var query = new TemplatePackageQuery(
            RequestedChannel: "stable",
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: false);

        var selection = await service.ResolveTemplatePackageAsync(query, CancellationToken.None);

        Assert.Equal("stable", selection.Channel.Name);
        Assert.Equal(PackageChannelType.Explicit, selection.Channel.Type);
        Assert.Equal("13.3.0", selection.Package.Version);
    }

    [Fact]
    public async Task ResolveTemplatePackageAsync_NonExistentRequestedChannel_NotLocal_StillThrowsChannelNotFound()
    {
        // Companion to RequestedChannel_NotFound_Throws: any unrecognized name (typo
        // such as "stalbe") must still surface a ChannelNotFoundException so users see
        // the failure instead of silently falling through to the implicit channel.
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var implicitCh = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache());
                return Task.FromResult<IEnumerable<PackageChannel>>([implicitCh]);
            }
        };

        var service = CreateService(packagingService: packagingService);

        var query = new TemplatePackageQuery(
            RequestedChannel: "stalbe",
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: false);

        await Assert.ThrowsAsync<Aspire.Cli.Exceptions.ChannelNotFoundException>(
            async () => await service.ResolveTemplatePackageAsync(query, CancellationToken.None));
    }

    [Theory]
    // The `aspire new` code path opts into hives via IncludePrHives: true. When a hive
    // is actually on disk (GetHiveCount() > 0), the resolver must consider every
    // registered channel — and the explicit pr-12345 channel (pinnedVersion 2.0.0) wins
    // over the implicit channel (version 1.0.0).
    [InlineData(true, true, "2.0.0", false, "pr-12345")]
    // Opt-in alone is not enough: the user must also have hive directories on disk
    // (GetHiveCount() > 0). Without them, even with IncludePrHives: true the resolver
    // must restrict to the implicit channel. This is what protects developers running
    // `aspire new` on a clean machine from accidentally pulling from an explicit channel
    // that was registered but never installed.
    [InlineData(true, false, "1.0.0", true, null)]
    // The `aspire init` code path passes IncludePrHives: false intentionally so a
    // developer with stale ~/.aspire/hives/* doesn't get a different template than on
    // a clean machine. Even with a hive present, the resolver must restrict to the
    // implicit channel.
    [InlineData(false, true, "1.0.0", true, null)]
    public async Task ResolveTemplatePackageAsync_IncludePrHives_RespectsHiveGate(
        bool includePrHives,
        bool createHiveOnDisk,
        string expectedVersion,
        bool expectImplicitChannel,
        string? expectedChannelName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var hivesDir = new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives"));
        if (createHiveOnDisk)
        {
            hivesDir.Create();
            hivesDir.CreateSubdirectory("pr-12345");
        }

        var executionContext = CreateExecutionContextWithHives(workspace.WorkspaceRoot, hivesDir);

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var implicitCh = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache
                {
                    GetTemplatePackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                    [
                        new Aspire.Shared.NuGetPackageCli { Id = TemplateNuGetConfigService.TemplatesPackageName, Version = "1.0.0", Source = "implicit-src" }
                    ])
                });
                var hiveCh = PackageChannel.CreateExplicitChannel(
                    "pr-12345",
                    PackageChannelQuality.Both,
                    [new PackageMapping("Aspire*", "pr-src")],
                    new FakeNuGetPackageCache(),
                    pinnedVersion: "2.0.0");
                return Task.FromResult<IEnumerable<PackageChannel>>([implicitCh, hiveCh]);
            }
        };

        var service = CreateService(packagingService: packagingService, executionContext: executionContext);

        var query = new TemplatePackageQuery(
            RequestedChannel: null,
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: includePrHives);

        var selection = await service.ResolveTemplatePackageAsync(query, CancellationToken.None);

        Assert.Equal(expectedVersion, selection.Package.Version);
        Assert.Equal(expectImplicitChannel ? PackageChannelType.Implicit : PackageChannelType.Explicit, selection.Channel.Type);
        if (expectedChannelName is not null)
        {
            Assert.Equal(expectedChannelName, selection.Channel.Name);
        }
    }

    private static CliExecutionContext CreateExecutionContextWithHives(DirectoryInfo workingDirectory, DirectoryInfo hivesDirectory)
    {
        return TestExecutionContextHelper.CreateExecutionContext(
            workingDirectory,
            hivesDirectory: hivesDirectory);
    }

    private static string[] GetPackagePatternsForSource(XDocument doc, string source)
    {
        var packageSourceMapping = doc.Root!.Element("packageSourceMapping");
        if (packageSourceMapping is null)
        {
            return [];
        }

        return packageSourceMapping
            .Elements("packageSource")
            .Where(e => string.Equals((string?)e.Attribute("key"), source, StringComparison.OrdinalIgnoreCase))
            .Elements("package")
            .Select(e => (string?)e.Attribute("pattern"))
            .Where(pattern => pattern is not null)
            .Select(pattern => pattern!)
            .ToArray();
    }

    private static TemplateNuGetConfigService CreateService(
        TestPackagingService? packagingService = null,
        CliExecutionContext? executionContext = null)
    {
        return new TemplateNuGetConfigService(
            new TestInteractionService(),
            executionContext ?? TestExecutionContextFactory.CreateTestContext(),
            packagingService ?? MockPackagingServiceFactory.Create(),
            new StubTemplateVersionPrompter(),
            new StubCliHostEnvironment());
    }

    private sealed class StubTemplateVersionPrompter : Aspire.Cli.Commands.ITemplateVersionPrompter
    {
        public Task<(Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)> PromptForTemplatesVersionAsync(
            IEnumerable<(Aspire.Shared.NuGetPackageCli Package, PackageChannel Channel)> candidatePackages,
            CancellationToken cancellationToken)
        {
            throw new InvalidOperationException(
                "TemplateNuGetConfigService unexpectedly entered the version-prompt path; this stub is wired in tests where the prompt should never be reached.");
        }
    }

    private sealed class StubCliHostEnvironment : ICliHostEnvironment
    {
        public bool SupportsInteractiveInput => false;
        public bool SupportsInteractiveOutput => false;
        public bool SupportsAnsi => false;
    }
}
