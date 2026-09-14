// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Tests.Agents;

public class AgentAssetCatalogProviderTests(ITestOutputHelper outputHelper)
{
    private const string FailureMessage = "The requested assets are unavailable.";

    [Fact]
    public void Registration_AssociatesEachCatalogWithItsLocationsAndPolicy()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var source = CreateUnavailableSource();
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.Replace(ServiceDescriptor.Singleton<IAgentAssetSource>(source));
        using var serviceProvider = services.BuildServiceProvider();
        var provider = serviceProvider.GetRequiredService<IAgentAssetCatalogProvider>();

        var skills = provider.GetCatalog(AgentAssetKind.Skill);
        Assert.Same(skills, provider.GetCatalog(AgentAssetKind.Skill));
        Assert.IsType<SkillCatalog>(skills);
        Assert.Equal(AgentAssetKind.Skill, skills.AssetKind);
        Assert.Equal(SkillCatalog.KnownLocations, skills.Locations);
        Assert.Same(AgentAssetFileInstaller.Additive, skills.FileInstaller);

        var extensions = provider.GetCatalog(AgentAssetKind.Extension);
        Assert.Same(extensions, provider.GetCatalog(AgentAssetKind.Extension));
        Assert.IsType<ExtensionCatalog>(extensions);
        Assert.Equal(AgentAssetKind.Extension, extensions.AssetKind);
        Assert.Equal(ExtensionCatalog.KnownLocations, extensions.Locations);
        Assert.Same(AgentAssetFileInstaller.ManagedDirectory, extensions.FileInstaller);
        Assert.Empty(source.RequestedKinds);
    }

    [Fact]
    public void CatalogContractsExposeOnlyNeutralTypes()
    {
        foreach (var catalogType in new[] { typeof(SkillCatalog), typeof(ExtensionCatalog) })
        {
            Assert.Equal(
                [typeof(IAgentAssetSource)],
                Assert.Single(catalogType.GetConstructors()).GetParameters().Select(parameter => parameter.ParameterType));
        }

        Assert.Equal(
            [typeof(AgentAssetKind), typeof(AgentAssetFileInstaller), typeof(IReadOnlyList<AgentAssetLocation>)],
            typeof(IAgentAssetCatalog).GetProperties().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => property.PropertyType));
    }

    [Fact]
    public void Registration_RejectsDuplicateKinds()
    {
        var catalog = new SkillCatalog(CreateUnavailableSource());

        var exception = Assert.Throws<InvalidOperationException>(() => new AgentAssetCatalogProvider([catalog, catalog]));

        Assert.Equal("Multiple agent asset catalogs are registered for asset kind 'Skill'.", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task Registration_RejectsUnknownKindsWithoutAcquisition(int kind)
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);
        var unknownKind = (AgentAssetKind)kind;
        Assert.Throws<InvalidOperationException>(() => provider.GetCatalog(unknownKind));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResolveAsync(
            unknownKind, requestedAssets: null, detectedLanguage: null, TestContext.Current.CancellationToken));
        Assert.Empty(source.RequestedKinds);
    }

    [Theory]
    [InlineData(nameof(AgentAssetKind.Skill))]
    [InlineData(nameof(AgentAssetKind.Extension))]
    public void InstallTargets_AcceptKindIndependentLocations(string kindName)
    {
        var assetKind = Enum.Parse<AgentAssetKind>(kindName);
        var source = CreateUnavailableSource();
        var catalog = CreateProvider(source).GetCatalog(assetKind);
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

        Assert.Equal(assetKind, catalog.AssetKind);
        Assert.Equal(
            [
                (Path.Combine(workspace.FullName, ".custom", "assets"), ".custom/assets"),
                (Path.Combine(home.FullName, ".custom", "assets"), "~/.custom/assets")
            ],
            targets.Select(target =>
                (Path.Combine(target.RootDirectory.FullName, target.RelativeAssetDirectory), target.DisplayDirectory)));
        Assert.Empty(source.RequestedKinds);
    }

    [Theory]
    [InlineData(nameof(AgentAssetKind.Skill), "standard", ".agents/skills", true, true)]
    [InlineData(nameof(AgentAssetKind.Skill), "claudecode", ".claude/skills", true, false)]
    [InlineData(nameof(AgentAssetKind.Skill), "github", ".github/skills", true, false)]
    [InlineData(nameof(AgentAssetKind.Skill), "opencode", ".opencode/skill", true, false)]
    [InlineData(nameof(AgentAssetKind.Extension), "project", ".github/extensions", true, false)]
    [InlineData(nameof(AgentAssetKind.Extension), "user", ".copilot/extensions", false, true)]
    public void InstallTargets_ResolveOnlySelectedLocationsAndSupportedScopes(
        string kindName, string locationId, string displayDirectory, bool workspaceScope, bool userScope)
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);
        var assetKind = Enum.Parse<AgentAssetKind>(kindName);
        var catalog = provider.GetCatalog(assetKind);
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
        Assert.Empty(source.RequestedKinds);
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

        var target = Assert.Single(provider.GetCatalog(AgentAssetKind.Extension).ResolveInstallTargets(
            ExtensionCatalog.UserExtensions, workspace, home, environment));

        var expectedDirectory = string.IsNullOrEmpty(configuredHome) ? Path.Combine(home.FullName, ".copilot") : configuredHome;
        var expectedDisplay = string.IsNullOrEmpty(directoryName) || directoryName == ".copilot"
            ? "~/.copilot/extensions"
            : Path.Combine(expectedDirectory, "extensions");
        Assert.Equal(
            (expectedDirectory, "extensions", expectedDisplay),
            (target.RootDirectory.FullName, target.RelativeAssetDirectory, target.DisplayDirectory));
        Assert.Empty(source.RequestedKinds);
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

        var targets = provider.GetCatalog(AgentAssetKind.Extension).ResolveInstallTargets(
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
        Assert.Empty(source.RequestedKinds);
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
            Skills = AgentAssetSourceResult.Available(
            [
                CreateAsset("zeta", []),
                CreateAsset("PLAYWRIGHT-CLI", []),
                CreateAsset("alpha", [KnownLanguageId.CSharp]),
                CreateAsset("DOTNET-INSPECT", [])
            ])
        };
        var provider = CreateProvider(source);

        var result = await provider.ResolveAsync(
            AgentAssetKind.Skill, "all", language is null ? default(LanguageId?) : new LanguageId(language), TestContext.Current.CancellationToken);

        Assert.False(result.IsFailure);
        Assert.Null(result.DiagnosticMessage);
        Assert.Equal(expectedNames.Split(','), result.Assets.Select(asset => asset.Name));
        Assert.Equal(expectedDefaults.Split(','), result.Assets.Where(asset => asset.IsDefault).Select(asset => asset.Name));
        Assert.Same(SkillCatalog.PlaywrightCli, Assert.Single(result.Assets, asset => asset.HasName("playwright-cli")));
        Assert.Equal([AgentAssetKind.Skill], source.RequestedKinds);
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
            AgentAssetKind.Skill, requestedAssets, new LanguageId(KnownLanguageId.CSharp), TestContext.Current.CancellationToken);

        Assert.False(result.IsFailure);
        Assert.Null(result.DiagnosticMessage);
        Assert.Equal([SkillCatalog.DotnetInspect, SkillCatalog.PlaywrightCli], result.Assets);
        Assert.Empty(source.RequestedKinds);
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
            AgentAssetKind.Skill, requestedAssets, detectedLanguage: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsFailure);
        Assert.Equal(expectDiagnostic ? FailureMessage : null, result.DiagnosticMessage);
        Assert.Equal([SkillCatalog.PlaywrightCli], result.Assets);
        Assert.Equal([AgentAssetKind.Skill], source.RequestedKinds);
    }

    [Fact]
    public async Task Extensions_UnavailableSourceFailsWithoutCliFallback()
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);

        var result = await provider.ResolveAsync(
            AgentAssetKind.Extension, requestedAssets: null, detectedLanguage: null, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Empty(result.Assets);
        Assert.Equal(FailureMessage, result.DiagnosticMessage);
        Assert.Equal([AgentAssetKind.Extension], source.RequestedKinds);
    }

    [Fact]
    public async Task Extensions_ExplicitOptOutDoesNotAcquireFromSource()
    {
        var source = CreateUnavailableSource();

        var result = await CreateProvider(source).ResolveAsync(
            AgentAssetKind.Extension, "none", detectedLanguage: null, TestContext.Current.CancellationToken);

        Assert.False(result.IsFailure);
        Assert.Null(result.DiagnosticMessage);
        Assert.Empty(result.Assets);
        Assert.Empty(source.RequestedKinds);
    }

    [Fact]
    public async Task Resolution_UsesEachRequestsKindLanguageAndSelection()
    {
        var skill = CreateAsset("bundled-skill", []);
        var alpha = CreateAsset("alpha", []);
        var zeta = CreateAsset("zeta", []);
        var restricted = CreateAsset("restricted", [KnownLanguageId.CSharp]);
        var source = new FakeAgentAssetSource
        {
            Skills = AgentAssetSourceResult.Available([skill]),
            Extensions = AgentAssetSourceResult.Available([zeta, restricted, alpha])
        };
        var provider = CreateProvider(source);

        var skills = await provider.ResolveAsync(
            AgentAssetKind.Skill, "all", new LanguageId(KnownLanguageId.CSharp), TestContext.Current.CancellationToken);
        var cliOnly = await provider.ResolveAsync(
            AgentAssetKind.Skill, "playwright-cli", detectedLanguage: null, TestContext.Current.CancellationToken);
        var extensions = await provider.ResolveAsync(
            AgentAssetKind.Extension, "all", detectedLanguage: null, TestContext.Current.CancellationToken);

        Assert.Equal([skill, SkillCatalog.DotnetInspect, SkillCatalog.PlaywrightCli], skills.Assets);
        Assert.Equal([SkillCatalog.PlaywrightCli], cliOnly.Assets);
        Assert.Equal([alpha, zeta], extensions.Assets);
        Assert.Equal([AgentAssetKind.Skill, AgentAssetKind.Extension], source.RequestedKinds);
    }

    [Fact]
    public async Task Resolution_CancellationDoesNotStartAcquisition()
    {
        var source = CreateUnavailableSource();
        var provider = CreateProvider(source);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ResolveAsync(
            AgentAssetKind.Skill, "all", detectedLanguage: null, cancellation.Token));
        Assert.Empty(source.RequestedKinds);
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
            Skills = AgentAssetSourceResult.Unavailable(FailureMessage),
            Extensions = AgentAssetSourceResult.Unavailable(FailureMessage)
        };

    private static AgentAssetDefinition CreateAsset(string name, IReadOnlyList<string> languages)
        => new(
            name, $"{name} description",
            [new AgentAssetFile("content.txt", "Asset content")],
            installExcludedRelativePaths: [], isDefault: true, applicableLanguages: languages);
}
