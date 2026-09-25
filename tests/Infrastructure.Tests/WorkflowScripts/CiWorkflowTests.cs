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
            "(?ms)^      - name: Set up Java\\n(?<body>.*?)(?=^      - |\\z)");
        Assert.True(javaSetup.Success, "Could not find the Java setup step in run-tests.yml.");
        Assert.Contains("if: ${{ fromJson(inputs.properties).requiresJava == true }}", javaSetup.Value);
        Assert.Contains("uses: actions/setup-java@b6effb05e454b25005698d916606bdc6ffcbf961 # v5.7.0", javaSetup.Value);
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
