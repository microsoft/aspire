// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Scaffolding;

/// <summary>
/// Scaffolding an AppHost records the language's default hosting packages in <c>aspire.config.json</c>
/// and prepares the AppHost server with them, so their resource APIs are generated without an
/// <c>aspire add</c> step.
/// </summary>
public class DefaultHostingPackagesTests(ITestOutputHelper outputHelper)
{
    private const string RequestedSdkVersion = "13.4.0-test";
    private const string IdentitySdkVersion = "13.9.0-identity";

    private FakeSucceedingAppHostServerProject? _serverProject;

    [Theory]
    [InlineData(KnownLanguageId.TypeScript, "Aspire.Hosting.JavaScript")]
    [InlineData(KnownLanguageId.Python, "Aspire.Hosting.Python")]
    [InlineData(KnownLanguageId.Go, "Aspire.Hosting.Go")]
    public async Task ScaffoldAsync_RecordsAndRestoresTheLanguageDefaultHostingPackage(string languageId, string expectedPackage)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var language = GetLanguage(languageId);

        var result = await ScaffoldAsync(workspace, language, RequestedSdkVersion);

        Assert.True(result);
        var package = Assert.Single(LoadPackages(workspace));
        Assert.Equal(expectedPackage, package.Key);
        Assert.Equal(RequestedSdkVersion, package.Value.Version);
        var restored = _serverProject!.Integrations.Select(integration => (integration.Name, integration.Version)).ToList();
        Assert.Contains((expectedPackage, RequestedSdkVersion), restored);
        Assert.Contains((language.PackageName, RequestedSdkVersion), restored);
    }

    [Fact]
    public async Task ScaffoldAsync_UsesTheCliSdkVersionWhenNoneIsRequested()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var result = await ScaffoldAsync(workspace, GetLanguage(KnownLanguageId.TypeScript), sdkVersion: null);

        Assert.True(result);
        var package = Assert.Single(LoadPackages(workspace));
        Assert.Equal("Aspire.Hosting.JavaScript", package.Key);
        Assert.Equal(IdentitySdkVersion, package.Value.Version);
    }

    [Fact]
    public async Task ScaffoldAsync_RecordsTheJavaScriptPackageForABrownfieldTypeScriptAppHost()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "package.json"),
            """{ "name": "existing-app" }""",
            TestContext.Current.CancellationToken);

        var result = await ScaffoldAsync(workspace, GetLanguage(KnownLanguageId.TypeScript), RequestedSdkVersion);

        Assert.True(result);
        var package = Assert.Single(LoadPackages(workspace));
        Assert.Equal("Aspire.Hosting.JavaScript", package.Key);
        Assert.Equal(RequestedSdkVersion, package.Value.Version);
    }

    [Fact]
    public async Task ScaffoldAsync_KeepsAnExistingEntryForADefaultHostingPackage()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"),
            """{ "packages": { "Aspire.Hosting.JavaScript": "9.9.9" } }""",
            TestContext.Current.CancellationToken);

        var result = await ScaffoldAsync(workspace, GetLanguage(KnownLanguageId.TypeScript), RequestedSdkVersion);

        Assert.True(result);
        var package = Assert.Single(LoadPackages(workspace));
        Assert.Equal("Aspire.Hosting.JavaScript", package.Key);
        Assert.Equal("9.9.9", package.Value.Version);
    }

    [Theory]
    [InlineData(KnownLanguageId.TypeScript, "aspire.hosting.javascript")]
    [InlineData(KnownLanguageId.Python, "aspire.hosting.python")]
    [InlineData(KnownLanguageId.Go, "aspire.hosting.go")]
    public async Task ScaffoldAsync_KeepsAnExistingEntryForADefaultHostingPackageWithADifferentCase(string languageId, string existingPackageName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(
            Path.Combine(workspace.WorkspaceRoot.FullName, "aspire.config.json"),
            $$"""{ "packages": { "{{existingPackageName}}": "9.9.9" } }""",
            TestContext.Current.CancellationToken);

        var result = await ScaffoldAsync(workspace, GetLanguage(languageId), RequestedSdkVersion);

        Assert.True(result);
        var package = Assert.Single(LoadPackages(workspace));
        Assert.Equal(existingPackageName, package.Key);
        Assert.Equal("9.9.9", package.Value.Version);
    }

    [Fact]
    public async Task ScaffoldAsync_RecordsNoPackagesForALanguageWithoutDefaultHostingPackages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var language = GetLanguage(KnownLanguageId.TypeScript) with { DefaultHostingPackages = [] };

        var result = await ScaffoldAsync(workspace, language, RequestedSdkVersion);

        Assert.True(result);
        Assert.Empty(LoadPackages(workspace));
        Assert.Collection(
            _serverProject!.Integrations,
            integration => Assert.Equal("Aspire.Hosting", integration.Name),
            integration => Assert.Equal(language.PackageName, integration.Name));
    }

    [Theory]
    [InlineData("ts-starter", KnownLanguageId.TypeScript)]
    [InlineData("py-starter", KnownLanguageId.TypeScript)] // The Python starter's AppHost is a TypeScript AppHost.
    [InlineData("go-starter", KnownLanguageId.Go)]
    public void StarterTemplate_DeclaresTheDefaultHostingPackagesOfItsAppHostLanguage(string template, string appHostLanguageId)
    {
        var language = GetLanguage(appHostLanguageId);
        var configPath = Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Templating", "Templates", template, "aspire.config.json");
        Assert.True(File.Exists(configPath), $"Expected the {template} aspire.config.json at {configPath}");

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        var declaredPackages = document.RootElement
            .GetProperty("packages")
            .EnumerateObject()
            .Select(package => package.Name)
            .ToArray();

        Assert.NotEmpty(language.DefaultHostingPackages);
        Assert.All(language.DefaultHostingPackages, package => Assert.Contains(package, declaredPackages));
    }

    [Fact]
    public void DefaultHostingPackages_AreStablePackagesBuiltByThisRepository()
    {
        var sourceDirectory = Path.Combine(GetRepoRoot(), "src");

        Assert.All(
            DefaultLanguageDiscovery.AllLanguages.SelectMany(language => language.DefaultHostingPackages),
            package =>
            {
                var projectPath = Path.Combine(sourceDirectory, package, $"{package}.csproj");
                Assert.True(File.Exists(projectPath), $"Expected {package} to be built from src/{package}.");

                // A preview-only package has no version that satisfies the stable CLI version the scaffolder pins.
                // MSBuild does not see this read, so each default package project is also listed in the
                // path_rules of eng/github-ci/test-trigger-map.yml to select this test project.
                Assert.False(
                    File.ReadAllText(projectPath).Contains("SuppressFinalPackageVersion", StringComparison.Ordinal),
                    $"{package} sets SuppressFinalPackageVersion, so it only ships as a preview and cannot be a default hosting package.");
            });
    }

    private static LanguageInfo GetLanguage(string languageId)
        => DefaultLanguageDiscovery.AllLanguages.Single(language => language.LanguageId.Value == languageId);

    private static Dictionary<string, PackageEntry> LoadPackages(TemporaryWorkspace workspace)
    {
        var config = AspireConfigFile.Load(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(config);

        return config.Packages ?? [];
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private async Task<bool> ScaffoldAsync(TemporaryWorkspace workspace, LanguageInfo language, string? sdkVersion)
    {
        var scaffoldingService = new ScaffoldingService(
            appHostServerProjectFactory: new TestAppHostServerProjectFactory
            {
                CreateAsyncCallback = (path, _) =>
                {
                    _serverProject = new FakeSucceedingAppHostServerProject(path);
                    return Task.FromResult<IAppHostServerProject>(_serverProject);
                }
            },
            serverSessionFactory: FakeAppHostServerSessionFactory.CreateForScaffolding(),
            languageDiscovery: new TestLanguageDiscovery(language),
            interactionService: new TestInteractionService(),
            environment: new TestEnvironment(),
            logger: NullLogger<ScaffoldingService>.Instance,
            executionContext: workspace.CreateExecutionContext(identityVersion: IdentitySdkVersion),
            profilingTelemetry: new ProfilingTelemetry(new ConfigurationBuilder().Build()),
            features: new TestFeatures(),
            processExecutionFactory: new TestProcessExecutionFactory());

        var context = new ScaffoldContext(
            Language: language,
            TargetDirectory: workspace.WorkspaceRoot,
            ProjectName: "test",
            SdkVersion: sdkVersion);

        return await scaffoldingService.ScaffoldAsync(context, TestContext.Current.CancellationToken);
    }
}
