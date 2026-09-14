// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Agents;

public class AgentAssetCatalogProviderTests(ITestOutputHelper outputHelper)
{
    private const string FailureMessage = "The requested assets are unavailable.";

    [Fact]
    public void Registration_RetainsCatalogsAndLocationsWithoutAcquisition()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IAgentAssetCatalogProvider>();
        var catalogs = provider.GetCatalogs().ToArray();
        Assert.Equal(["skills", "extensions"], catalogs.Select(catalog => catalog.Name));
        Assert.Equal(catalogs, provider.GetCatalogs());

        var skills = Assert.IsType<SkillCatalog>(catalogs[0]);
        Assert.Equal(SkillCatalog.KnownLocations, skills.Locations);

        var extensions = Assert.IsType<ExtensionCatalog>(catalogs[1]);
        Assert.Equal(ExtensionCatalog.KnownLocations, extensions.Locations);
        Assert.Empty(Assert.IsType<FakeAspireSkillsInstaller>(serviceProvider.GetRequiredService<IAspireSkillsInstaller>()).RequestedProviders);
    }

    [Fact]
    public async Task Registration_AllowsMultipleCatalogsWithIndependentSources()
    {
        var firstAsset = CreateAsset("first", []);
        var secondAsset = CreateAsset("second", []);
        var firstSource = new FakeAgentAssetSource
        {
            Result = AgentAssetSourceResult.Available([firstAsset])
        };
        var secondSource = new FakeAgentAssetSource
        {
            Result = AgentAssetSourceResult.Available([secondAsset])
        };
        var firstCatalog = new ExtensionCatalog(firstSource);
        var secondCatalog = new ExtensionCatalog(secondSource);
        var provider = new AgentAssetCatalogProvider([firstCatalog, secondCatalog]);

        Assert.Equal([firstCatalog, secondCatalog], provider.GetCatalogs());
        var first = await provider.ResolveAsync(firstCatalog, "all", detectedLanguage: null, TestContext.Current.CancellationToken);
        var second = await provider.ResolveAsync(secondCatalog, "all", detectedLanguage: null, TestContext.Current.CancellationToken);
        Assert.Equal([firstAsset], first.Assets);
        Assert.Equal([secondAsset], second.Assets);
        Assert.Equal(1, firstSource.RequestCount);
        Assert.Equal(1, secondSource.RequestCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Installation_UsesCatalogPolicyAndIsIdempotent(bool extensions)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var root = workspace.CreateDirectory("root");
        var assetDirectory = root.CreateSubdirectory(Path.Combine("assets", "example"));
        var stalePath = Path.Combine(assetDirectory.FullName, "extra.txt");
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(stalePath, "Existing file", cancellationToken);
        var source = CreateUnavailableSource();
        IAgentAssetCatalog catalog = extensions ? new ExtensionCatalog(source) : new SkillCatalog(source);
        var asset = CreateAsset("example", []);
        var target = new AgentAssetInstallTarget(root, "assets", "assets");

        Assert.True(await catalog.InstallAsync(target, asset, cancellationToken));
        Assert.Equal(!extensions, File.Exists(stalePath));
        Assert.Equal("Asset content", await File.ReadAllTextAsync(Path.Combine(assetDirectory.FullName, "content.txt"), cancellationToken));
        Assert.False(await catalog.InstallAsync(target, asset, cancellationToken));
        Assert.Equal(0, source.RequestCount);
    }

    [Theory]
    [InlineData("skills")]
    [InlineData("extensions")]
    public void InstallTargets_AcceptCatalogIndependentLocations(string catalogName)
    {
        var source = CreateUnavailableSource();
        var catalog = CreateProvider(source).GetCatalogs().First(catalog => catalog.Name == catalogName);
        var location = new AgentAssetLocation(
            "custom",
            "Custom",
            "A custom agent asset location.",
            Path.Combine(".custom", "assets"),
            isDefault: false,
            scopes: AgentAssetLocationScope.Workspace | AgentAssetLocationScope.User);
        var workspace = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "workspace"));
        var home = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "home"));

        var targets = catalog.ResolveInstallTargets(location, workspace, home, new TestEnvironment());

        Assert.Equal(
            [
                (Path.Combine(workspace.FullName, ".custom", "assets"), ".custom/assets"),
                (Path.Combine(home.FullName, ".custom", "assets"), "~/.custom/assets")
            ],
            targets.Select(target =>
                (Path.Combine(target.RootDirectory.FullName, target.RelativeAssetDirectory), target.DisplayDirectory)));
        Assert.Equal(0, source.RequestCount);
    }

    [Theory]
    [InlineData("skills", "standard", ".agents/skills", true, true)]
    [InlineData("skills", "claudecode", ".claude/skills", true, false)]
    [InlineData("skills", "github", ".github/skills", true, false)]
    [InlineData("skills", "opencode", ".opencode/skill", true, false)]
    [InlineData("extensions", "project", ".github/extensions", true, false)]
    [InlineData("extensions", "user", ".copilot/extensions", false, true)]
    public void InstallTargets_ResolveOnlySelectedLocationsAndSupportedScopes(
        string catalogName, string locationId, string displayDirectory, bool workspaceScope, bool userScope)
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);
        var catalog = provider.GetCatalogs().First(catalog => catalog.Name == catalogName);
        var location = Assert.Single(catalog.Locations, location => location.Id == locationId);
        var workspace = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "workspace"));
        var home = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "home"));
        // A malformed unused override must not affect skills or project-only extensions.
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            ["COPILOT_HOME"] = location == ExtensionCatalog.UserExtensions ? null : "\0"
        });
        var targets = catalog.ResolveInstallTargets(location, workspace, home, environment);
        var relativeDirectory = displayDirectory.Replace('/', Path.DirectorySeparatorChar);
        var expected = new List<(string Path, string Display)>();
        if (workspaceScope)
        {
            expected.Add((Path.Combine(workspace.FullName, relativeDirectory), displayDirectory));
        }
        if (userScope)
        {
            expected.Add((Path.Combine(home.FullName, relativeDirectory), $"~/{displayDirectory}"));
        }

        Assert.Equal(expected, targets.Select(target =>
            (Path.Combine(target.RootDirectory.FullName, target.RelativeAssetDirectory), target.DisplayDirectory)));
        Assert.Equal(0, source.RequestCount);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".copilot")]
    [InlineData("custom-copilot")]
    public void InstallTargets_UserExtensionsHonorCopilotHome(string? directoryName)
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);
        var workspace = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "workspace"));
        var home = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "home"));
        var configuredHome = string.IsNullOrEmpty(directoryName) ? directoryName : Path.Combine(home.FullName, directoryName);
        var environment = new TestEnvironment(new Dictionary<string, string?> { ["COPILOT_HOME"] = configuredHome });

        var target = Assert.Single(provider.GetCatalogs().First(catalog => catalog.Name == "extensions").ResolveInstallTargets(
            ExtensionCatalog.UserExtensions, workspace, home, environment));

        var expectedDirectory = string.IsNullOrEmpty(configuredHome) ? Path.Combine(home.FullName, ".copilot") : configuredHome;
        var expectedDisplay = string.IsNullOrEmpty(directoryName) || directoryName == ".copilot"
            ? "~/.copilot/extensions"
            : Path.Combine(expectedDirectory, "extensions");
        Assert.Equal(
            (expectedDirectory, "extensions", expectedDisplay),
            (target.RootDirectory.FullName, target.RelativeAssetDirectory, target.DisplayDirectory));
        Assert.Equal(0, source.RequestCount);
    }

    [Fact]
    public void InstallTargets_UserExtensionsResolveCurrentEnvironmentOnEnumeration()
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);
        var workspace = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "workspace"));
        var home = new DirectoryInfo(Path.Combine(AppContext.BaseDirectory, "home"));
        var configuredHome = Path.Combine(home.FullName, "custom-copilot");
        var variables = new Dictionary<string, string?> { ["COPILOT_HOME"] = "\0" };
        var environment = new TestEnvironment(variables);

        var targets = provider.GetCatalogs().First(catalog => catalog.Name == "extensions").ResolveInstallTargets(
            ExtensionCatalog.UserExtensions, workspace, home, environment);
        variables["COPILOT_HOME"] = configuredHome;

        var target = Assert.Single(targets);
        Assert.Equal(
            (configuredHome, "extensions", Path.Combine(configuredHome, "extensions")),
            (target.RootDirectory.FullName, target.RelativeAssetDirectory, target.DisplayDirectory));

        variables.Remove("COPILOT_HOME");

        var defaultTarget = Assert.Single(targets);
        Assert.Equal(
            (Path.Combine(home.FullName, ".copilot"), "extensions", "~/.copilot/extensions"),
            (defaultTarget.RootDirectory.FullName, defaultTarget.RelativeAssetDirectory, defaultTarget.DisplayDirectory));
        Assert.Equal(0, source.RequestCount);
    }

    [Theory]
    [InlineData(null, "playwright-cli,zeta", "zeta")]
    [InlineData(KnownLanguageId.CSharp, "alpha,dotnet-inspect,playwright-cli,zeta", "alpha,zeta")]
    [InlineData(KnownLanguageId.TypeScript, "playwright-cli,zeta", "zeta")]
    public async Task Skills_MergeWithCliPrecedenceThenFilterAndSort(
        string? language, string expectedNames, string expectedDefaults)
    {
        var source = new FakeAgentAssetSource
        {
            Result = AgentAssetSourceResult.Available(
            [
                CreateAsset("zeta", []),
                CreateAsset("PLAYWRIGHT-CLI", []),
                CreateAsset("alpha", [KnownLanguageId.CSharp]),
                CreateAsset("DOTNET-INSPECT", [])
            ])
        };
        var provider = CreateProvider(source);

        var result = await provider.ResolveAsync(
            new SkillCatalog(source), "all", language is null ? default(LanguageId?) : new LanguageId(language), TestContext.Current.CancellationToken);

        Assert.False(result.IsFailure);
        Assert.Null(result.DiagnosticMessage);
        Assert.Equal(expectedNames.Split(','), result.Assets.Select(asset => asset.Name));
        Assert.Equal(expectedDefaults.Split(','), result.Assets.Where(asset => asset.IsDefault).Select(asset => asset.Name));
        Assert.Same(SkillCatalog.PlaywrightCli, Assert.Single(result.Assets, asset => asset.HasName("playwright-cli")));
        Assert.Equal(1, source.RequestCount);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("NONE")]
    [InlineData("playwright-cli")]
    [InlineData("DOTNET-INSPECT")]
    [InlineData("playwright-cli, , dotnet-inspect")]
    public async Task Skills_CliOnlySelectionDoesNotAcquireFromSource(string requestedAssets)
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);

        var result = await provider.ResolveAsync(
            new SkillCatalog(source), requestedAssets, new LanguageId(KnownLanguageId.CSharp), TestContext.Current.CancellationToken);

        Assert.False(result.IsFailure);
        Assert.Null(result.DiagnosticMessage);
        Assert.Equal([SkillCatalog.DotnetInspect, SkillCatalog.PlaywrightCli], result.Assets);
        Assert.Equal(0, source.RequestCount);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("all", false)]
    [InlineData("ALL", false)]
    [InlineData(" , ", false)]
    [InlineData("bundle-only", true)]
    [InlineData("playwright-cli,bundle-only", true)]
    public async Task Skills_UnavailableSourceFallsBackAndOnlyDiagnosesExplicitSourceNames(
        string? requestedAssets, bool expectDiagnostic)
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);

        var result = await provider.ResolveAsync(
            new SkillCatalog(source), requestedAssets, detectedLanguage: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsFailure);
        Assert.Equal(expectDiagnostic ? FailureMessage : null, result.DiagnosticMessage);
        Assert.Equal([SkillCatalog.PlaywrightCli], result.Assets);
        Assert.Equal(1, source.RequestCount);
    }

    [Fact]
    public async Task Extensions_UnavailableSourceFailsWithoutCliFallback()
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);

        var result = await provider.ResolveAsync(
            new ExtensionCatalog(source), requestedAssets: null, detectedLanguage: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Empty(result.Assets);
        Assert.Equal(FailureMessage, result.DiagnosticMessage);
        Assert.Equal(1, source.RequestCount);
    }

    [Fact]
    public async Task Extensions_ExplicitOptOutDoesNotAcquireFromSource()
    {
        var source = CreateUnavailableSource();

        var result = await CreateProvider(source).ResolveAsync(
            new ExtensionCatalog(source), "none", detectedLanguage: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsFailure);
        Assert.Null(result.DiagnosticMessage);
        Assert.Empty(result.Assets);
        Assert.Equal(0, source.RequestCount);
    }

    [Fact]
    public async Task Resolution_UsesEachRequestsCatalogLanguageAndSelection()
    {
        var skill = CreateAsset("bundled-skill", []);
        var alpha = CreateAsset("alpha", []);
        var zeta = CreateAsset("zeta", []);
        var restricted = CreateAsset("restricted", [KnownLanguageId.CSharp]);
        var skillSource = new FakeAgentAssetSource
        {
            Result = AgentAssetSourceResult.Available([skill])
        };
        var extensionSource = new FakeAgentAssetSource
        {
            Result = AgentAssetSourceResult.Available([zeta, restricted, alpha])
        };
        var skillCatalog = new SkillCatalog(skillSource);
        var extensionCatalog = new ExtensionCatalog(extensionSource);
        var provider = new AgentAssetCatalogProvider([skillCatalog, extensionCatalog]);

        var skills = await provider.ResolveAsync(
            skillCatalog, "all", new LanguageId(KnownLanguageId.CSharp), TestContext.Current.CancellationToken);
        var cliOnly = await provider.ResolveAsync(
            skillCatalog, "playwright-cli", detectedLanguage: null, TestContext.Current.CancellationToken);
        var extensions = await provider.ResolveAsync(
            extensionCatalog, "all", detectedLanguage: null, TestContext.Current.CancellationToken);

        Assert.Equal([skill, SkillCatalog.DotnetInspect, SkillCatalog.PlaywrightCli], skills.Assets);
        Assert.Equal([SkillCatalog.PlaywrightCli], cliOnly.Assets);
        Assert.Equal([alpha, zeta], extensions.Assets);
        Assert.Equal(1, skillSource.RequestCount);
        Assert.Equal(1, extensionSource.RequestCount);
    }

    [Fact]
    public async Task Resolution_CancellationDoesNotStartAcquisition()
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ResolveAsync(
            new SkillCatalog(source), "all", detectedLanguage: null, cancellation.Token));
        Assert.Equal(0, source.RequestCount);
    }

    [Fact]
    public void SourceResult_CopiesResolvedAssets()
    {
        var asset = CreateAsset("original", []);
        AgentAssetDefinition[] assets = [asset];
        var result = AgentAssetSourceResult.Available(assets);
        assets[0] = CreateAsset("replacement", []);

        Assert.True(result.IsAvailable);
        Assert.Null(result.Message);
        Assert.Same(asset, Assert.Single(result.Assets));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void SourceResult_RequiresDiagnosticWhenUnavailable(string message)
    {
        Assert.Throws<ArgumentException>(() => AgentAssetSourceResult.Unavailable(message));
    }

    private static AgentAssetCatalogProvider CreateProvider(IAgentAssetSource source)
        => new([new SkillCatalog(source), new ExtensionCatalog(source)]);

    private static FakeAgentAssetSource CreateUnavailableSource()
        => new()
        {
            Result = AgentAssetSourceResult.Unavailable(FailureMessage)
        };

    private static AgentAssetDefinition CreateAsset(string name, IReadOnlyList<string> languages)
        => new(
            name, $"{name} description",
            [new AgentAssetFile("content.txt", "Asset content")],
            installExcludedRelativePaths: [], isDefault: true, applicableLanguages: languages);
}
