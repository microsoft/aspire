// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Templating;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Tests.Commands;

/// <summary>
/// Behavioral guard on the <c>channel</c> value that <c>aspire new &lt;template&gt;</c>
/// persists into <c>aspire.config.json</c>. Drives <see cref="NewCommand"/> end-to-end
/// through real <c>CliTemplateFactory</c> + real <c>ScaffoldingService</c> /
/// <c>TypeScriptStarterTemplate</c> / <c>{Go,Python}StarterTemplate</c> writers, with only
/// <see cref="IAppHostServerProjectFactory"/> swapped for a fake whose
/// <see cref="IAppHostServerProject.PrepareAsync"/> returns failure so the early on-disk
/// side-effect is captured without touching the network, the dotnet CLI, npm, or RPC.
/// <para>
/// This is the integration-level regression net for the channel-pin/SDK-version mismatch
/// bug class (PR #17120 + davidfowl follow-up). The original fix landed in the TS starter
/// factory; the follow-up extended it to <see cref="Aspire.Cli.Scaffolding.ScaffoldingService"/>
/// (the path used by every empty template + <c>aspire init</c> polyglot). The bug class
/// itself, however, lives at any writer of <c>aspire.config.json#channel</c> — so this
/// class parameterises across <em>every</em> CLI template that ships an
/// <c>aspire.config.json</c> writer, asserting both shapes of the contract:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// Templates that pin a channel (TS starter, all empty templates + polyglot init):
/// the value is persisted iff <see cref="NewCommand"/> resolved one (Explicit
/// <c>--channel</c>, or an identity-match against a registered Explicit channel) —
/// never as a silent fallback to <see cref="CliExecutionContext.IdentityChannel"/>.
/// </description>
/// </item>
/// <item>
/// <description>
/// Templates that never pin a channel (Go starter, Python starter): the persisted
/// <c>channel</c> stays <see langword="null"/> regardless of CLI identity or
/// <c>--channel</c>. This is the tripwire that catches anyone re-introducing the
/// dead channel-write code that the PR #17120 fix removed.
/// </description>
/// </item>
/// </list>
/// <para>
/// Companion to <c>NewCommandChannelResolutionTests</c> (verifies the value
/// <see cref="NewCommand"/> hands to template factories via <c>TemplateInputs.Channel</c>),
/// <c>ChannelReseedTests</c> (verifies <see cref="Aspire.Cli.Scaffolding.ScaffoldingService"/>
/// persists <c>ScaffoldContext.Channel</c> verbatim), and
/// <c>TypeScriptStarterSmokeTests</c> (end-to-end coverage on the daily smoke workflow).
/// </para>
/// </summary>
public class NewCommandTemplateConfigPersistenceTests(ITestOutputHelper outputHelper)
{
    /// <summary>
    /// Templates that pin <c>aspire.config.json#channel</c> when <see cref="NewCommand"/>
    /// resolves an Explicit channel. Covers both writer code paths:
    /// <see cref="Aspire.Cli.Scaffolding.ScaffoldingService"/> (the empty templates + polyglot init)
    /// and <c>CliTemplateFactory.TypeScriptStarterTemplate</c> (the TS starter's own write).
    /// Direct regression for the bug class fixed by PR #17120 + davidfowl follow-up:
    /// when the CLI's identity isn't a registered Explicit channel, no channel must be
    /// persisted — otherwise <c>aspire add</c> / <c>aspire restore</c> route
    /// <c>Aspire.*</c> through a PSM that can't satisfy the SDK version.
    /// </summary>
    [Theory]
    [InlineData(KnownTemplateId.TypeScriptEmptyAppHost, "apphost.ts")]
    [InlineData(KnownTemplateId.PythonEmptyAppHost, "apphost.py")]
    [InlineData(KnownTemplateId.GoEmptyAppHost, "apphost.go")]
    [InlineData(KnownTemplateId.JavaEmptyAppHost, "AppHost.java")]
    [InlineData(KnownTemplateId.TypeScriptStarter, "apphost.ts")]
    [InlineData(KnownTemplateId.PythonStarter, "apphost.ts")]
    [InlineData(KnownTemplateId.GoStarter, "apphost.go")]
    public async Task ChannelPinningTemplate_IdentityNotRegistered_DoesNotPinChannel(string templateId, string _)
    {
        var persisted = await ScaffoldAndReadPersistedChannelAsync(
            templateId: templateId,
            identityChannel: PackageChannelNames.Daily,
            registerIdentityChannel: false,
            explicitChannelArg: null);

        // No channel pin → PrebuiltAppHostServer aggregates sources from every registered
        // channel when aspire add / aspire restore run later.
        Assert.Null(persisted);
    }

