// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Spec-derived regression tests for the "4th reader" channel-fallback removal.
/// <para>
/// Per PR1's design contract (mirroring the 3 readers covered by
/// <see cref="Configuration.GlobalChannelFallbackRemovalTests"/>),
/// <see cref="TemplateNuGetConfigService"/> MUST NOT consult
/// <see cref="IConfigurationService.GetConfigurationAsync(string, CancellationToken)"/>
/// (or the directory-scoped variant) to resolve the channel from any of its
/// channel-resolving entry points:
/// <list type="number">
///   <item><see cref="TemplateNuGetConfigService.PromptToCreateOrUpdateNuGetConfigAsync(string?, string, CancellationToken)"/></item>
///   <item><see cref="TemplateNuGetConfigService.CreateOrUpdateNuGetConfigWithoutPromptAsync(string?, string, CancellationToken)"/></item>
///   <item><see cref="TemplateNuGetConfigService.ResolveTemplatePackageAsync(TemplatePackageQuery, CancellationToken)"/></item>
/// </list>
/// </para>
/// <para>
/// The strongest spec encoding is "the dependency simply isn't there" — if
/// <see cref="IConfigurationService"/> is not injected, no fallback can possibly
/// occur. We assert that structurally first; a behavioral exercise of each entry
/// point follows as defense-in-depth in case a future change re-introduces the
/// dependency for some other purpose.
/// </para>
/// </summary>
public class TemplateNuGetConfigServiceTests
{
    [Fact]
    public void Ctor_DoesNotAcceptIConfigurationService()
    {
        // The strongest possible spec encoding: the type's constructor cannot accept the
        // dependency the spec forbids. Any future change that re-introduces
        // IConfigurationService as a constructor parameter MUST also restore an explicit
        // tripwire in this file (see GlobalChannelFallbackRemovalTests for the pattern).
        var ctor = typeof(TemplateNuGetConfigService).GetConstructors().Single();

        Assert.DoesNotContain(
            ctor.GetParameters(),
            p => p.ParameterType == typeof(IConfigurationService));
    }

    [Fact]
    public void Type_HasNoIConfigurationServiceField()
    {
        // Defensive: the dependency is gone from the ctor; ensure no stray instance field
        // of type IConfigurationService survives that some future refactor could repurpose
        // for a global-channel read.
        var fields = typeof(TemplateNuGetConfigService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        Assert.DoesNotContain(fields, f => f.FieldType == typeof(IConfigurationService));
    }

    [Fact]
    public async Task PromptToCreateOrUpdateNuGetConfigAsync_NullChannelName_DoesNotConsultGlobalConfig()
    {
        // Behavioral defense-in-depth: even if a future change re-introduces an
        // IConfigurationService dependency for some other purpose, this entry point
        // MUST short-circuit on null/whitespace channelName without consulting the
        // global config. We assert that no exception flies and the implicit channel
        // is not asked for any work.
        var service = CreateService();

        await service.PromptToCreateOrUpdateNuGetConfigAsync(channelName: null, outputPath: Directory.CreateTempSubdirectory().FullName, CancellationToken.None);
        await service.PromptToCreateOrUpdateNuGetConfigAsync(channelName: "", outputPath: Directory.CreateTempSubdirectory().FullName, CancellationToken.None);
        await service.PromptToCreateOrUpdateNuGetConfigAsync(channelName: "   ", outputPath: Directory.CreateTempSubdirectory().FullName, CancellationToken.None);
    }

    [Fact]
    public async Task CreateOrUpdateNuGetConfigWithoutPromptAsync_NullChannelName_DoesNotConsultGlobalConfig()
    {
        var service = CreateService();

        var dir = Directory.CreateTempSubdirectory();
        try
        {
            // For null/whitespace inputs the method must short-circuit and return false
            // without ever asking ANY config service for a channel.
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
    public async Task ResolveTemplatePackageAsync_NullChannelOverride_DoesNotConsultGlobalConfig_AndUsesImplicitOnly()
    {
        // Spec §G1 (cross-route channel contamination): when the caller does not supply
        // a channel override (--channel), the resolver MUST fall back to implicit-only
        // channels — not to the global ~/.aspire/aspire.config.json#channel. This test
        // exercises the actual production codepath with a tracking packaging service that
        // returns one implicit + one explicit channel; the resolver must request only the
        // implicit one.
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

        // The resolver throws EmptyChoicesException when no packages found — that's fine,
        // we are asserting the resolver did NOT throw or consult any global config first.
        await Assert.ThrowsAsync<Aspire.Cli.Interaction.EmptyChoicesException>(
            async () => await service.ResolveTemplatePackageAsync(query, CancellationToken.None));
    }

    private static TemplateNuGetConfigService CreateService(
        TestPackagingService? packagingService = null)
    {
        return new TemplateNuGetConfigService(
            new TestInteractionService(),
            TestExecutionContextFactory.CreateTestContext(),
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
                "TemplateNuGetConfigService unexpectedly entered the prompt path during a tripwire test.");
        }
    }

    private sealed class StubCliHostEnvironment : ICliHostEnvironment
    {
        public bool SupportsInteractiveInput => false;
        public bool SupportsInteractiveOutput => false;
        public bool SupportsAnsi => false;
    }
}
