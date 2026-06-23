// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Commands;

/// <summary>
/// Behavioral guard on <see cref="NewCommand"/>'s channel resolution. The command sources its
/// channel preference from <c>--channel</c>, <see cref="CliExecutionContext.IdentityChannel"/>,
/// and the registered channel set only — never from the global <c>IConfigurationService</c>
/// <c>channel</c> key. This test pins that contract so a regression can't quietly re-introduce
/// cross-route channel contamination.
/// </summary>
public class NewCommandChannelResolutionTests(ITestOutputHelper outputHelper)
{
    /// <summary>
    /// Negative-shape tripwire: <c>aspire new</c> must never read the <c>channel</c> key from
    /// the global <see cref="IConfigurationService"/>. The injected configuration service
    /// throws on any <c>GetConfigurationAsync</c> or <c>GetConfigurationFromDirectoryAsync</c>
    /// call whose key is <c>channel</c>; if the command invokes either, the test fails with
    /// the thrown message. Mirrors <c>InitCommand_DoesNotConsultGlobalConfigurationServiceForChannelKey</c>.
    /// </summary>
    [Fact]
    public async Task NewCommand_DoesNotConsultGlobalConfigurationServiceForChannelKey()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var tripwireConfigService = new global::Aspire.Cli.Tests.TestServices.TestConfigurationService
        {
            OnGetConfiguration = key =>
            {
                if (string.Equals(key, "channel", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "aspire new must not consult IConfigurationService for the 'channel' key. " +
                        "Channel resolution sources from --channel and CliExecutionContext.IdentityChannel only.");
                }
                return null;
            },
            OnGetConfigurationFromDirectory = (key, _) =>
            {
                if (string.Equals(key, "channel", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "aspire new must not consult IConfigurationService.GetConfigurationFromDirectoryAsync " +
                        "for the 'channel' key. Channel resolution sources from --channel and CliExecutionContext.IdentityChannel only.");
                }
                return null;
            }
        };

        string? capturedTemplateVersion = null;

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ConfigurationServiceFactory = _ => tripwireConfigService;