    /// <summary>
    /// Happy path: when the identity matches a registered Explicit channel,
    /// <see cref="NewCommand"/> resolves it and every channel-pinning template persists
    /// the name into <c>aspire.config.json#channel</c>. This is the pin that lets
    /// <c>aspire add</c> route <c>Aspire.*</c> through the matching feed via PSM.
    /// </summary>
    [Theory]
    [InlineData(KnownTemplateId.TypeScriptEmptyAppHost, "apphost.ts")]
    [InlineData(KnownTemplateId.PythonEmptyAppHost, "apphost.py")]
    [InlineData(KnownTemplateId.GoEmptyAppHost, "apphost.go")]
    [InlineData(KnownTemplateId.JavaEmptyAppHost, "AppHost.java")]
    [InlineData(KnownTemplateId.TypeScriptStarter, "apphost.ts")]
    [InlineData(KnownTemplateId.PythonStarter, "apphost.ts")]
    [InlineData(KnownTemplateId.GoStarter, "apphost.go")]
    public async Task ChannelPinningTemplate_IdentityMatchesRegisteredChannel_PinsThatChannel(string templateId, string _)
    {
        var persisted = await ScaffoldAndReadPersistedChannelAsync(
            templateId: templateId,
            identityChannel: PackageChannelNames.Daily,
            registerIdentityChannel: true,
            explicitChannelArg: null);

        Assert.Equal(PackageChannelNames.Daily, persisted);
    }

    /// <summary>
    /// Explicit <c>--channel</c> overrides identity at the resolution layer and propagates
    /// through to the persisted pin for every channel-pinning template — covered at the
    /// resolution layer by <c>NewCommand_ExplicitChannelArg_OverridesIdentityChannel</c>
    /// and asserted here at the persistence layer so a future drift between the two is
    /// caught.
    /// </summary>
    [Theory]
    [InlineData(KnownTemplateId.TypeScriptEmptyAppHost, "apphost.ts")]
    [InlineData(KnownTemplateId.PythonEmptyAppHost, "apphost.py")]
    [InlineData(KnownTemplateId.GoEmptyAppHost, "apphost.go")]
    [InlineData(KnownTemplateId.JavaEmptyAppHost, "AppHost.java")]
    [InlineData(KnownTemplateId.TypeScriptStarter, "apphost.ts")]
    [InlineData(KnownTemplateId.PythonStarter, "apphost.ts")]
    [InlineData(KnownTemplateId.GoStarter, "apphost.go")]
    public async Task ChannelPinningTemplate_ExplicitChannelArg_OverridesIdentityAndPersists(string templateId, string _)
    {
        var persisted = await ScaffoldAndReadPersistedChannelAsync(
            templateId: templateId,
            identityChannel: PackageChannelNames.Daily,
            registerIdentityChannel: true,
            explicitChannelArg: PackageChannelNames.Stable);

        Assert.Equal(PackageChannelNames.Stable, persisted);
    }

    /// <summary>
    /// Issue #17121 regression guard: <c>PackagingService</c> now registers the
    /// <c>staging</c> channel for a staging-identity CLI. Once <see cref="NewCommand"/>
    /// resolves that registered identity channel, channel-pinning templates must persist
    /// <c>channel: staging</c> so later add/restore operations keep using staging.
    /// </summary>
    [Theory]
    [InlineData(KnownTemplateId.TypeScriptEmptyAppHost)]
    [InlineData(KnownTemplateId.TypeScriptStarter)]
    public async Task ChannelPinningTemplate_StagingIdentityWithRegisteredChannel_PinsStagingChannel(string templateId)
    {
        var persisted = await ScaffoldAndReadPersistedChannelAsync(
            templateId: templateId,
            identityChannel: PackageChannelNames.Staging,
            registerIdentityChannel: true,
            explicitChannelArg: null);

        Assert.Equal(PackageChannelNames.Staging, persisted);
    }

