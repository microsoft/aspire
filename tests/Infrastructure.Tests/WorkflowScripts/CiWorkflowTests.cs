// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class CiWorkflowTests
{
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
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "run-tests.yml"));
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
    public void CliTestsUsePipelineNuGetServiceIndexOverride()
    {
        var workflow = ReadWorkflow("run-tests.yml");
        var configureStep = GetStep(GetJob(workflow, "test"), "Configure CLI test NuGet service index");
        Assert.Contains("$env:TEST_ASSEMBLY_NAME -eq 'Aspire.Cli.Tests'", configureStep);
        Assert.Contains("$env:GITHUB_ENV", configureStep);
        var serviceIndexMatch = System.Text.RegularExpressions.Regex.Match(
            configureStep,
            "ASPIRE_CLI_NUGET_SERVICE_INDEX=(?<source>https://[^\"\\r\\n]+)");
        Assert.True(serviceIndexMatch.Success, "The GitHub test runner must provide an HTTPS NuGet service index.");
        var serviceIndex = serviceIndexMatch.Groups["source"].Value;

        var pipeline = File.ReadAllText(Path.Combine(
            RepoRoot.Path,
            "eng",
            "pipelines",
            "templates",
            "BuildAndTest.yml"));
        var nonHelixTestStep = System.Text.RegularExpressions.Regex.Match(
            pipeline,
            "(?ms)^    - script: .*?^      displayName: Run non-helix tests$");
        Assert.True(nonHelixTestStep.Success, "Could not find the non-Helix test step in BuildAndTest.yml.");
        Assert.Contains($"ASPIRE_CLI_NUGET_SERVICE_INDEX: {serviceIndex}", nonHelixTestStep.Value);
    }

    [Fact]
    public void AcquisitionOuterloopTestsReceiveGitHubToken()
    {
        var properties = File.ReadAllText(Path.Combine(
            RepoRoot.Path,
            "eng",
            "testing",
            "CITestsProperties.props"));
        Assert.Contains(
            "<CITestsProperty Include=\"requiresGitHubToken\" MSBuildProp=\"RequiresGitHubToken\"",
            properties);

        var acquisitionTests = File.ReadAllText(Path.Combine(
            RepoRoot.Path,
            "tests",
            "Aspire.Acquisition.Tests",
            "Aspire.Acquisition.Tests.csproj"));
        Assert.Contains("<RequiresGitHubToken>true</RequiresGitHubToken>", acquisitionTests);

        var specializedRunner = ReadWorkflow("specialized-test-runner.yml");
        var tokenCheck = GetStep(
            GetJob(specializedRunner, "generate_tests_matrix"),
            "Check if any test requires GitHub token");
        Assert.Contains("steps.inject_properties.outputs.runsheet", tokenCheck);
        Assert.Contains(".properties.requiresGitHubToken == true", tokenCheck);

        var testRunner = GetJob(ReadWorkflow("run-tests.yml"), "test");
        Assert.Contains(
            "fromJson(inputs.properties).requiresGitHubToken == true",
            testRunner);
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
        => File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", fileName));

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
