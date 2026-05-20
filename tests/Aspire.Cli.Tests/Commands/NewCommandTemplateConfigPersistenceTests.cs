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
using Aspire.Cli.Utils;
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
    private const string PrChannelName = "pr-17225";
    private static readonly string s_prVersion = VersionHelper.GetDefaultSdkVersion();

    private static readonly PrDogfoodNewTemplateCase[] s_prDogfoodNewTemplateCases =
    [
        PrDogfoodNewTemplateCase.CliConfig(KnownTemplateId.TypeScriptStarter, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.SelectableEmpty(KnownLanguageId.CSharp, PrDogfoodNewTemplateContract.CSharpEmptyAppHost, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.SelectableEmpty(KnownLanguageId.TypeScript, PrDogfoodNewTemplateContract.AspireConfig, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.SelectableEmpty(KnownLanguageId.Python, PrDogfoodNewTemplateContract.AspireConfig, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.SelectableEmpty(KnownLanguageId.Go, PrDogfoodNewTemplateContract.AspireConfig, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.SelectableEmpty(KnownLanguageId.Java, PrDogfoodNewTemplateContract.AspireConfig, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.SelectableEmpty(KnownLanguageId.Rust, PrDogfoodNewTemplateContract.AspireConfig, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.CliConfig(KnownTemplateId.TypeScriptEmptyAppHost, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.CliConfig(KnownTemplateId.PythonEmptyAppHost, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.CliConfig(KnownTemplateId.JavaEmptyAppHost, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.CliConfig(KnownTemplateId.GoEmptyAppHost, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.CliConfig(KnownTemplateId.RustEmptyAppHost, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.CliConfig(KnownTemplateId.PythonStarter, ["--localhost-tld", "false", "--use-redis-cache", "false"]),
        PrDogfoodNewTemplateCase.CliConfig(KnownTemplateId.GoStarter, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.DotNet("aspire-starter", ["--localhost-tld", "false", "--use-redis-cache", "false"]),
        PrDogfoodNewTemplateCase.DotNet("aspire-ts-cs-starter", ["--localhost-tld", "false", "--use-redis-cache", "false"]),
        PrDogfoodNewTemplateCase.DotNet(KnownTemplateId.DotNetEmptyAppHost, ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.DotNet("aspire-apphost", ["--localhost-tld", "false"]),
        PrDogfoodNewTemplateCase.DotNet("aspire-servicedefaults"),
    ];

    private static readonly PrDogfoodNewTemplateExclusion[] s_prDogfoodNewTemplateExclusions =
    [
        new("aspire-test", "This wrapper requires an interactive framework sub-template selection before it reaches package resolution.")
    ];

    public static TheoryData<PrDogfoodNewTemplateCase> PrDogfoodNewTemplateCases()
    {
        var data = new TheoryData<PrDogfoodNewTemplateCase>();
        foreach (var testCase in s_prDogfoodNewTemplateCases)
        {
            data.Add(testCase);
        }

        return data;
    }

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

    [Fact]
    public void PrDogfoodNewTemplateContract_AccountsForEveryRegisteredNewTemplate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var services = CreatePrDogfoodNewTemplateServices(workspace, processPath: null, dotNetRunner: new TestDotNetCliRunner());

        using var serviceProvider = services.BuildServiceProvider();
        var templateProvider = serviceProvider.GetRequiredService<ITemplateProvider>();
        var registeredTemplateKeys = templateProvider.GetTemplates()
            .SelectMany(GetPrDogfoodTemplateCoverageKeys)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var accountedForTemplateKeys = s_prDogfoodNewTemplateCases.Select(static testCase => testCase.CoverageKey)
            .Concat(s_prDogfoodNewTemplateExclusions.Select(static exclusion => exclusion.CoverageKey))
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(accountedForTemplateKeys, registeredTemplateKeys);
        Assert.All(s_prDogfoodNewTemplateExclusions, static exclusion => Assert.False(string.IsNullOrWhiteSpace(exclusion.Reason)));
    }

    /// <summary>
    /// Issue #17225 regression guard: when <c>PackagingService</c> discovers the running
    /// <c>pr-&lt;N&gt;</c> CLI's matching dogfood install hive, every registered <c>aspire new</c>
    /// template that consumes Aspire packages must scaffold from that channel/source.
    /// </summary>
    [Theory]
    [MemberData(nameof(PrDogfoodNewTemplateCases))]
    public async Task NewTemplate_PrDogfoodInstallHiveDiscovered_UsesPrChannel(PrDogfoodNewTemplateCase testCase)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var (processPath, packagesDirectory) = CreatePrDogfoodInstallLayout(workspace);
        var dotNetTemplateInstalls = new List<DotNetTemplateInstall>();
        var dotNetRunner = new TestDotNetCliRunner
        {
            InstallTemplateAsyncCallback = (packageName, version, nugetConfigFile, nugetSource, _, _, _) =>
            {
                dotNetTemplateInstalls.Add(new DotNetTemplateInstall(packageName, version, nugetConfigFile?.FullName, nugetSource));
                return (0, version);
            },
            NewProjectAsyncCallback = (templateName, name, outputPath, _, _) =>
            {
                Directory.CreateDirectory(outputPath);
                File.WriteAllText(Path.Combine(outputPath, $"{name}.generated"), templateName);
                return 0;
            }
        };

        var services = CreatePrDogfoodNewTemplateServices(workspace, processPath, dotNetRunner);

        services.AddSingleton<IAppHostServerProjectFactory>(_ => new TestAppHostServerProjectFactory
        {
            CreateAsyncCallback = (path, _) =>
                Task.FromResult<IAppHostServerProject>(new FakeFailingAppHostServerProject(path))
        });

        using var serviceProvider = services.BuildServiceProvider();
        var newCommand = serviceProvider.GetRequiredService<NewCommand>();

        const string outputDirectoryName = "TemplateOut";
        var commandArguments = new List<string>
        {
            "new",
            testCase.TemplateId,
            "--name",
            "TemplateOut",
            "--output",
            $"./{outputDirectoryName}",
        };
        if (testCase.LanguageId is not null)
        {
            commandArguments.Add("--language");
            commandArguments.Add(testCase.LanguageId);
        }
        commandArguments.AddRange(testCase.ExtraArguments);

        var parseResult = newCommand.Parse(string.Join(" ", commandArguments));
        var exitCode = await parseResult.InvokeAsync().DefaultTimeout();

        var outputDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, outputDirectoryName);
        switch (testCase.Contract)
        {
            case PrDogfoodNewTemplateContract.CSharpEmptyAppHost:
                Assert.Empty(dotNetTemplateInstalls);
                var appHostFile = Path.Combine(outputDirectory, "apphost.cs");
                Assert.True(File.Exists(appHostFile));
                Assert.Contains(s_prVersion, await File.ReadAllTextAsync(appHostFile));

                var csharpNuGetConfig = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "nuget.config"));
                Assert.Contains(packagesDirectory.FullName.Replace('\\', '/'), csharpNuGetConfig);
                break;

            case PrDogfoodNewTemplateContract.AspireConfig:
                Assert.Empty(dotNetTemplateInstalls);
                var config = AspireConfigFile.Load(outputDirectory);
                Assert.NotNull(config);
                Assert.Equal(PrChannelName, config.Channel);
                if (config.SdkVersion is not null)
                {
                    Assert.Equal(s_prVersion, config.SdkVersion);
                }
                break;

            case PrDogfoodNewTemplateContract.DotNetTemplate:
                Assert.Equal((int)CliExitCodes.Success, exitCode);
                var install = Assert.Single(dotNetTemplateInstalls);
                Assert.Equal(TemplateNuGetConfigService.TemplatesPackageName, install.PackageName);
                Assert.Equal(s_prVersion, install.Version);
                Assert.Equal(packagesDirectory.FullName.Replace('\\', '/'), install.NuGetSource);

                var dotNetConfig = AspireConfigFile.Load(outputDirectory);
                Assert.NotNull(dotNetConfig);
                Assert.Equal(PrChannelName, dotNetConfig.Channel);

                var dotNetNuGetConfig = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "nuget.config"));
                Assert.Contains(packagesDirectory.FullName.Replace('\\', '/'), dotNetNuGetConfig);
                break;

            default:
                throw new InvalidOperationException($"Unknown template contract: {testCase.Contract}");
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
        var implicitChannel = PackageChannel.CreateImplicitChannel(implicitCache, new TestFeatures());

        var stableCache = new FakeNuGetPackageCache
        {
            GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                Task.FromResult<IEnumerable<NuGetPackage>>(
                    [new NuGetPackage { Id = "Aspire.ProjectTemplates", Source = "nuget", Version = "13.5.0" }])
        };
        var stableChannel = PackageChannel.CreateExplicitChannel(
            PackageChannelNames.Stable,
            PackageChannelQuality.Stable,
            [new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg)],
            stableCache,
            features: new TestFeatures());

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
                    new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg),
                ],
                cache,
                features: new TestFeatures()));
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

    private IServiceCollection CreatePrDogfoodNewTemplateServices(TemporaryWorkspace workspace, string? processPath, TestDotNetCliRunner dotNetRunner)
    {
        return CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => TestExecutionContextHelper.CreateExecutionContext(
                workspace.WorkspaceRoot,
                hivesDirectory: new DirectoryInfo(Path.Combine(workspace.WorkspaceRoot.FullName, ".aspire", "hives")),
                identityChannel: PrChannelName);
            options.PackagingServiceFactory = sp => new PackagingService(
                sp.GetRequiredService<CliExecutionContext>(),
                sp.GetRequiredService<INuGetPackageCache>(),
                sp.GetRequiredService<IFeatures>(),
                sp.GetRequiredService<IConfiguration>(),
                NullLogger<PackagingService>.Instance,
                processPathProvider: () => processPath);
            options.NuGetPackageCacheFactory = _ => new FakeNuGetPackageCache
            {
                GetTemplatePackagesAsyncCallback = (_, prerelease, _, _) =>
                    Task.FromResult<IEnumerable<NuGetPackage>>(
                    [
                        new NuGetPackage
                        {
                            Id = TemplateNuGetConfigService.TemplatesPackageName,
                            Source = prerelease ? "preview-feed" : "stable-feed",
                            Version = prerelease ? "0.0.1-preview.1" : "0.0.1"
                        }
                    ])
            };
            options.DotNetCliRunnerFactory = _ => dotNetRunner;
            options.EnabledFeatures =
            [
                KnownFeatures.ExperimentalPolyglotPython,
                KnownFeatures.ExperimentalPolyglotGo,
                KnownFeatures.ExperimentalPolyglotJava,
                KnownFeatures.ExperimentalPolyglotRust,
                KnownFeatures.ShowAllTemplates,
            ];
        });
    }

    private static (string ProcessPath, DirectoryInfo PackagesDirectory) CreatePrDogfoodInstallLayout(TemporaryWorkspace workspace)
    {
        var installPrefix = Directory.CreateDirectory(Path.Combine(workspace.WorkspaceRoot.FullName, "custom-aspire-prefix"));
        var processPath = Path.Combine(installPrefix.FullName, "dogfood", PrChannelName, "bin", "aspire");
        Directory.CreateDirectory(Path.GetDirectoryName(processPath)!);
        File.WriteAllText(processPath, string.Empty);

        var packagesDirectory = Directory.CreateDirectory(Path.Combine(installPrefix.FullName, "hives", PrChannelName, "packages"));
        File.WriteAllText(Path.Combine(packagesDirectory.FullName, $"Aspire.ProjectTemplates.{s_prVersion}.nupkg"), string.Empty);

        return (processPath, packagesDirectory);
    }

    private static IEnumerable<string> GetPrDogfoodTemplateCoverageKeys(ITemplate template)
    {
        return template.SelectableAppHostLanguages.Count == 0
            ? [template.Name]
            : template.SelectableAppHostLanguages.Select(languageId => $"{template.Name}:{languageId}");
    }

    public sealed record PrDogfoodNewTemplateCase(
        string TemplateId,
        string? LanguageId,
        PrDogfoodNewTemplateContract Contract,
        string[] ExtraArguments)
    {
        public string CoverageKey => LanguageId is null ? TemplateId : $"{TemplateId}:{LanguageId}";

        public static PrDogfoodNewTemplateCase SelectableEmpty(string languageId, PrDogfoodNewTemplateContract contract, string[] extraArguments)
        {
            return new(KnownTemplateId.CSharpEmptyAppHost, languageId, contract, extraArguments);
        }

        public static PrDogfoodNewTemplateCase CliConfig(string templateId, string[]? extraArguments = null)
        {
            return new(templateId, LanguageId: null, PrDogfoodNewTemplateContract.AspireConfig, extraArguments ?? []);
        }

        public static PrDogfoodNewTemplateCase DotNet(string templateId, string[]? extraArguments = null)
        {
            return new(templateId, LanguageId: null, PrDogfoodNewTemplateContract.DotNetTemplate, extraArguments ?? []);
        }

        public override string ToString()
        {
            return CoverageKey;
        }
    }

    private sealed record PrDogfoodNewTemplateExclusion(string CoverageKey, string Reason);

    private sealed record DotNetTemplateInstall(string PackageName, string Version, string? NuGetConfigFile, string? NuGetSource);

    public enum PrDogfoodNewTemplateContract
    {
        CSharpEmptyAppHost,
        AspireConfig,
        DotNetTemplate
    }
}
