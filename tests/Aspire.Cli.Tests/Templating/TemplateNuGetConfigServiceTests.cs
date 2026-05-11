// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;

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
    public async Task ResolveTemplatePackageAsync_NullChannelOverride_UsesImplicitChannelOnly()
    {
        // No explicit ChannelOverride: the resolver picks the implicit channel only.
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
            ChannelOverride: null,
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
    public async Task ResolveTemplatePackageAsync_LocalChannelOverride_NoLocalHive_FallsBackToImplicitChannel()
    {
        // A locally-built CLI bakes channel="local" into assembly metadata. On a clean
        // machine without ~/.aspire/hives/local, PackagingService produces no "local"
        // channel, and InitCommand forwards CliExecutionContext.Channel ("local") as
        // ChannelOverride. Without the resolver-level fallback this throws
        // ChannelNotFoundException and `aspire init` is unusable on a clean machine.
        // The fallback policy: a request for "local" with no matching channel resolves
        // to the implicit channel (ambient NuGet config) — a CLI with no local hive is
        // semantically just a CLI using ambient NuGet.
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var implicitCh = PackageChannel.CreateImplicitChannel(new FakeNuGetPackageCache
                {
                    GetTemplatePackagesAsyncCallback = (_, _, _, _) => Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                    [
                        new Aspire.Shared.NuGetPackageCli { Id = TemplateNuGetConfigService.TemplatesPackageName, Version = "13.3.0", Source = "implicit" }
                    ])
                });
                return Task.FromResult<IEnumerable<PackageChannel>>([implicitCh]);
            }
        };

        var service = CreateService(packagingService: packagingService);

        var query = new TemplatePackageQuery(
            ChannelOverride: PackageChannelNames.Local,
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: false);

        var selection = await service.ResolveTemplatePackageAsync(query, CancellationToken.None);

        Assert.Equal(PackageChannelType.Implicit, selection.Channel.Type);
    }

    [Fact]
    public async Task ResolveTemplatePackageAsync_NonExistentChannelOverride_NotLocal_StillThrowsChannelNotFound()
    {
        // The fallback is intentionally narrow: only "local" → implicit. A request for
        // any other unrecognized channel name must still fail loudly so typos surface
        // (e.g., "stalbe" for "stable").
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
            ChannelOverride: "stalbe",
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: false);

        await Assert.ThrowsAsync<Aspire.Cli.Exceptions.ChannelNotFoundException>(
            async () => await service.ResolveTemplatePackageAsync(query, CancellationToken.None));
    }

    [Fact]
    public async Task ResolveTemplatePackageAsync_IncludePrHivesTrue_WithHivesPresent_AllChannelsParticipate()
    {
        // The `aspire new` code path opts into hives via IncludePrHives: true. When a hive
        // is actually on disk (GetHiveCount() > 0), the resolver must consider every
        // registered channel — not just the implicit. Verified by registering implicit
        // (version 1.0.0) and an explicit pr-12345 (pinned 2.0.0), and asserting that the
        // explicit channel's higher version wins (which is only reachable if it
        // participated in the candidate set).
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var hivesDir = workspace.CreateDirectory(".aspire").CreateSubdirectory("hives");
        hivesDir.CreateSubdirectory("pr-12345");

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
            ChannelOverride: null,
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: true);

        var selection = await service.ResolveTemplatePackageAsync(query, CancellationToken.None);

        Assert.Equal("2.0.0", selection.Package.Version);
        Assert.Equal(PackageChannelType.Explicit, selection.Channel.Type);
        Assert.Equal("pr-12345", selection.Channel.Name);
    }

    [Fact]
    public async Task ResolveTemplatePackageAsync_IncludePrHivesTrue_NoHivesOnDisk_RestrictsToImplicit()
    {
        // Opt-in alone is not enough: the user must also have hive directories. Without
        // them, even with IncludePrHives: true, the resolver must still restrict to the
        // implicit channel. This is what protects developers running `aspire new` on a
        // clean machine from accidentally pulling from an explicit channel that was
        // registered but never installed.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // Intentionally do NOT create a hives directory. This is the precondition under
        // test — GetHiveCount() must return 0 so the AND short-circuits to false.

        var executionContext = CreateExecutionContextWithHives(
            workspace.WorkspaceRoot,
            new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives")));

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
            ChannelOverride: null,
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: true);

        var selection = await service.ResolveTemplatePackageAsync(query, CancellationToken.None);

        Assert.Equal("1.0.0", selection.Package.Version);
        Assert.Equal(PackageChannelType.Implicit, selection.Channel.Type);
    }

    [Fact]
    public async Task ResolveTemplatePackageAsync_IncludePrHivesFalse_IgnoresHivesEvenWhenPresent()
    {
        // The `aspire init` code path passes IncludePrHives: false intentionally so a
        // developer with stale ~/.aspire/hives/* doesn't get a different template than on
        // a clean machine. Even with a hive present, the resolver must restrict to the
        // implicit channel.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var hivesDir = workspace.CreateDirectory(".aspire").CreateSubdirectory("hives");
        hivesDir.CreateSubdirectory("pr-12345");

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
            ChannelOverride: null,
            VersionOverride: null,
            SourceOverride: null,
            IncludePrHives: false);

        var selection = await service.ResolveTemplatePackageAsync(query, CancellationToken.None);

        Assert.Equal("1.0.0", selection.Package.Version);
        Assert.Equal(PackageChannelType.Implicit, selection.Channel.Type);
    }

    private static CliExecutionContext CreateExecutionContextWithHives(DirectoryInfo workingDirectory, DirectoryInfo hivesDirectory)
    {
        var cacheDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "cache"));
        var sdksDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "sdks"));
        var logsDirectory = new DirectoryInfo(Path.Combine(workingDirectory.FullName, ".aspire", "logs"));
        return new CliExecutionContext(
            workingDirectory, hivesDirectory, cacheDirectory, sdksDirectory, logsDirectory,
            Path.Combine(logsDirectory.FullName, "test.log"));
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