    /// <summary>
    /// Issue #17225 regression guard: when <c>PackagingService</c> discovers the running
    /// <c>pr-&lt;N&gt;</c> CLI's matching dogfood install hive, <c>aspire new aspire-empty</c>
    /// must use that channel for CLI template version resolution across the language paths.
    /// C# writes an embedded AppHost template and NuGet.config; guest languages persist the
    /// channel/sdk before the fake prebuilt AppHost prepare failure stops the run.
    /// </summary>
    [Theory]
    [InlineData(KnownLanguageId.CSharp)]
    [InlineData(KnownLanguageId.TypeScript)]
    [InlineData(KnownLanguageId.Python)]
    [InlineData(KnownLanguageId.Go)]
    [InlineData(KnownLanguageId.Java)]
    [InlineData(KnownLanguageId.Rust)]
    public async Task EmptyAppHost_PrDogfoodInstallHiveDiscovered_UsesPrChannelForLanguage(string languageId)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        const string prChannelName = "pr-17225";
        const string prVersion = "13.4.0-pr.17225.gabc123";
        var installPrefix = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "custom-aspire-prefix"));
        var processPath = Path.Combine(installPrefix.FullName, "dogfood", prChannelName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
        File.WriteAllText(processPath, string.Empty);

        var packagesDirectory = Directory.CreateDirectory(Path.Combine(installPrefix.FullName, "hives", prChannelName, "packages"));
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.ProjectTemplates.{prVersion}.nupkg"), string.Empty);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot,
                hivesDirectory: new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives")),
                identityChannel: prChannelName);
            options.PackagingServiceFactory = sp => new PackagingService(
                sp.GetRequiredService<CliExecutionContext>(),
                sp.GetRequiredService<INuGetPackageCache>(),
                sp.GetRequiredService<IFeatures>(),
                sp.GetRequiredService<IConfiguration>(),
                NullLogger<PackagingService>.Instance,
                processPathProvider: () => processPath);

            options.EnabledFeatures =
            [
                KnownFeatures.ExperimentalPolyglotPython,
                KnownFeatures.ExperimentalPolyglotGo,
                KnownFeatures.ExperimentalPolyglotJava,
                KnownFeatures.ExperimentalPolyglotRust,
            ];
        });

        services.AddSingleton<IAppHostServerProjectFactory>(_ => new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (path, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeFailingAppHostServerProject(path))
        });

        using var serviceProvider = services.BuildServiceProvider();
        var newCommand = serviceProvider.GetRequiredService<NewCommand>();

        const string outputDirectoryName = "TemplateOut";
        var parseResult = newCommand.Parse(
            $"new {KnownTemplateId.CSharpEmptyAppHost} --language {languageId} --name TemplateOut --output ./{outputDirectoryName} --localhost-tld false");
        _ = await parseResult.InvokeAsync().DefaultTimeout();

        var outputDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, outputDirectoryName);
        if (languageId.Equals(KnownLanguageId.CSharp, StringComparison.OrdinalIgnoreCase))
        {
            var appHostFile = Path.Combine(outputDirectory, "apphost.cs");
            Assert.True(File.Exists(appHostFile));
            Assert.Contains(prVersion, await File.ReadAllTextAsync(appHostFile));

            var nugetConfig = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "nuget.config"));
            Assert.Contains(packagesDirectory.FullName.Replace('\\', '/'), nugetConfig);
        }
        else
        {
            var config = AspireConfigFile.Load(outputDirectory);
            Assert.NotNull(config);
            Assert.Equal(prChannelName, config.Channel);
            Assert.Equal(prVersion, config.SdkVersion);
        }
    }

    /// <summary>
    /// Drives <c>aspire new &lt;templateId&gt;</c> against the real <see cref="NewCommand"/>,
    /// real <c>CliTemplateFactory</c>, and real <see cref="Aspire.Cli.Scaffolding.ScaffoldingService"/>
    /// — only the <see cref="IAppHostServerProjectFactory"/> is swapped out for a fake whose
    /// <c>PrepareAsync</c> returns failure, so the run terminates cleanly after the early
    /// channel write side-effect we want to inspect.
    /// </summary>
    /// <returns>
    /// The <c>channel</c> value persisted to <c>aspire.config.json</c> in the scaffolded
    /// output directory, or <see langword="null"/> if no <c>aspire.config.json</c> was
    /// written or its <c>channel</c> property is absent.
    /// </returns>
    private async Task<string?> ScaffoldAndReadPersistedChannelAsync(
        string templateId,
        string identityChannel,
        bool registerIdentityChannel,
        string? explicitChannelArg)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => BuildExecutionContextWithIdentity(workspace, identityChannel);
            options.PackagingServiceFactory = _ => BuildPackagingService(identityChannel, registerIdentityChannel);
            // Enable all polyglot feature flags so the Python / Go / Java / Rust templates
            // register as subcommands of `aspire new`. Outside tests these are gated by
            // KnownFeatures.ExperimentalPolyglot* flags; here we want to exercise the full
            // matrix regardless of release state.
            options.EnabledFeatures =
            [
                KnownFeatures.ExperimentalPolyglotPython,
                KnownFeatures.ExperimentalPolyglotGo,
                KnownFeatures.ExperimentalPolyglotJava,
                KnownFeatures.ExperimentalPolyglotRust,
            ];
        });

        // Override the real IAppHostServerProjectFactory so PrepareAsync returns failure
        // and the (real) scaffolding / starter writer terminates without touching the
        // network, the dotnet CLI, or template restore. The earlier channel-write
        // side-effect still runs (when a channel was resolved), which is exactly what
        // this test inspects. Last AddSingleton wins for GetRequiredService, so this
        // replaces the default registration from CliTestHelper.
        services.AddSingleton<IAppHostServerProjectFactory>(_ => new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (path, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeFailingAppHostServerProject(path))
        });

        using var serviceProvider = services.BuildServiceProvider();
        var newCommand = serviceProvider.GetRequiredService<NewCommand>();

        const string outputDirectoryName = "TemplateOut";
        var channelArg = string.IsNullOrEmpty(explicitChannelArg) ? "" : $" --channel {explicitChannelArg}";

        // `--localhost-tld false` short-circuits the bool? confirmation prompt so the
        // test is non-interactive. The value doesn't affect channel persistence.
        var parseResult = newCommand.Parse(
            $"new {templateId} --name TemplateOut --output ./{outputDirectoryName} --localhost-tld false{channelArg}");
        _ = await parseResult.InvokeAsync().DefaultTimeout();

        // We don't assert the exit code — the fake AppHostServerProject deliberately
        // fails, so the run exits non-success after the on-disk write. The contract we
        // test is the *side-effect*: whatever channel the writer decided to persist is
        // what landed in aspire.config.json (or no channel if none was resolved /
        // template doesn't pin).
        var outputDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, outputDirectoryName);
        var config = AspireConfigFile.Load(outputDirectory);
        return config?.Channel;
    }

    private static IPackagingService BuildPackagingService(string identityChannel, bool registerIdentityChannel)
    {
        var implicitCache = new FakeNuGetPackageCache
        {
            GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                Task.FromResult<IEnumerable<NuGetPackage>>(
                    [new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "13.3.0" }])
        };
        var implicitChannel = PackageChannel.CreateImplicitChannel(implicitCache);

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
            stableCache);

        var channels = new List<PackageChannel> { implicitChannel, stableChannel };

        if (registerIdentityChannel &&
            !string.Equals(identityChannel, PackageChannelNames.Stable, StringComparison.OrdinalIgnoreCase))
        {
            // Mirror PackagingService.GetChannelsAsync's shape: PR-hive channels are
            // PackageChannelQuality.Both with a local-path Aspire* mapping, daily/staging
            // are PackageChannelQuality.Prerelease with a remote feed URL.
            var isPrChannel = identityChannel.StartsWith("pr-", StringComparison.OrdinalIgnoreCase);
            var quality = isPrChannel ? PackageChannelQuality.Both : PackageChannelQuality.Prerelease;
            var feed = isPrChannel ? "/fake/pr-hive/packages" : "https://example.invalid/feed/v3/index.json";
            var version = isPrChannel ? "13.4.0-pr.99999.gabc123" : "13.4.0-preview.1.99999.1";
            var cache = new FakeNuGetPackageCache
            {
                GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                    Task.FromResult<IEnumerable<NuGetPackage>>(
                        [new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "test", Version = version }])
            };
            channels.Add(PackageChannel.CreateExplicitChannel(
                identityChannel,
                quality,
                [
                    new PackageMapping("Aspire*", feed),
                    new PackageMapping(PackageMapping.AllPackages, "https://api.nuget.org/v3/index.json"),
                ],
                cache));
        }

        return new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>(channels)
        };
    }

    private static CliExecutionContext BuildExecutionContextWithIdentity(TemporaryWorkspace workspace, string identityChannel)
    {
        return workspace.CreateExecutionContext(identityChannel: identityChannel);
    }
}