            // Pin a single Implicit channel so the template resolver has a definite fall-through
            // target. The assertion is about IConfigurationService NOT being touched for "channel";
            // the channel set itself is incidental.
            options.PackagingServiceFactory = _ =>
            {
                var fakeCache = new FakeNuGetPackageCache
                {
                    GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                        Task.FromResult<IEnumerable<NuGetPackage>>(
                            [new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "13.3.0" }])
                };
                var implicitChannel = PackageChannel.CreateImplicitChannel(fakeCache, new TestFeatures(), NullLogger.Instance);
                return new TestPackagingService
                {
                    GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([implicitChannel])
                };
            };

            options.DotNetCliRunnerFactory = _ =>
            {
                var runner = new TestDotNetCliRunner();
                runner.InstallTemplateAsyncCallback = (_, version, _, _, _, _, _) =>
                {
                    capturedTemplateVersion = version;
                    return (0, version);
                };
                runner.NewProjectAsyncCallback = (_, _, outputPath, _, _) =>
                {
                    Directory.CreateDirectory(outputPath);
                    return 0;
                };
                return runner;
            };
        });

        using var serviceProvider = services.BuildServiceProvider();
        var newCommand = serviceProvider.GetRequiredService<NewCommand>();

        var parseResult = newCommand.Parse("new aspire-starter --name TestApp --output ./output --use-redis-cache --test-framework None");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        // The template version came from the Implicit channel's package cache. If the tripwire
        // had been triggered, the run would have failed before reaching install.
        Assert.Equal("13.3.0", capturedTemplateVersion);
    }

    /// <summary>
    /// Channel-resolution contract: when the running CLI's identity is a non-local channel
    /// (daily / staging / stable) and no <c>--channel</c> is passed, <c>aspire new</c> must
    /// resolve the channel whose name matches the identity — not the Implicit (nuget.org)
    /// channel. Prerelease identities (daily/staging) further pin the template version to the
    /// current CLI/SDK so the bundled server and restored Aspire packages stay on the same
    /// version; the stable identity falls through to the highest shipped stable package because
    /// the stable feed doesn't filter the running CLI's version out of search. The stable
    /// identity still resolves its template <em>version</em> from the stable channel (proved by
    /// the 13.5.0 pickup), but its channel name is NOT pinned into the project
    /// (<c>ShouldPersistChannelName</c> excludes <c>stable</c>) so the new project keeps the
    /// ambient nuget.org configuration; daily/staging/pr do pin so restores hit their feed.
    /// </summary>
    [Theory]
    [InlineData(PackageChannelNames.Daily, "13.4.0-preview.1.99999.1", null)]
    [InlineData(PackageChannelNames.Stable, "13.5.0", "13.5.0")]
    public async Task NewCommand_NoChannelArg_ResolvesTemplateFromIdentityChannel(string identityChannel, string identityChannelVersion, string? expectedVersion)
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: identityChannel,
            channelOptionArg: null,
            identityChannelVersion: identityChannelVersion);

        Assert.Equal(expectedVersion ?? VersionHelper.GetDefaultSdkVersion(), captured.Version);

        if (string.Equals(identityChannel, PackageChannelNames.Stable, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Null(captured.Channel);
        }
        else
        {
            Assert.Equal(identityChannel, captured.Channel);
        }
    }

    [Fact]
    public async Task NewCommand_NoChannelArg_DailyChannelWithoutExactCliVersion_PinsTemplateToCurrentCliVersion()
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: PackageChannelNames.Daily,
            channelOptionArg: null,
            identityChannelVersion: "13.5.0-preview.1.99999.1");

        Assert.Equal(VersionHelper.GetDefaultSdkVersion(), captured.Version);
        Assert.Equal(PackageChannelNames.Daily, captured.Channel);
    }

    /// <summary>
    /// PR-channel CLI is already covered by the local-build channel branch retained in
    /// <see cref="NewCommand"/>. Pinned here so a future refactor doesn't regress the
    /// well-tested PR-hive path while reshaping the identity-channel selection.
    /// </summary>
    [Fact]
    public async Task NewCommand_NoChannelArg_PrChannelIdentity_ResolvesTemplateFromPrChannel()
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: "pr-99999",
            channelOptionArg: null,
            identityChannelVersion: "13.4.0-pr.99999.gabc123");

        Assert.Equal("13.4.0-pr.99999.gabc123", captured.Version);
        Assert.Equal("pr-99999", captured.Channel);
    }

    /// <summary>
    /// Defensive: when the identity channel is something that isn't a registered channel
    /// (typo, future addition, etc.), the resolver must fall back to the Implicit (nuget.org)
    /// channel rather than failing outright. Otherwise any unrecognized identity would lock
    /// out <c>aspire new</c> entirely.
    /// <para>
    /// When the Implicit channel wins, the resolved channel name is NOT persisted into
    /// <c>inputs.Channel</c> (so the per-project <c>aspire.config.json</c> stays
    /// unpinned and inherits the user's ambient NuGet configuration).
    /// </para>
    /// </summary>
    [Fact]
    public async Task NewCommand_NoChannelArg_IdentityChannelNotRegistered_FallsBackToImplicit()
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: "stalbe", // intentional typo: not registered as a channel
            channelOptionArg: null,
            identityChannelVersion: null);

        Assert.Equal("13.3.0", captured.Version); // value from Implicit channel
        Assert.Null(captured.Channel); // Implicit channels never persist a channel pin
    }

    /// <summary>
    /// Issue #17121 regression guard: a staging-identity CLI should have a registered
    /// staging channel from <c>PackagingService.GetChannelsAsync</c>, so <c>aspire new</c>
    /// resolves the channel from staging instead of falling back to the Implicit NuGet.org
    /// channel, while keeping the template version pinned to the current CLI.
    /// </summary>
    [Fact]
    public async Task NewCommand_NoChannelArg_StagingIdentityWithStagingChannelRegistered_ResolvesTemplateFromStaging()
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: PackageChannelNames.Staging,
            channelOptionArg: null,
            identityChannelVersion: "13.4.0-rc.1.99999.1");

        Assert.Equal(VersionHelper.GetDefaultSdkVersion(), captured.Version);
        Assert.Equal(PackageChannelNames.Staging, captured.Channel);
    }

    /// <summary>
    /// Explicit <c>--channel stable</c> must always override the running CLI's identity channel —
    /// so a developer on a daily CLI can still scaffold a stable-channel project for reproduction
    /// or migration testing. When the CLI's own version is not published on the stable feed
    /// (a daily-shape or PR-shape build), the resolver falls through to the highest shipped stable
    /// package rather than forcing the unpublishable CLI version into the generated apphost.
    /// Pinning to the current CLI version is reserved for prerelease channels — see
    /// <see cref="NewCommand_ExplicitPrereleaseChannel_PrefersCurrentCliVersionWhenAvailable"/>.
    /// </summary>
    [Fact]
    public async Task NewCommand_ExplicitChannelArg_OverridesIdentityChannel()
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: PackageChannelNames.Daily,
            channelOptionArg: PackageChannelNames.Stable,
            identityChannelVersion: "13.4.0-preview.1.99999.1");

        // Highest shipped stable from the stable channel feed in the test fixture.
        Assert.Equal("13.5.0", captured.Version);
        Assert.Equal(PackageChannelNames.Stable, captured.Channel);
    }

    /// <summary>
    /// A shipped CLI must prefer its own SDK/template version from an explicitly selected
    /// non-local channel instead of floating to a newer daily/staging package from the same feed.
    /// </summary>
    [Theory]
    [InlineData(PackageChannelNames.Daily)]
    [InlineData(PackageChannelNames.Staging)]
    public async Task NewCommand_ExplicitPrereleaseChannel_PrefersCurrentCliVersionWhenAvailable(string channelName)
    {
        var cliVersion = VersionHelper.GetDefaultSdkVersion();

        var captured = await CaptureTemplateInputsAsync(
            identityChannel: channelName,
            channelOptionArg: channelName,
            identityChannelVersion: cliVersion,
            identityChannelVersions: ["99.0.0-preview.1", cliVersion]);

        Assert.Equal(cliVersion, captured.Version);
        Assert.Equal(channelName, captured.Channel);
    }

    /// <summary>
    /// SmokeTest regression guard: when a non-stable-shape CLI (PR build, daily-shape preview, etc.)
    /// is invoked with <c>--channel stable</c>, the resolver must NOT pin the template to the
    /// running CLI's version. The CLI's own version is not published on the stable feed, so a
    /// generated <c>apphost.cs</c> referencing it would fail NuGet restore. The fallback that pins
    /// to the current CLI version is reserved for prerelease channels (daily, staging) where the
    /// feed search filter is what hides the otherwise-restorable shipped stable package.
    /// </summary>
    [Theory]
    [InlineData("13.4.0-preview.1.99999.1")] // daily-shape CLI
    [InlineData("13.4.0-pr.17573.gabc1234")] // PR-shape CLI (e2e SmokeTests scenario)
    public async Task NewCommand_ExplicitStableChannel_NonStableCliVersion_FallsBackToHighestShippedStable(string identityChannelVersion)
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: PackageChannelNames.Daily,
            channelOptionArg: PackageChannelNames.Stable,
            identityChannelVersion: identityChannelVersion);

        Assert.Equal("13.5.0", captured.Version);
        Assert.Equal(PackageChannelNames.Stable, captured.Channel);
    }

    /// <summary>
    /// Issue: <c>aspire new aspire-starter</c> (and <c>aspire-starter-csharp-typescript</c>)
    /// run through the <see cref="TemplateRuntime.DotNet"/> path, which delegates template
    /// package resolution to <c>TemplateNuGetConfigService.ResolveTemplatePackageAsync</c>
    /// using <c>TemplateInputs.Channel</c> as the <c>RequestedChannel</c>. When no
    /// <c>--channel</c> is supplied, <see cref="NewCommand"/> must forward the running CLI's
    /// <see cref="CliExecutionContext.IdentityChannel"/> through <c>inputs.Channel</c> so the
    /// DotNet template path searches the identity-matching feed for
    /// <c>Aspire.ProjectTemplates</c> — symmetrical to the CLI-runtime path
    /// (<see cref="NewCommand_NoChannelArg_ResolvesTemplateFromIdentityChannel"/>).
    /// <para>
    /// Without this forwarding, a daily / staging / release-branch CLI silently resolves the
    /// templates package from the Implicit (nuget.org) channel — for example, on a 13.4
    /// CLI it fails to discover the matching <c>13.4.0</c> template and falls back to the
    /// latest stable on nuget.org. Passing <c>--channel</c> explicitly works because that
    /// value is already forwarded into <c>inputs.Channel</c>.
    /// </para>
    /// <para>
    /// The three feed-backed identity shapes are exercised — PR (developer dogfood build
    /// against a hive), daily (nightly dnceng feed), and staging (release-branch dnceng feed).
    /// The <c>stable</c> identity is deliberately NOT forwarded (its packages are on nuget.org,
    /// the ambient default), which is covered separately by
    /// <see cref="NewCommand_DotNetRuntimeTemplate_NoChannelArg_StableIdentity_DoesNotForwardChannel"/>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("pr-99999", "13.4.0-pr.99999.gabc123")]
    [InlineData(PackageChannelNames.Daily, "13.4.0-preview.1.99999.1")]
    [InlineData(PackageChannelNames.Staging, "13.4.0-rc.1.99999.1")]
    public async Task NewCommand_DotNetRuntimeTemplate_NoChannelArg_ForwardsIdentityChannelToInputs(string identityChannel, string identityChannelVersion)
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: identityChannel,
            channelOptionArg: null,
            identityChannelVersion: identityChannelVersion,
            runtime: TemplateRuntime.DotNet);

        // DotNet-runtime templates resolve the template package version themselves inside
        // DotNetTemplateFactory.ApplyTemplateAsync — NewCommand does not populate inputs.Version
        // for this runtime — so only inputs.Channel is asserted here.
        Assert.Equal(identityChannel, captured.Channel);
    }

    /// <summary>
    /// Counterpart to <see cref="NewCommand_DotNetRuntimeTemplate_NoChannelArg_ForwardsIdentityChannelToInputs"/>
    /// for the <c>stable</c> identity: a stable-identity DotNet-runtime <c>aspire new</c> must
    /// leave <c>inputs.Channel</c> as <c>null</c>. The stable channel maps everything to
    /// nuget.org (the ambient default source), so pinning it into the project — and dropping a
    /// <c>&lt;clear/&gt;</c>-based NuGet.config for it — would be redundant and would wipe the
    /// user's other feeds. The stable channel is still registered, proving the skip is driven by
    /// the channel name (<c>ShouldPersistChannelName</c>) rather than an absence of mappings.
    /// </summary>
    [Fact]
    public async Task NewCommand_DotNetRuntimeTemplate_NoChannelArg_StableIdentity_DoesNotForwardChannel()
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: PackageChannelNames.Stable,
            channelOptionArg: null,
            identityChannelVersion: "13.5.0",
            runtime: TemplateRuntime.DotNet);

        Assert.Null(captured.Channel);
    }

    /// <summary>
    /// Defensive: when the identity channel is something that isn't a registered channel
    /// (typo, future addition, locally-built CLI without the local hive installed, etc.),
    /// <see cref="NewCommand"/> must NOT blindly forward the unrecognized identity into
    /// <c>inputs.Channel</c> — doing so would make the DotNet template path throw
    /// <c>ChannelNotFoundException</c> inside <c>TemplateNuGetConfigService.ResolveTemplatePackageAsync</c>.
    /// Instead, leave <c>inputs.Channel</c> as <c>null</c> so the resolver consults the Implicit
    /// (nuget.org) channel and the new project inherits the user's ambient NuGet configuration.
    /// Mirrors the CLI-runtime contract pinned by
    /// <see cref="NewCommand_NoChannelArg_IdentityChannelNotRegistered_FallsBackToImplicit"/>.
    /// </summary>
    [Fact]
    public async Task NewCommand_DotNetRuntimeTemplate_NoChannelArg_IdentityChannelNotRegistered_FallsBackToNull()
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: "stalbe", // intentional typo: not registered as a channel
            channelOptionArg: null,
            identityChannelVersion: null,
            runtime: TemplateRuntime.DotNet);

        // Implicit-fallback case: inputs.Channel stays null so DotNetTemplateFactory does not
        // pin a per-project channel and the new project uses the ambient NuGet configuration.
        Assert.Null(captured.Channel);
    }

    /// <summary>
    /// Explicit <c>--channel</c> on a DotNet-runtime template (aspire-starter family) must
    /// still flow through <c>inputs.Channel</c> verbatim and override the running CLI's
    /// identity. Pinned here so the identity-channel forwarding fix doesn't accidentally
    /// clobber an explicit user choice (e.g. a daily CLI scaffolding a stable-channel
    /// project for migration testing).
    /// </summary>
    [Fact]
    public async Task NewCommand_DotNetRuntimeTemplate_ExplicitChannelArg_OverridesIdentityChannel()
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: PackageChannelNames.Daily,
            channelOptionArg: PackageChannelNames.Stable,
            identityChannelVersion: "13.4.0-preview.1.99999.1",
            runtime: TemplateRuntime.DotNet);

        Assert.Equal(PackageChannelNames.Stable, captured.Channel);
    }

    /// <summary>
    /// <c>--version</c> on a DotNet-runtime template must still flow through to
    /// <c>inputs.Version</c> AND identity-channel forwarding must still populate
    /// <c>inputs.Channel</c>. The version pin tells <c>ResolveTemplatePackageAsync</c> which
    /// package to pick, but the channel pin still selects the feed that package is fetched
    /// from and the per-project NuGet.config mappings the generated project will use.
    /// Without both, a daily CLI passing <c>--version 13.4.0-preview.1.99999.1</c> would
    /// resolve the version against nuget.org (where prerelease daily builds aren't published)
    /// and fail.
    /// </summary>
    [Fact]
    public async Task NewCommand_DotNetRuntimeTemplate_VersionOverride_StillForwardsIdentityChannel()
    {
        var captured = await CaptureTemplateInputsAsync(
            identityChannel: PackageChannelNames.Daily,
            channelOptionArg: null,
            identityChannelVersion: "13.4.0-preview.1.99999.1",
            runtime: TemplateRuntime.DotNet,
            versionOptionArg: "13.4.0-preview.1.99999.1");

        Assert.Equal("13.4.0-preview.1.99999.1", captured.Version);
        Assert.Equal(PackageChannelNames.Daily, captured.Channel);
    }

    /// <summary>
    /// Invokes <see cref="NewCommand"/> with a fake template that captures the
    /// <see cref="TemplateInputs"/> handed to it. Its <c>Version</c> reflects which channel
    /// won template-version resolution; its <c>Channel</c> reflects what the template
    /// factories will persist into the new project's <c>aspire.config.json</c> (or use as
    /// the <c>RequestedChannel</c> for DotNet-runtime templates).
    /// </summary>
    /// <param name="identityChannel">Identity baked into the CLI under test.</param>
    /// <param name="channelOptionArg">Value passed via <c>--channel</c>, or <c>null</c> to omit the flag.</param>
    /// <param name="identityChannelVersion">
    /// Version returned by the channel whose name matches <paramref name="identityChannel"/>,
    /// or <c>null</c> when that channel is not registered.
    /// </param>
    /// <param name="identityChannelVersions">Optional list of versions exposed by the identity channel.</param>
    /// <param name="runtime">Template runtime kind. CLI runtime drives ResolveCliTemplateVersionAsync; DotNet runtime mirrors aspire-starter.</param>
    private async Task<CapturedTemplateInputs> CaptureTemplateInputsAsync(
        string identityChannel,
        string? channelOptionArg,
        string? identityChannelVersion,
        IEnumerable<string>? identityChannelVersions = null,
        TemplateRuntime runtime = TemplateRuntime.Cli,
        string? versionOptionArg = null)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var capturedInputs = new CapturedTemplateInputs();

        // A fake template that intercepts the inputs and returns success without invoking
        // the heavyweight template scaffolding pipeline (RPC, codegen, bundled NuGet restore).
        // The template is registered via a fake ITemplateProvider injected through
        // CliServiceCollectionTestOptions.TemplateProviderFactory.
        //
        // The runtime kind switches which code path inside NewCommand.ExecuteAsync produces
        // inputs.Channel: TemplateRuntime.Cli walks ResolveCliTemplateVersionAsync (which
        // owns the identity-channel fallback for the CLI starters), while TemplateRuntime.DotNet
        // mirrors the path used by aspire-starter / aspire-starter-csharp-typescript, which
        // delegate version/feed selection to DotNetTemplateFactory → TemplateNuGetConfigService.
        // NewCommand is responsible for passing the right channel into inputs.Channel in both
        // cases, so this helper can exercise both with a single shape.
        var fakeTemplate = new CallbackTemplate(
            name: "fake-template",
            description: "Fake template for channel-resolution tests",
            pathDeriverCallback: (ctx, projectName) => Path.Combine(ctx.WorkingDirectory.FullName, projectName),
            applyOptionsCallback: _ => { },
            applyTemplateCallback: (_, inputs, _, _) =>
            {
                capturedInputs.Version = inputs.Version;
                capturedInputs.Channel = inputs.Channel;
                var outputPath = Path.Combine(workspace.WorkspaceRoot.FullName, "captured");
                Directory.CreateDirectory(outputPath);
                return Task.FromResult(new TemplateResult(CliExitCodes.Success, outputPath));
            },
            runtime: runtime,
            languageId: KnownLanguageId.TypeScript);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => BuildExecutionContextWithIdentity(workspace, identityChannel);

            options.TemplateProviderFactory = _ => new SingleTemplateProvider(fakeTemplate);

            options.PackagingServiceFactory = _ => BuildPackagingService(identityChannel, identityChannelVersion, identityChannelVersions);
        });

        using var serviceProvider = services.BuildServiceProvider();
        var newCommand = serviceProvider.GetRequiredService<NewCommand>();

        var channelArg = string.IsNullOrEmpty(channelOptionArg) ? "" : $" --channel {channelOptionArg}";
        var versionArg = string.IsNullOrEmpty(versionOptionArg) ? "" : $" --version {versionOptionArg}";
        var parseResult = newCommand.Parse($"new fake-template --name TestApp --output ./captured{channelArg}{versionArg}");
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        return capturedInputs;
    }

    /// <summary>
    /// Builds a fake channel set that mirrors the shape produced by the real
    /// <c>PackagingService</c> (an Implicit nuget.org channel plus Stable / Daily / Staging /
    /// pr-* explicit channels), but with deterministic per-channel template versions so
    /// tests can identify which channel won resolution.
    /// </summary>
    private static IPackagingService BuildPackagingService(
        string identityChannel,
        string? identityChannelVersion,
        IEnumerable<string>? identityChannelVersions)
    {
        var identityVersions = identityChannelVersions?.ToArray()
            ?? (identityChannelVersion is null ? [] : [identityChannelVersion]);

        // Implicit channel always returns the stable token so a "fell-through to Implicit"
        // outcome is distinguishable from an identity-channel pickup.
        var implicitCache = new FakeNuGetPackageCache
        {
            GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                Task.FromResult<IEnumerable<NuGetPackage>>(
                    [new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "13.3.0" }])
        };
        var implicitChannel = PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures(), NullLogger.Instance);

        // Always register a stable channel — matches what PackagingService advertises in
        // production. Its version (13.5.0) is distinct from Implicit (13.3.0) so a test
        // that expects the stable explicit channel won't accidentally match Implicit.
        var stableCache = new FakeNuGetPackageCache
        {
            GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                Task.FromResult<IEnumerable<NuGetPackage>>(
                    [new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "13.5.0" }])
        };
        var stableChannel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Stable,
            PackageChannelQuality.Stable,
            [new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json")],
            stableCache,
            features: new TestFeatures(),
            NullLogger.Instance);

        var channels = new List<PackageChannel> { implicitChannel, stableChannel };

        // Register a non-stable explicit channel matching the identity, when the test
        // scenario calls for it. Deliberately omitted in the "identity not registered"
        // case so fallback to Implicit can be observed.
        var isDailyOrStaging = identityVersions.Length > 0 &&
            !string.Equals(identityChannel, PackageChannelNames.Stable, StringComparison.OrdinalIgnoreCase) &&
            !identityChannel.StartsWith("pr-", StringComparison.OrdinalIgnoreCase);
        if (isDailyOrStaging)
        {
            var explicitCache = new FakeNuGetPackageCache
            {
                GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                    Task.FromResult<IEnumerable<NuGetPackage>>(
                        identityVersions.Select(version => new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = version }))
            };
            channels.Add(PackageChannel.CreateExplicitChannel(
                identityChannel,
                PackageChannelQuality.Prerelease,
                [
                    new PackageMapping("Aspire*", "https://example.invalid/feed/v3/index.json"),
                    new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json"),
                ],
                explicitCache,
                features: new TestFeatures(),
                NullLogger.Instance));
        }

        // PR hives are an additional explicit channel shape (PackageChannelQuality.Both
        // with a local-path Aspire* mapping). Register so the local-build identity branch
        // still has a concrete channel to match against.
        if (identityChannel.StartsWith("pr-", StringComparison.OrdinalIgnoreCase) &&
            identityChannelVersion is not null)
        {
            var prCache = new FakeNuGetPackageCache
            {
                GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                    Task.FromResult<IEnumerable<NuGetPackage>>(
                        [new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "pr-hive", Version = identityChannelVersion }])
            };
            channels.Add(PackageChannel.CreateExplicitChannel(
                identityChannel,
                PackageChannelQuality.Both,
                [
                    new PackageMapping("Aspire*", "/fake/pr-hive/packages"),
                    new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json"),
                ],
                prCache,
                features: new TestFeatures(),
                NullLogger.Instance));
        }

        return new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(channels)
        };
    }

    private static CliExecutionContext BuildExecutionContextWithIdentity(TemporaryWorkspace workspace, string identityChannel)
    {
        return workspace.CreateExecutionContext(
            identityChannel: identityChannel);
    }

    private sealed class CapturedTemplateInputs
    {
        public string? Version { get; set; }
        public string? Channel { get; set; }
    }

    /// <summary>
    /// Minimal <see cref="ITemplateProvider"/> exposing a single template so
    /// <see cref="NewCommand"/> registers it as the only subcommand and tests can drive
    /// <c>aspire new &lt;name&gt;</c> deterministically.
    /// </summary>
    private sealed class SingleTemplateProvider(ITemplate template) : ITemplateProvider
    {
        public IEnumerable<ITemplate> GetTemplates() => [template];
        public Task<IEnumerable<ITemplate>> GetTemplatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<ITemplate>>([template]);
        public Task<IEnumerable<ITemplate>> GetInitTemplatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IEnumerable<ITemplate>>([template]);
    }
}
