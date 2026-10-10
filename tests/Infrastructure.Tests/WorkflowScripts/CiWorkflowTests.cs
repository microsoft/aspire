// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class CiWorkflowTests
{
    [Fact]
    public void TemplateManifestGenerationUsesSameBuildPackagesBeforeCleanup()
    {
        var job = GetJob(ReadWorkflow("build-packages.yml"), "build_packages");
        var generate = GetStep(job, "Generate template component manifest");

        Assert.True(job.IndexOf("name: Build with packages", StringComparison.Ordinal) <
            job.IndexOf("name: Generate template component manifest", StringComparison.Ordinal));
        Assert.True(job.IndexOf("name: Generate template component manifest", StringComparison.Ordinal) <
            job.IndexOf("name: Clean up artifacts", StringComparison.Ordinal));
        Assert.Contains("eng/scripts/generate-template-cgmanifest.ps1", generate);
    }

    [Fact]
    public void TemplateManifestArtifactContainsOnlyTheFinalInventory()
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(ReadWorkflow("build-packages.yml")));
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children["jobs"];
        var job = (YamlMappingNode)jobs.Children["build_packages"];
        var steps = (YamlSequenceNode)job.Children["steps"];
        var upload = Assert.Single(steps.Children.Cast<YamlMappingNode>(), step =>
            step.Children.TryGetValue("name", out var name) && ((YamlScalarNode)name).Value == "Upload template component manifest");
        var inputs = (YamlMappingNode)upload.Children["with"];

        Assert.Equal("artifacts/cg/templates/cgmanifest.json", ((YamlScalarNode)inputs.Children["path"]).Value);
        Assert.Equal("5", ((YamlScalarNode)inputs.Children["retention-days"]).Value);
        Assert.Equal("error", ((YamlScalarNode)inputs.Children["if-no-files-found"]).Value);
    }

    [Theory]
    [InlineData("prepare_winget_installer_artifacts")]
    [InlineData("prepare_homebrew_installer_artifacts")]
    public void InstallerJobsDependOnBuiltPackages(string jobName)
    {
        var workflow = ReadWorkflow("tests.yml");
        var job = GetJob(workflow, jobName);

        Assert.Contains("      build_packages,", job);
    }

    [Fact]
    public void InstallerWorkflowStagesSameRunTemplatePackages()
    {
        var workflow = ReadWorkflow("prepare-installer-artifacts.yml");
        var job = GetJob(workflow, "prepare_installer_artifacts");
        var downloadStep = GetStep(job, "Download NuGet packages");
        var configureStep = GetStep(job, "Configure CLI package override");

        Assert.Contains("name: built-nugets", downloadStep);
        Assert.Contains("path: ${{ github.workspace }}/built-nugets", downloadStep);
        Assert.Contains("Aspire.ProjectTemplates.*.nupkg", configureStep);
        Assert.Contains("Where-Object { $_.Directory.Name -eq 'Shipping' }", configureStep);
        Assert.Contains("ASPIRE_CLI_PACKAGES=$packageDirectory", configureStep);
        Assert.Contains("$env:GITHUB_ENV", configureStep);
    }

    [Fact]
    public void RunTestsInstallsJavaForProjectsThatRequireIt()
    {
        var workflow = ReadWorkflow("run-tests.yml");
        var javaSetup = System.Text.RegularExpressions.Regex.Match(
            workflow,
            "(?ms)^      - name: Set up Java\\r?\\n(?<body>.*?)(?=^      - |\\z)");
        Assert.True(javaSetup.Success, "Could not find the Java setup step in run-tests.yml.");
        Assert.Contains("if: ${{ fromJson(inputs.properties).requiresJava == true }}", javaSetup.Value);
        Assert.Contains("uses: actions/setup-java@", javaSetup.Value);
        Assert.Contains("distribution: temurin", javaSetup.Value);
        Assert.Contains("java-version: 21", javaSetup.Value);

        var properties = File.ReadAllText(Path.Combine(RepoRoot.Path, "eng", "testing", "CITestsProperties.props"));
        Assert.Contains("<CITestsProperty Include=\"requiresJava\" MSBuildProp=\"RequiresJava\"", properties);

        var javaTests = File.ReadAllText(Path.Combine(
            RepoRoot.Path,
            "tests",
            "Aspire.Hosting.CodeGeneration.Java.Tests",
            "Aspire.Hosting.CodeGeneration.Java.Tests.csproj"));
        Assert.Contains("<RequiresJava>true</RequiresJava>", javaTests);
    }

    [Fact]
    public void CiFailureTrackerCheckoutDoesNotPinMain()
    {
        var workflow = ReadWorkflow("ci.yml");
        var job = GetJob(workflow, "ci_failure_tracker");

        var checkout = System.Text.RegularExpressions.Regex.Match(job, "(?ms)^      - uses: actions/checkout@.*?(?=^      - |\\z)");
        Assert.True(checkout.Success, "Could not find the ci_failure_tracker checkout step.");

        // Push CI also runs on release/**. Pinning this checkout to main makes the
        // tracker execute main's reporter instead of the workflow code from the branch
        // whose run is being evaluated.
        Assert.DoesNotContain("ref: main", checkout.Value);
    }

    private static string ReadWorkflow(string fileName)
        => File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName)).ReplaceLineEndings("\n");

    private static string GetJob(string workflow, string jobName)
    {
        var job = System.Text.RegularExpressions.Regex.Match(
            workflow,
            $@"(?ms)^  {System.Text.RegularExpressions.Regex.Escape(jobName)}:\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\n|\z)");
        Assert.True(job.Success, $"Could not find the {jobName} job.");

        return job.Value;
    }

    private static string GetStep(string job, string stepName)
    {
        var step = System.Text.RegularExpressions.Regex.Match(
            job,
            $@"(?ms)^      - name: {System.Text.RegularExpressions.Regex.Escape(stepName)}\n.*?(?=^      - |\z)");
        Assert.True(step.Success, $"Could not find the {stepName} step.");

        return step.Value;
    }
}
