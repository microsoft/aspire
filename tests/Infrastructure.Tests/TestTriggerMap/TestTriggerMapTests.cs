// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.SelectTests;
using Aspire.TestUtilities;
using System.Runtime.CompilerServices;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Exercises curated trigger-map routing through the production selector with repository inputs.
/// </summary>
[Collection("GraphAffectedProjects")] // MSBuildLocator registers process-wide; keep graph use serialized.
public sealed class TestTriggerMapTests
{
    [Fact]
    public void IndependentNativeWorkflowSelectsInfrastructureWithoutExtraJobs()
    {
        var result = SelectWithRealMap(".github/workflows/native-hosting-validation.yml");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Equal(["Infrastructure.Tests"], result.TestProjects);
        Assert.Empty(result.Jobs);
    }

    [Theory]
    [InlineData("tests/Aspire.Dashboard.Playwright.Tests/Integration/Playwright/Pages/ResourcesTests.cs", "Aspire.Dashboard.Playwright.Tests")]
    [InlineData("tests/Aspire.EndToEnd.Tests/IntegrationTests.cs", "Aspire.EndToEnd.Tests")]
    public void OuterloopTestSourcesSelectTheirOwnProjectWithoutExtraJobs(string path, string project)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Equal([project], result.TestProjects);
        Assert.Empty(result.Jobs);
    }

    [Fact]
    public void AppHostServerV2SelectsItsBundleConsumers()
    {
        var result = SelectWithRealMap("src/Aspire.Hosting.Native.Server/Program.cs", "Aspire.Hosting.Native.Server");

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
        Assert.Contains("job:cli-starter-validation", result.Jobs);
        Assert.Contains("job:extension-e2e", result.Jobs);
        Assert.Contains("job:winget-installer", result.Jobs);
        Assert.Contains("job:homebrew-installer", result.Jobs);
    }

    [Theory]
    [InlineData("eng/scripts/generate-template-cgmanifest.ps1")]
    public void TemplateManifestInputsSelectInfrastructureCoverage(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Equal(["Infrastructure.Tests"], result.TestProjects);
        Assert.Empty(result.Jobs);
    }

    [Fact]
    public void TemplateManifestBuildTargetSelectsInfrastructureCoverage()
    {
        var result = SelectWithRealMap("src/Aspire.ProjectTemplates/Aspire.ProjectTemplates.csproj");

        Assert.False(result.SelectsAll);
        Assert.Contains("Infrastructure.Tests", result.TestProjects);
    }

    [Theory]
    [InlineData("src/Aspire.Dashboard/Aspire.Dashboard.csproj")]
    [InlineData("src/Aspire.Cli/Resources/NewResource.resx")]
    [InlineData("src/Aspire.Hosting.Testing/Properties/NewResource.resx")]
    public void ResourceMetadataInputsSelectInfrastructureTests(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Contains("Infrastructure.Tests", result.TestProjects);
    }

    [Theory]
    [InlineData("src/Aspire.Dashboard/Resources/NewResource.Designer.cs")]
    [InlineData("src/Aspire.Dashboard/Resources/xlf/NewResource.fr.xlf")]
    [InlineData("src/Aspire.Dashboard/Components/NewComponent.razor")]
    public void InputsNotReadByResourceMetadataTestsKeepExistingSelection(string path)
    {
        var result = SelectWithRealMap(path, "Aspire.Dashboard");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Equal(["Aspire.Templates.Tests"], result.TestProjects);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void GoHostingChangesSelectExtensionE2e(bool hasAffectedProject)
    {
        const string path = "src/Aspire.Hosting.Go/GoHostingExtensions.cs";
        var result = hasAffectedProject
            ? SelectWithRealMap(path, "Aspire.Hosting.Go")
            : SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Contains("job:extension-e2e", result.Jobs);
    }

    // Repo-relative '/'-separated project paths listed in Aspire.slnx (the Layer 1 graph root).
    private static IReadOnlySet<string> LoadSolutionProjectPaths()
    {
        var slnx = File.ReadAllText(Path.Combine(RepoRoot.Path, "Aspire.slnx"));
        // <Project Path="src/Foo/Foo.csproj" /> — paths use '/' in the slnx already.
        return System.Text.RegularExpressions.Regex.Matches(slnx, "Path=\"([^\"]+\\.csproj)\"")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    public static TheoryData<string, string[]> AuditedLoosePathCases => new()
    {
        {
            "eng/github-ci/resolve-comparison-base.sh",
            ["test:Infrastructure.Tests"]
        },
        {
            "eng/scripts/pack-cli-npm-package.ps1",
            ["test:Aspire.Cli.Tests", "test:Infrastructure.Tests"]
        },
        {
            "eng/scripts/pack-cli-npm-package.pointer.README.md",
            ["test:Infrastructure.Tests"]
        },
        {
            "tools/CreateFailingTestIssue/Program.cs",
            ["test:Infrastructure.Tests"]
        },
        {
            "tools/CreateFailingTestIssue/CreateFailingTestIssue.csproj",
            ["test:Infrastructure.Tests"]
        },
        {
            ".github/workflows/prepare-installer-artifacts.yml",
            ["test:Infrastructure.Tests", "job:winget-installer", "job:homebrew-installer"]
        },
        {
            ".github/actionlint.yaml",
            ["test:Infrastructure.Tests"]
        },
        {
            ".github/actionlint-version.json",
            ["test:Infrastructure.Tests"]
        },
        {
            ".github/scripts/assert-extension-e2e-bridge-vsix.ps1",
            ["job:extension-e2e"]
        },
        {
            ".github/workflows/extension-unit-tests.yml",
            ["test:Infrastructure.Tests", "job:extension-unit"]
        },
        {
            ".github/workflows/extension-e2e-tests.yml",
            ["test:Infrastructure.Tests", "job:extension-unit", "job:extension-e2e"]
        },
        {
            ".vscode/launch.json",
            ["job:extension-unit"]
        },
        {
            ".vscode/tasks.json",
            ["job:extension-unit"]
        },
        {
            "eng/test-retry-patterns.json",
            ["test:Infrastructure.Tests"]
        },
        {
            "eng/scripts/aspire-skills-bundle.common.ps1",
            ["test:Infrastructure.Tests"]
        },
        {
            "eng/scripts/smoke-installed-cli.ps1",
            ["job:winget-installer"]
        },
        {
            "eng/scripts/smoke-installed-cli.sh",
            ["job:homebrew-installer"]
        },
        {
            // These paths do not exist intentionally: they prove new packaging inputs stay covered
            // without requiring this test to enumerate every current RID-specific project.
            "eng/dashboardpack/future-package-input.targets",
            [
                "test:Aspire.Hosting.Sdk.Tests",
                "test:Aspire.Templates.Tests",
                "test:Aspire.Cli.EndToEnd.Tests",
                "job:cli-starter-validation",
                "job:extension-e2e",
                "job:homebrew-installer",
                "job:native-dashboard-validation",
                "job:winget-installer"
            ]
        },
        {
            "eng/dashboardpack/Sdk.targets",
            [
                "test:Aspire.Hosting.Sdk.Tests",
                "test:Aspire.Templates.Tests",
                "test:Aspire.Cli.EndToEnd.Tests",
                "job:cli-starter-validation",
                "job:extension-e2e",
                "job:homebrew-installer",
                "job:native-dashboard-validation",
                "job:winget-installer"
            ]
        },
        {
            "eng/dcppack/future-package-input.targets",
            ["test:Aspire.Cli.EndToEnd.Tests", "test:Aspire.Hosting.Sdk.Tests", "test:Aspire.Templates.Tests", "test:Aspire.TerminalHost.Tests", "job:extension-e2e"]
        },
        {
            "eng/scripts/load-cli-e2e-images.sh",
            ["test:Aspire.Cli.EndToEnd.Tests"]
        },
        {
            "eng/clipack/Common.projitems",
            [
                "test:Aspire.Cli.Tests",
                "test:Aspire.Cli.EndToEnd.Tests",
                "test:Infrastructure.Tests",
                "job:cli-starter-validation",
                "job:extension-e2e",
                "job:homebrew-installer",
                "job:winget-installer"
            ]
        },
        {
            "eng/clipack/Aspire.Cli.win-x64.csproj",
            [
                "test:Aspire.Cli.Tests",
                "test:Infrastructure.Tests",
                "job:cli-starter-validation",
                "job:extension-e2e",
                "job:homebrew-installer",
                "job:winget-installer"
            ]
        },
        {
            "eng/clipack/Aspire.Cli.linux-musl-x64.csproj",
            [
                "test:Aspire.Cli.Tests",
                "test:Infrastructure.Tests",
                "job:homebrew-installer",
                "job:winget-installer"
            ]
        },
        {
            "eng/clipack/Aspire.Cli.linux-arm64.csproj",
            [
                "test:Aspire.Cli.Tests",
                "test:Infrastructure.Tests",
                "job:cli-starter-validation",
                "job:homebrew-installer",
                "job:winget-installer"
            ]
        },
        {
            "eng/clipack/npm/aspire.js",
            [
                "test:Aspire.Cli.Tests",
                "test:Infrastructure.Tests",
                "job:homebrew-installer",
                "job:winget-installer"
            ]
        },
        {
            "eng/clipack/Aspire.Cli.NativeSymbols.proj",
            [
                "test:Aspire.Cli.Tests",
                "test:Infrastructure.Tests",
                "job:homebrew-installer",
                "job:winget-installer"
            ]
        },
        {
            "eng/scripts/cli-starter-validation.ps1",
            ["job:cli-starter-validation"]
        },
        {
            "eng/scripts/test-native-dashboard.ps1",
            ["job:native-dashboard-validation"]
        },
        {
            "eng/scripts/verify-native-dashboard-warnings.ps1",
            ["test:Infrastructure.Tests", "job:native-dashboard-validation"]
        },
        {
            ".github/workflows/native-dashboard-validation.yml",
            ["test:Infrastructure.Tests", "job:native-dashboard-validation"]
        },
        {
            "tests/Aspire.Dashboard.Tests/Aspire.Dashboard.Tests.csproj",
            ["test:Aspire.Dashboard.Tests", "job:native-dashboard-validation"]
        },
        {
            "tests/Aspire.Dashboard.Playwright.Tests/Integration/Playwright/NativeAotDashboardTests.cs",
            ["test:Aspire.Dashboard.Playwright.Tests", "job:native-dashboard-validation"]
        },
        {
            "tests/Aspire.Dashboard.Tests/Integration/NativeAotDashboardTests.cs",
            ["test:Aspire.Dashboard.Tests", "job:native-dashboard-validation"]
        },
        {
            "tests/Aspire.Dashboard.Playwright.Tests/Integration/Playwright/Infrastructure/PlaywrightFixture.cs",
            ["test:Aspire.Dashboard.Playwright.Tests", "job:native-dashboard-validation"]
        },
        {
            "tests/Aspire.Dashboard.Playwright.Tests/Aspire.Dashboard.Playwright.Tests.csproj",
            ["test:Aspire.Dashboard.Playwright.Tests", "job:native-dashboard-validation"]
        },
        {
            "tests/Shared/Playwright/Playwright.targets",
            ["test:Aspire.Dashboard.Playwright.Tests", "test:Aspire.Templates.Tests", "job:native-dashboard-validation"]
        },
        {
            "eng/scripts/get-aspire-cli-pr.ps1",
            [
                "test:Aspire.Acquisition.Tests",
                "test:Aspire.Cli.EndToEnd.Tests",
                "job:cli-starter-validation",
                "job:homebrew-installer",
                "job:winget-installer"
            ]
        },
        {
            "eng/scripts/get-aspire-cli-pr.sh",
            [
                "test:Aspire.Acquisition.Tests",
                "test:Aspire.Cli.EndToEnd.Tests",
                "job:homebrew-installer",
                "job:winget-installer"
            ]
        },
        {
            ".github/workflows/cli-starter-validation.yml",
            ["test:Infrastructure.Tests", "job:cli-starter-validation"]
        },
        {
            "eng/clipack/Aspire.Cli.linux-x64.csproj",
            ["test:Aspire.Cli.Tests", "test:Aspire.Cli.EndToEnd.Tests", "test:Infrastructure.Tests", "job:cli-starter-validation", "job:extension-e2e", "job:winget-installer", "job:homebrew-installer"]
        },
        {
            "tools/CreateLayout/Program.cs",
            ["test:Aspire.Cli.EndToEnd.Tests", "test:Infrastructure.Tests", "job:cli-starter-validation", "job:extension-e2e", "job:winget-installer", "job:homebrew-installer"]
        },
        {
            "eng/Bundle.proj",
            ["test:Aspire.Cli.EndToEnd.Tests", "test:Infrastructure.Tests", "job:cli-starter-validation", "job:extension-e2e", "job:winget-installer", "job:homebrew-installer"]
        },
        {
            "tools/CreateLayout/verify-tray-payload.sh",
            ["test:Aspire.Cli.EndToEnd.Tests", "test:Infrastructure.Tests", "job:cli-starter-validation", "job:extension-e2e", "job:winget-installer", "job:homebrew-installer"]
        },
        {
            "tools/CreateLayout/verify-windows-tray-payload.ps1",
            ["test:Aspire.Cli.EndToEnd.Tests", "test:Infrastructure.Tests", "job:cli-starter-validation", "job:extension-e2e", "job:winget-installer", "job:homebrew-installer"]
        },
        {
            "src/Aspire.Tray/Windows/publish.ps1",
            ["test:Aspire.Cli.EndToEnd.Tests", "test:Infrastructure.Tests", "job:cli-starter-validation", "job:extension-e2e", "job:winget-installer", "job:homebrew-installer"]
        },
        {
            "playground/JavaSpringBoot/JavaSpringBoot.AppHost.Java/aspire.config.json",
            ["test:Aspire.Playground.Tests", "job:extension-e2e"]
        },
        {
            "playground/TsIntegrationSpike/deno-api/deno.json",
            ["test:Aspire.Playground.Tests", "job:polyglot", "job:typescript-sdk"]
        },
        {
            "playground/TsIntegrationSpike/deno-integration/integration.ts",
            ["test:Aspire.Playground.Tests", "job:polyglot", "job:typescript-sdk"]
        },
        {
            "playground/TsIntegrationSpike/kafka-integration/host-runtime.ts",
            ["test:Aspire.Playground.Tests", "test:Aspire.Cli.EndToEnd.Tests", "job:polyglot", "job:typescript-sdk"]
        },
        {
            "playground/TsIntegrationSpike/aspire.config.json",
            ["test:Aspire.Playground.Tests", "job:polyglot"]
        },
        {
            "playground/TsKafkaLib/packages/aspire-kafka/src/index.ts",
            ["test:Aspire.Playground.Tests", "job:polyglot"]
        },
        {
            "playground/TsKafkaLib/app/tsconfig.json",
            ["test:Aspire.Playground.Tests", "job:polyglot"]
        },
        {
            "src/Aspire.Hosting.Nuxt/src/integration.ts",
            ["job:polyglot", "job:typescript-sdk"]
        },
        {
            "playground/NuxtApp/web/app/app.vue",
            ["test:Aspire.Playground.Tests", "job:polyglot"]
        },
        {
            "playground/NuxtApp/verify.mjs",
            ["test:Aspire.Playground.Tests", "job:polyglot", "job:typescript-sdk"]
        },
        {
            ".gitignore",
            ["test:Infrastructure.Tests"]
        },
    };

    [Theory]
    [MemberData(nameof(AuditedLoosePathCases))]
    public void AuditedLoosePathSelectsExactConsumerSetWithoutRunAllFallback(string path, string[] expectedTargets)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll, $"{path} unexpectedly selected ALL: {result.EscalationReason}");
        Assert.Empty(result.UnmatchedFiles);

        var actualTargets = result.TestProjects.Select(name => $"test:{name}")
            .Concat(result.Jobs)
            .Order(StringComparer.Ordinal);
        Assert.Equal(expectedTargets.Order(StringComparer.Ordinal), actualTargets);
    }

    [Theory]
    [InlineData("src/Aspire.Hosting.CodeGeneration.TypeScript/Resources/integration-host.mts")]
    [InlineData("src/Aspire.Hosting.CodeGeneration.TypeScript/Resources/transport.mts")]
    [InlineData("src/Aspire.Hosting.CodeGeneration.TypeScript/Resources/base.mts")]
    [InlineData("src/Aspire.Hosting.CodeGeneration.TypeScript/Resources/package.json")]
    public void IntegrationRuntimeResourcesSelectLifetimeE2eConsumer(string path)
    {
        var result = SelectWithRealMap(path, "Aspire.Hosting.CodeGeneration.TypeScript");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
        Assert.Contains(result.TestCauses["Aspire.Cli.EndToEnd.Tests"],
            cause => cause.Kind == CauseKind.PathRule && cause.Trigger == path);
        Assert.Contains("job:typescript-sdk", result.Jobs);
    }

    [Fact]
    [RequiresTools(["git"])]
    public void ExtensionJavaE2eDiscoveryInputsAreNotGitIgnored()
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = RepoRoot.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--no-pager");
        startInfo.ArgumentList.Add("check-ignore");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("extension/java-e2e-workspace/AppHost.java");
        startInfo.ArgumentList.Add("extension/java-e2e-workspace/aspire.config.json");
        startInfo.ArgumentList.Add("extension/java-e2e-workspace/JavaSpringBoot.AppHost.Java/AppHost.java");
        startInfo.ArgumentList.Add("extension/java-e2e-workspace/JavaSpringBoot.AppHost.Java/aspire.config.json");

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start git.");
        var ignoredPaths = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(
            process.ExitCode == 1,
            process.ExitCode == 0
                ? $"Git ignore rules must not exclude Java AppHost discovery inputs because aspire ls discovers them through git: {ignoredPaths}"
                : $"git check-ignore failed ({process.ExitCode}): {stderr}");
    }

    [Theory]
    [InlineData(".config/dotnet-tools.json")]
    [InlineData(".mcp.json")]
    [InlineData("src/Aspire.Dashboard/Components/Controls/TerminalTitle.razor.js")]
    [InlineData("src/Aspire.Dashboard/Components/Layout/TerminalDock.razor.js")]
    [InlineData("src/Aspire.Dashboard/wwwroot/js/app-terminalwindow.js")]
    [InlineData("tests/Aspire.Dashboard.Components.Tests/JavaScript/TerminalWindow.test.mjs")]
    [InlineData("tests/Aspire.Dashboard.Components.Tests/JavaScript/TerminalTitle.test.mjs")]
    [InlineData("tests/Aspire.Dashboard.Components.Tests/JavaScript/TerminalDock.test.mjs")]
    [InlineData("src/Aspire.Dashboard/wwwroot/js/app-resourcegraph.js")]
    [InlineData("tests/Aspire.Dashboard.Components.Tests/JavaScript/ResourceGraph.test.mjs")]
    [InlineData("src/Aspire.Dashboard/wwwroot/js/app-dashboard-experience.js")]
    [InlineData("tests/Aspire.Dashboard.Components.Tests/JavaScript/DashboardExperience.test.mjs")]
    public void DashboardScriptInputsSelectInfrastructureTests(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        // Infrastructure executes the scripts in addition to the consumers attributed by the project graph.
        Assert.Contains("Infrastructure.Tests", result.TestProjects);
    }

    [Theory]
    [InlineData("eng/scripts/gha-testreport.ps1")]
    [InlineData("eng/scripts/split-test-projects-for-ci.ps1")]
    [InlineData("tools/ExtractTestPartitions/Program.cs")]
    public void BroadCiInputHasAnExplicitRunAllRule(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.True(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
    }

    [Fact]
    public void GitAttributesLineEndingRulesRunTheFullMatrix()
    {
        var result = SelectWithRealMap(".gitattributes");

        Assert.True(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
    }

    [Fact]
    public void SolutionEditRunsTheFullMatrix()
    {
        var soloEdit = SelectWithRealMap("Aspire.slnx");

        Assert.True(soloEdit.SelectsAll);
        Assert.Empty(soloEdit.UnmatchedFiles);

        // A project removed from the solution takes its files with it; nothing at head owns them, so the
        // run-all fallback still fires on the deleted project itself.
        var removal = SelectWithRealMap("src/Aspire.Hosting.Retired/Aspire.Hosting.Retired.csproj");

        Assert.True(removal.SelectsAll);
    }

    [Theory]
    [InlineData("eng/common/BuildConfiguration/build-configuration.json")]
    [InlineData("eng/common/loc/P22DotNetHtmlLocalization.lss")]
    [InlineData("eng/common/generate-locproject.ps1")]
    [InlineData("eng/common/native/NativeAotSupported.props")]
    public void EngCommonInputsRunTheFullMatrix(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.True(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
    }

    [Theory]
    [InlineData("eng/common/templates/jobs/jobs.yml")]
    [InlineData("eng/common/templates-official/jobs/jobs.yml")]
    [InlineData("eng/common/core-templates/jobs/jobs.yml")]
    public void EngCommonPipelineTemplatesAlsoRunTheFullMatrix(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.True(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
    }

    [Theory]
    [InlineData("eng/scripts/verify-cli-npm-package.ps1")]
    [InlineData("eng/scripts/verify-cli-tool-nupkg.ps1")]
    [InlineData("eng/scripts/stabilization-smoke-init-restore.sh")]
    [InlineData("eng/generate-catalog.ps1")]
    [InlineData("eng/scripts/update-aspire-skills-bundle.ps1")]
    [InlineData("eng/scripts/verify-aspire-skills-bundle.ps1")]
    public void PathHandledOutsideSelectorDoesNotForceTestSelection(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Empty(result.TestProjects);
        Assert.Empty(result.Jobs);
    }

    [Theory]
    [InlineData("src/Aspire.Hosting.Dotnet/Aspire.Hosting.Dotnet.csproj", "Aspire.Hosting.Dotnet")]
    [InlineData("src/Aspire.Hosting.Foundry/Aspire.Hosting.Foundry.csproj", "Aspire.Hosting.Foundry")]
    public void DeploymentE2eIsSelectedWhenRuntimeHostingDependencyIsAffected(string path, string affectedProject)
    {
        var result = SelectWithRealMap(path, affectedProject);

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Contains("job:deployment-e2e", result.Jobs);
        Assert.Contains(result.JobCauses["job:deployment-e2e"], cause =>
            cause.Kind == CauseKind.AffectedProject && cause.Trigger == affectedProject);
    }

    [Fact]
    public void DeploymentE2eIsNotSelectedForFoundryTestOnlyChange()
    {
        var result = SelectWithRealMap(
            "tests/Aspire.Hosting.Foundry.Tests/Aspire.Hosting.Foundry.Tests.csproj",
            "Aspire.Hosting.Foundry.Tests");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Equal("Aspire.Hosting.Foundry.Tests", Assert.Single(result.TestProjects));
        Assert.Empty(result.Jobs);
    }

    [Theory]
    [InlineData("src/Aspire.Cli/Commands/RunCommand.cs", "Aspire.Cli")]
    [InlineData("src/Aspire.Managed/Program.cs", "Aspire.Managed")]
    [InlineData("src/Aspire.AppHost.Sdk/Aspire.AppHost.Sdk.csproj", "Aspire.AppHost.Sdk")]
    [InlineData("src/Aspire.Hosting.JavaScript/JavaScriptAppResource.cs", "Aspire.Hosting.JavaScript")]
    [InlineData("src/Aspire.Hosting.CodeGeneration.TypeScript/TypeScriptApiProjector.cs", "Aspire.Hosting.CodeGeneration.TypeScript")]
    [InlineData("src/Aspire.Hosting.AppHost/Aspire.Hosting.AppHost.csproj", "Aspire.Hosting.AppHost")]
    [InlineData("src/Aspire.Hosting.PostgreSQL/PostgresBuilderExtensions.cs", "Aspire.Hosting.PostgreSQL")]
    [InlineData("src/Aspire.Hosting.Dotnet/DotnetProjectResourceBuilderExtensions.cs", "Aspire.Hosting.Dotnet")]
    public void CliStarterValidationRunsWhenStarterRuntimeDependencyIsAffected(string path, string affectedProject)
    {
        var result = SelectWithRealMap(path, affectedProject);

        Assert.False(result.SelectsAll);
        Assert.Contains("job:cli-starter-validation", result.Jobs);
        Assert.Contains(result.JobCauses["job:cli-starter-validation"], cause => cause.Kind == CauseKind.AffectedProject);
    }

    [Theory]
    [InlineData("src/Aspire.ProjectTemplates/Aspire.ProjectTemplates.csproj")]
    [InlineData("src/Aspire.ProjectTemplates/templates/aspire-starter/Aspire-StarterApplication.1.AppHost/AppHost.cs")]
    [InlineData("eng/Bundle.proj")]
    public void CliStarterValidationRunsForLooseStarterConsumerChange(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Contains("job:cli-starter-validation", result.Jobs);
    }

    [Theory]
    [InlineData("src/Aspire.ProjectTemplates/Aspire.ProjectTemplates.csproj")]
    // This path does not exist intentionally: it proves a newly added template is covered by the
    // package-wide rule instead of repeating the current template inventory in the test.
    [InlineData("src/Aspire.ProjectTemplates/templates/future-template/.template.config/template.json")]
    public void CliE2eRunsForProjectTemplatePackageInputs(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
    }

    [Theory]
    [InlineData("src/Aspire.ProjectTemplates/templates/aspire-starter/.template.config/aspirecli.host.json")]
    [InlineData("src/Aspire.ProjectTemplates/templates/aspire-apphost/.template.config/dotnetcli.host.json")]
    [InlineData("src/Aspire.ProjectTemplates/templates/future-template/.template.config/template.json")]
    [InlineData("src/Aspire.ProjectTemplates/templates/aspire-starter/Aspire-StarterApplication.1.AppHost/Properties/launchSettings.json")]
    public void LocalTemplateSourcesRunCliUnitCoverage(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Cli.Tests", result.TestProjects);
    }

    [Fact]
    public void CliBundleConsumersRunWhenCreateLayoutIsAffected()
    {
        var result = SelectWithRealMap("tools/CreateLayout/Program.cs", "CreateLayout");

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
        Assert.Contains(
            result.TestCauses["Aspire.Cli.EndToEnd.Tests"],
            cause => cause.Kind == CauseKind.AffectedProject);
        Assert.Equal(
            ["job:cli-starter-validation", "job:extension-e2e", "job:homebrew-installer", "job:winget-installer"],
            result.Jobs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void CliBundleConsumersRunWhenStandaloneTerminalHostIsAffected()
    {
        var result = SelectWithRealMap("src/Aspire.TerminalHost/Program.cs", "Aspire.TerminalHost");

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
        Assert.Equal(
            ["job:cli-starter-validation", "job:extension-e2e", "job:homebrew-installer", "job:winget-installer"],
            result.Jobs.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Aspire.Cli", "Aspire.Cli.EndToEnd.Tests")]
    [InlineData("Aspire.TerminalHost", "Aspire.Cli.EndToEnd.Tests")]
    [InlineData("Aspire.Dashboard", "Aspire.Templates.Tests")]
    public void TerminalPipelinePublishSettingsRunTheirInfrastructureCoverage(string projectName, string consumerProject)
    {
        var result = SelectWithRealMap(
            $"src/{projectName}/{projectName}.csproj", projectName);

        Assert.False(result.SelectsAll);
        Assert.Contains("Infrastructure.Tests", result.TestProjects);
        Assert.Contains(consumerProject, result.TestProjects);
    }

    [Fact]
    public void DashboardChangesRunTemplatesTestsThatStartAppHosts()
    {
        var result = SelectWithRealMap(
            "src/Aspire.Dashboard/Program.cs",
            "Aspire.Dashboard");

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Templates.Tests", result.TestProjects);
        Assert.Contains("job:native-dashboard-validation", result.Jobs);
    }

    [Fact]
    public void DotnetProjectResourceChangesRunGeneratedAppHostConsumers()
    {
        var result = SelectWithRealMap("src/Aspire.Hosting.Dotnet/Aspire.Hosting.Dotnet.csproj", "Aspire.Hosting.Dotnet");

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Templates.Tests", result.TestProjects);
        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
        Assert.Contains("job:extension-e2e", result.Jobs);
        Assert.Contains("job:cli-starter-validation", result.Jobs);
    }

    [Fact]
    public void DotnetProjectResourceChangesRunGeneratedAppHostConsumersWithoutProjectGraph()
    {
        var result = SelectWithRealMap("src/Aspire.Hosting.Dotnet/Aspire.Hosting.Dotnet.csproj");

        Assert.False(result.SelectsAll);
        Assert.Contains("Aspire.Templates.Tests", result.TestProjects);
        Assert.Contains("Aspire.Cli.EndToEnd.Tests", result.TestProjects);
        Assert.Contains("job:extension-e2e", result.Jobs);
        Assert.Contains("job:cli-starter-validation", result.Jobs);
    }

    [Fact]
    public void CliStarterValidationDoesNotRunForUnrelatedHostingIntegrationChange()
    {
        var result = SelectWithRealMap(
            "src/Aspire.Hosting.Redis/RedisBuilderExtensions.cs",
            "Aspire.Hosting.Redis",
            "Aspire.Hosting.Redis.Tests",
            "Aspire.Hosting.Tests");

        Assert.False(result.SelectsAll);
        Assert.False(
            result.Jobs.Contains("job:cli-starter-validation", StringComparer.Ordinal),
            "Aggregate tests selected by Redis changes must not pull in all six starter-validation jobs.");
    }

    [Fact]
    public void CliStarterValidationDoesNotRunForNpgsqlClientChange()
    {
        var result = SelectWithRealMap(
            "src/Components/Aspire.Npgsql/NpgsqlCommon.cs",
            "Aspire.Npgsql",
            "Aspire.Npgsql.Tests",
            "Aspire.Hosting.PostgreSQL.Tests");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.Jobs);
    }

    [Fact]
    public void UnrelatedProjectTemplateChangeRunsCliE2eAndTemplatesTestsWithoutStarterValidation()
    {
        var result = SelectWithRealMap(
            "src/Aspire.ProjectTemplates/templates/aspire-xunit/.template.config/template.json");

        Assert.False(result.SelectsAll);
        Assert.Equal(
            ["Aspire.Cli.EndToEnd.Tests", "Aspire.Cli.Tests", "Aspire.Templates.Tests"],
            result.TestProjects.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["job:deployment-e2e", "job:homebrew-installer", "job:winget-installer"],
            result.Jobs.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("src/Aspire.ProjectTemplates/templates/aspire-ts-cs-starter/frontend/package.json")]
    [InlineData("src/Aspire.ProjectTemplates/templates/aspire-ts-cs-starter/frontend/package-lock.json")]
    public void ProjectTemplateFrontendDependencyInputsRunCliSecurityGuards(string path)
    {
        var result = SelectWithRealMap(path);

        Assert.False(result.SelectsAll);
        Assert.Equal(
            ["Aspire.Cli.EndToEnd.Tests", "Aspire.Cli.Tests", "Aspire.Templates.Tests"],
            result.TestProjects.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["job:deployment-e2e", "job:homebrew-installer", "job:winget-installer"],
            result.Jobs.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("src/Aspire.Hosting.CodeGeneration.Go/AtsGoCodeGenerator.cs")]
    [InlineData("src/Aspire.Hosting.CodeGeneration.Go/GoLanguageSupport.cs")]
    public void GoModTemplatesSelectCliMinimumGoVersionGuard(string path)
    {
        var result = SelectWithRealMap(path, "Aspire.Hosting.CodeGeneration.Go");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Contains("Aspire.Cli.Tests", result.TestProjects);
        Assert.Contains(result.TestCauses["Aspire.Cli.Tests"],
            cause => cause.Kind == CauseKind.PathRule && cause.Trigger == path);
    }

    [Fact]
    public void CliStarterValidationDoesNotRunForSharedTestUtilityChange()
    {
        var result = SelectWithRealMap(
            "tests/Aspire.TestUtilities/FileUtil.cs",
            "Aspire.TestUtilities",
            "Aspire.Cli.Tests",
            "Aspire.Cli.EndToEnd.Tests");

        Assert.False(result.SelectsAll);
        Assert.False(
            result.Jobs.Contains("job:cli-starter-validation", StringComparer.Ordinal),
            "Shared test utilities must not pull in all six starter-validation jobs.");
    }

    [Fact]
    public void CliStarterValidationDoesNotRunForHostingSdkTestFakeChange()
    {
        var result = SelectWithRealMap(
            "tests/Aspire.Hosting.Sdk.Tests.FakeCommand/Program.cs",
            "Aspire.Hosting.Sdk.Tests.FakeCommand",
            "Aspire.Hosting.Sdk.Tests");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.Jobs);
    }

    [Fact]
    public void BuildPackagesWorkflowSelectsOnlyNuGetArtifactConsumers()
    {
        var result = SelectWithRealMap(".github/workflows/build-packages.yml");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Equal(
            ["Aspire.Cli.EndToEnd.Tests", "Aspire.Templates.Tests", "Infrastructure.Tests"],
            result.TestProjects.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["job:cli-starter-validation", "job:extension-e2e", "job:homebrew-installer", "job:polyglot", "job:winget-installer"],
            result.Jobs.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void BuildCliNativeArchivesWorkflowSelectsOnlyCliArchiveConsumers()
    {
        var result = SelectWithRealMap(".github/workflows/build-cli-native-archives.yml");

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Equal(
            ["Aspire.Cli.EndToEnd.Tests", "Aspire.Templates.Tests", "Infrastructure.Tests"],
            result.TestProjects.Order(StringComparer.Ordinal));
        Assert.Equal(
            ["job:cli-starter-validation", "job:extension-e2e", "job:homebrew-installer", "job:native-dashboard-validation", "job:polyglot", "job:winget-installer"],
            result.Jobs.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(".github/workflows/build-packages.yml")]
    [InlineData(".github/workflows/build-cli-native-archives.yml")]
    public void ArtifactProducerWorkflowSelectsEveryArtifactConsumingTestProject(string path)
    {
        // The narrowed routing above is only safe while the producer consumer groups still list every test
        // project that consumes a produced artifact in regular PR CI. Those projects declare artifact
        // consumption via RequiresNugets / RequiresCliArchive (read by eng/testing/CITestsProperties.props
        // into the runsheet, which is what puts them in the tests_requires_nugets_* /
        // tests_requires_cli_archive buckets that depend on the producer jobs). Adding a new regular-PR
        // consumer without updating the group would silently stop running it for producer-workflow changes
        // -- exactly the under-selection selective CI must never do.
        var consumers = LoadArtifactConsumingTestProjects()
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(consumers);

        var result = SelectWithRealMap(path);
        foreach (var consumer in consumers)
        {
            Assert.Contains(consumer, result.TestProjects, StringComparer.Ordinal);
        }
    }

    [Fact]
    public void ArtifactProducerWorkflowSelectsEverySelectorGatedArtifactConsumingJob()
    {
        var workflow = new YamlStream();
        using (var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "tests.yml"))))
        {
            workflow.Load(reader);
        }

        var root = Assert.IsType<YamlMappingNode>(workflow.Documents[0].RootNode);
        var jobs = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("jobs")]);
        var packageSelectedJobs = SelectWithRealMap(".github/workflows/build-packages.yml").Jobs.ToHashSet(StringComparer.Ordinal);
        var cliArchiveSelectedJobs = SelectWithRealMap(".github/workflows/build-cli-native-archives.yml").Jobs.ToHashSet(StringComparer.Ordinal);

        var packageConsumerTargets = ArtifactConsumingWorkflowJobTargets(jobs, "build_packages").ToList();
        var cliArchiveConsumerTargets = ArtifactConsumingWorkflowJobTargets(jobs, "build_cli_archive_").ToList();
        Assert.Contains(packageConsumerTargets, target => target.WorkflowJobId == "polyglot_validation");
        Assert.Contains(packageConsumerTargets, target => target.WorkflowJobId == "extension_e2e_tests");
        Assert.Contains(packageConsumerTargets, target => target.WorkflowJobId == "prepare_winget_installer_artifacts");
        Assert.Contains(cliArchiveConsumerTargets, target => target.WorkflowJobId == "native_dashboard_validation_linux_x64");
        Assert.DoesNotContain(packageSelectedJobs, job => job == "job:native-dashboard-validation");

        foreach (var (workflowJobId, target) in packageConsumerTargets)
        {
            Assert.True(
                packageSelectedJobs.Contains(target),
                $"{workflowJobId} consumes build_packages artifacts, so {target} must be selected.");
        }

        foreach (var (workflowJobId, target) in cliArchiveConsumerTargets)
        {
            Assert.True(
                cliArchiveSelectedJobs.Contains(target),
                $"{workflowJobId} consumes build_cli_archive_* artifacts, so {target} must be selected.");
        }
    }

    [Theory]
    [InlineData(".github/actions/check-changed-files/action.yml", false, new[] { "Infrastructure.Tests" }, new string[] { })]
    [InlineData(".github/actions/select-tests/action.yml", false, new[] { "Infrastructure.Tests" }, new string[] { })]
    [InlineData(".github/actions/setup-deno/action.yml", false, new[] { "Aspire.Hosting.JavaScript.Tests" }, new[] { "job:extension-e2e" })]
    [InlineData(".github/actions/unlock-macos-keychain/action.yml", true, new string[] { }, new string[] { })]
    public void LocalActionRoutingMatchesItsRegularPrCiConsumers(
        string path,
        bool selectsAll,
        string[] expectedTestProjects,
        string[] expectedJobs)
    {
        var result = SelectWithRealMap(path);

        Assert.Equal(selectsAll, result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);

        if (!selectsAll)
        {
            Assert.Equal(expectedTestProjects.Order(StringComparer.Ordinal), result.TestProjects.Order(StringComparer.Ordinal));
            Assert.Equal(expectedJobs.Order(StringComparer.Ordinal), result.Jobs.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void EnumerateTestsActionSelectsManagedTestProjectsButNotNonDotnetJobs()
    {
        var result = SelectWithRealMap(".github/actions/enumerate-tests/action.yml");
        var expectedTestProjects = LoadSolutionProjectPaths()
            .Where(projectPath => projectPath.StartsWith("tests/", StringComparison.Ordinal))
            .Select(projectPath => Path.GetFileNameWithoutExtension(projectPath)!)
            .Where(name => name.EndsWith(".Tests", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.False(result.SelectsAll);
        Assert.Empty(result.UnmatchedFiles);
        Assert.Equal(expectedTestProjects, result.TestProjects.Order(StringComparer.Ordinal));
        Assert.Empty(result.Jobs);
    }

    private static SelectionResult SelectWithRealMap(string path, params string[] layer1Affected)
    {
        var projectPaths = LoadSolutionProjectPaths();
        var testProjects = projectPaths
            .Where(projectPath => projectPath.StartsWith("tests/", StringComparison.Ordinal))
            .Select(projectPath => Path.GetFileNameWithoutExtension(projectPath)!)
            .Where(name => name.EndsWith(".Tests", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var testProjectNames = Directory.EnumerateFiles(
                Path.Combine(RepoRoot.Path, "tests"), "*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => name is not null)
            .Select(name => name!)
            .ToHashSet(StringComparer.Ordinal);
        var affectedTestProjectNames = layer1Affected
            .Where(testProjectNames.Contains)
            .ToHashSet(StringComparer.Ordinal);
        var projectDirectories = projectPaths
            .Select(projectPath => Path.GetDirectoryName(projectPath)!.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        var mapPath = Path.Combine(RepoRoot.Path, "eng", "github-ci", "test-trigger-map.yml");
        var selector = new TestSelector(mapPath, testProjects, projectDirectories, affectedTestProjectNames);

        return selector.Select([path], layer1Affected, new SelectorOptions());
    }

    private static IEnumerable<(string WorkflowJobId, string Target)> ArtifactConsumingWorkflowJobTargets(YamlMappingNode jobs, string producerPrefix)
    {
        foreach (var (jobIdNode, jobNode) in jobs.Children)
        {
            var jobId = Assert.IsType<YamlScalarNode>(jobIdNode).Value;
            Assert.False(string.IsNullOrWhiteSpace(jobId), "tests.yml contains a job with an empty id.");
            var job = Assert.IsType<YamlMappingNode>(jobNode);
            if (jobId == "results")
            {
                continue;
            }

            if (!DependsOnArtifactProducer(job, producerPrefix)
                || !job.Children.TryGetValue(new YamlScalarNode("if"), out var ifNode))
            {
                continue;
            }

            var runVars = System.Text.RegularExpressions.Regex
                .Matches(ifNode.ToString(), @"needs\.setup_for_tests\.outputs\.(run_[a-z0-9_]+)")
                .Select(match => match.Groups[1].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            foreach (var runVar in runVars)
            {
                yield return (jobId!, "job:" + runVar["run_".Length..].Replace('_', '-'));
            }
        }
    }

    private static bool DependsOnArtifactProducer(YamlMappingNode job, string producerPrefix)
    {
        if (!job.Children.TryGetValue(new YamlScalarNode("needs"), out var needsNode))
        {
            return false;
        }

        return needsNode switch
        {
            YamlScalarNode scalar => IsArtifactProducer(scalar.Value, producerPrefix),
            YamlSequenceNode sequence => sequence.Children
                .OfType<YamlScalarNode>()
                .Any(node => IsArtifactProducer(node.Value, producerPrefix)),
            _ => false
        };

        static bool IsArtifactProducer(string? jobId, string producerPrefix) =>
            producerPrefix == "build_packages"
                ? jobId == producerPrefix
                : jobId?.StartsWith(producerPrefix, StringComparison.Ordinal) is true;
    }

    private static IEnumerable<string> LoadArtifactConsumingTestProjects()
    {
        GraphAffectedProjects.EnsureMSBuildRegistered();

        var solutionPath = Path.Combine(RepoRoot.Path, "Aspire.slnx");
        return LoadArtifactConsumingTestProjectsCore(solutionPath);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IEnumerable<string> LoadArtifactConsumingTestProjectsCore(string solutionPath)
    {
        return LoadArtifactConsumingTestProjectMetadata(solutionPath)
            .Where(project => !project.SkipTests)
            .Select(project => project.Name);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IEnumerable<ArtifactConsumingTestProject> LoadArtifactConsumingTestProjectMetadata(string solutionPath)
    {
        var graph = GraphAffectedProjects.BuildGraph(
            RepoRoot.Path,
            solutionPath,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["IsGitHubActionsRunner"] = "true",
                ["IsGithubPullRequest"] = "true",
                ["RunOuterloopTests"] = "false",
                ["RunQuarantinedTests"] = "false",
            });

        return graph.ProjectNodes
            .Where(node => IsMatrixTestProject(node.ProjectInstance.FullPath))
            .Where(node =>
                IsTrue(node.ProjectInstance.GetPropertyValue("RequiresNugets"))
                || IsTrue(node.ProjectInstance.GetPropertyValue("RequiresCliArchive")))
            .Select(node => new ArtifactConsumingTestProject(
                Path.GetFileNameWithoutExtension(node.ProjectInstance.FullPath)!,
                IsTrue(node.ProjectInstance.GetPropertyValue("SkipTests"))))
            .GroupBy(project => project.Name, StringComparer.Ordinal)
            .Select(group => group.First());
    }

    private sealed record ArtifactConsumingTestProject(string Name, bool SkipTests);

    private static bool IsMatrixTestProject(string projectPath)
    {
        var relativePath = Path.GetRelativePath(RepoRoot.Path, projectPath).Replace('\\', '/');
        return relativePath.StartsWith("tests/", StringComparison.Ordinal)
            && Path.GetFileNameWithoutExtension(relativePath).EndsWith(".Tests", StringComparison.Ordinal);
    }

    private static bool IsTrue(string value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
