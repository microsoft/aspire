// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

public sealed class CiWorkflowTests
{
    [Fact]
    public void RunTestsWorkflowInvokesDotnetExeDirectlyForWindowsTests()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "run-tests.yml"));
        var windowsRunTestsStep = System.Text.RegularExpressions.Regex.Match(workflow, "(?ms)^      - name: Run tests \\(Windows\\).*?(?=^      - |\\z)");
        Assert.True(windowsRunTestsStep.Success, "Could not find the Windows run-tests step in run-tests.yml.");

        Assert.Contains(@"\artifacts\toolset\sdk.txt", windowsRunTestsStep.Value);
        Assert.Contains("Join-Path $dotnetSdkDir \"dotnet.exe\"", windowsRunTestsStep.Value);
        Assert.Contains("Start-Process -FilePath $dotnetExecutable", windowsRunTestsStep.Value);
        Assert.Contains("& $dotnetExecutable test --project", windowsRunTestsStep.Value);
        Assert.DoesNotContain("& ${{ env.DOTNET_SCRIPT }} test", windowsRunTestsStep.Value);
    }

    [Fact]
    public void RunTestsWorkflowOmitsSimpleFailingFilterWhenUsingQueryFilters()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "run-tests.yml"));

        Assert.DoesNotContain("contains(inputs.extraTestArgs, '--filter-query') && '' ||", workflow);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            workflow,
            @"EXPLICIT_FAILING_FILTER:\s*\$\{\{ !contains\(inputs\.extraTestArgs, '--filter-query'\) && '--filter-not-trait ""category=failing""' \|\| '' \}\}").Count);
    }

    [Fact]
    public void SpecializedWorkflowsKeepPullRequestPathFiltersInSync()
    {
        var quarantineWorkflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "tests-quarantine.yml"));
        var outerloopWorkflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "tests-outerloop.yml"));

        var quarantinePaths = GetPullRequestPaths(quarantineWorkflow);
        var outerloopPaths = GetPullRequestPaths(outerloopWorkflow);

        var sharedPaths = new[]
        {
            ".github/workflows/specialized-test-runner.yml",
            ".github/workflows/run-tests-core.yml",
            ".github/workflows/run-tests.yml",
            ".github/actions/enumerate-tests/**",
            ".github/workflows/build-cli-e2e-image.yml",
            "eng/scripts/generate-specialized-test-projects-list.sh",
            "eng/scripts/build-test-matrix.ps1",
            "eng/scripts/expand-test-matrix-github.ps1",
            "eng/scripts/split-test-matrix-by-deps.ps1",
            "eng/TestEnumerationRunsheetBuilder/**",
            "eng/AfterSolutionBuild.targets",
            "eng/testing/CITestsProperties.props",
            "tests/Directory.Build.targets"
        };

        foreach (var path in sharedPaths)
        {
            Assert.Contains(path, quarantinePaths);
            Assert.Contains(path, outerloopPaths);
        }

        Assert.Contains(".github/workflows/tests-quarantine.yml", quarantinePaths);
        Assert.Contains(".github/workflows/tests-outerloop.yml", outerloopPaths);

        Assert.Equal(
            quarantinePaths.Where(p => p != ".github/workflows/tests-quarantine.yml").Order(StringComparer.Ordinal),
            outerloopPaths.Where(p => p != ".github/workflows/tests-outerloop.yml").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SpecializedRunnerShortCircuitsEmptyProjectListAndScopesRestore()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "specialized-test-runner.yml"));

        Assert.Contains("grep -q '<OverrideProjectToBuild Include='", workflow);
        Assert.Contains("has_projects=false", workflow);
        Assert.Contains("No projects contain ${{ inputs.attributeName }}; generating an empty matrix.", workflow);
        Assert.Contains("artifacts/SpecializedRestore.props", workflow);
        Assert.Contains("OverrideProjectToBuild Include=\"$(RepoRoot)tools/ExtractTestPartitions/ExtractTestPartitions.csproj\"", workflow);

        var restoreStep = System.Text.RegularExpressions.Regex.Match(workflow, "(?ms)^      - name: Restore\\n.*?(?=^      - |\\z)");
        Assert.True(restoreStep.Success, "Could not find the specialized restore step.");
        Assert.Contains("if: ${{ steps.project_list.outputs.has_projects == 'true' }}", restoreStep.Value);
        Assert.Contains("/p:BeforeBuildPropsPath=${{ github.workspace }}/artifacts/SpecializedRestore.props", restoreStep.Value);

        var generateStep = System.Text.RegularExpressions.Regex.Match(workflow, "(?ms)^      - name: Generate canonical test matrix\\n.*?(?=^      - |\\z)");
        Assert.True(generateStep.Success, "Could not find the specialized canonical-matrix step.");
        Assert.Contains("if: ${{ steps.project_list.outputs.has_projects == 'true' }}", generateStep.Value);
    }

    [Fact]
    public void CiFailureTrackerCheckoutDoesNotPinMain()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "ci.yml"));
        var job = System.Text.RegularExpressions.Regex.Match(workflow, "(?ms)^  ci_failure_tracker:\\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\\n|\\z)");
        Assert.True(job.Success, "Could not find the ci_failure_tracker job in ci.yml.");

        var checkout = System.Text.RegularExpressions.Regex.Match(job.Value, "(?ms)^      - uses: actions/checkout@.*?(?=^      - |\\z)");
        Assert.True(checkout.Success, "Could not find the ci_failure_tracker checkout step.");

        // Push CI also runs on release/**. Pinning this checkout to main makes the
        // tracker execute main's reporter instead of the workflow code from the branch
        // whose run is being evaluated.
        Assert.DoesNotContain("ref: main", checkout.Value);
    }

    private static string[] GetPullRequestPaths(string workflow)
    {
        var lines = workflow.Split('\n');
        var pullRequestIndex = Array.FindIndex(lines, static line => line == "  pull_request:");
        Assert.True(pullRequestIndex >= 0, "Could not find pull_request trigger in workflow.");

        var pathsIndex = Array.FindIndex(lines, pullRequestIndex + 1, static line => line == "    paths:");
        Assert.True(pathsIndex >= 0, "Could not find pull_request paths in workflow.");

        return lines
            .Skip(pathsIndex + 1)
            .TakeWhile(static line => line.StartsWith("      - ", StringComparison.Ordinal))
            .Select(static line => line.Trim().TrimStart('-').Trim().Trim('\'', '"'))
            .ToArray();
    }
}
