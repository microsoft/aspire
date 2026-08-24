// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class ExtensionReleaseFastPathWorkflowTests
{
    private static readonly YamlMappingNode s_workflow = LoadWorkflow();
    private static readonly YamlMappingNode s_jobs = Mapping(s_workflow, "jobs");

    [Fact]
    public void WorkflowCallDeclaresReleaseOnlyInputDisabledByDefault()
    {
        var workflowCall = Mapping(Mapping(s_workflow, "on"), "workflow_call");
        var input = Mapping(Mapping(workflowCall, "inputs"), "extensionReleaseOnly");

        Assert.Equal("boolean", Scalar(input, "type"));
        Assert.Equal("false", Scalar(input, "default"));
    }

    [Fact]
    public void ReleaseOnlyModeSkipsSetupAndIndependentArtifactProducers()
    {
        string[] skippedJobs =
        [
            "setup_for_tests",
            "build_packages",
            "build_cli_archive_linux",
            "build_cli_archive_linux_arm64",
            "build_cli_archive_windows",
            "build_cli_archive_windows_arm64",
            "build_cli_archive_macos",
            "build_cli_archive_macos_x64",
        ];

        Assert.All(skippedJobs, jobName =>
        {
            var condition = Scalar(Mapping(s_jobs, jobName), "if") ?? string.Empty;
            Assert.Contains("!inputs.extensionReleaseOnly", condition, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ExtensionUnitTestsRunInlineWhenSetupIsSkippedForReleaseOnlyMode()
    {
        var job = Mapping(s_jobs, "extension_tests_win");

        Assert.False(job.Children.ContainsKey(new YamlScalarNode("uses")));
        Assert.Equal("windows-latest", Scalar(job, "runs-on"));

        var condition = CollapseWhitespace(Scalar(job, "if"));
        Assert.StartsWith("${{ !cancelled() &&", condition, StringComparison.Ordinal);
        Assert.Contains("inputs.extensionReleaseOnly", condition, StringComparison.Ordinal);
        Assert.Contains("needs.setup_for_tests.outputs.run_extension_unit == 'true'", condition, StringComparison.Ordinal);
        Assert.Contains("needs.setup_for_tests.outputs.run_extension_e2e == 'true'", condition, StringComparison.Ordinal);

        var steps = Steps(job);
        Assert.Equal(
            [
                "Checkout code",
                "Setup Node.js environment",
                "Install Corepack",
                "Validate lockfile registries",
                "Install dependencies",
                "Run tests",
                "Override extension version for PR builds",
                "Package VSIX",
                "Assert E2E VSIX contains bridge",
                "Package production VSIX",
                "Assert production VSIX excludes bridge",
                "Upload VSIX",
            ],
            steps.Select(step => Scalar(step, "name")));

        var runTests = Assert.Single(steps, step => Scalar(step, "name") == "Run tests");
        Assert.Equal("corepack yarn test", Scalar(runTests, "run"));
        Assert.False(runTests.Children.ContainsKey(new YamlScalarNode("if")));
    }

    [Fact]
    public void ReleaseOnlyModeDisablesVsixPackagingWithoutChangingNormalPackaging()
    {
        var steps = Steps(Mapping(s_jobs, "extension_tests_win"));
        var overrideVersion = Assert.Single(steps, step => Scalar(step, "name") == "Override extension version for PR builds");
        Assert.Equal(
            "${{ !inputs.extensionReleaseOnly && !cancelled() && inputs.extensionVersionOverride != '' }}",
            Scalar(overrideVersion, "if"));

        string[] packagingSteps =
        [
            "Package VSIX",
            "Assert E2E VSIX contains bridge",
            "Package production VSIX",
            "Assert production VSIX excludes bridge",
            "Upload VSIX",
        ];

        Assert.All(packagingSteps, stepName =>
        {
            var step = Assert.Single(steps, candidate => Scalar(candidate, "name") == stepName);
            Assert.Equal("${{ !inputs.extensionReleaseOnly && !cancelled() }}", Scalar(step, "if"));
        });
    }

    [Fact]
    public void FinalResultsRequiresReleaseUnitTestSuccessAndPreservesNormalSkipChecks()
    {
        var results = Mapping(s_jobs, "results");
        var failureStep = Assert.Single(Steps(results), step => Scalar(step, "name") == "Fail if any dependency failed");
        var condition = CollapseWhitespace(Scalar(failureStep, "if"));

        Assert.Contains("contains(needs.*.result, 'failure')", condition, StringComparison.Ordinal);
        Assert.Contains("contains(needs.*.result, 'cancelled')", condition, StringComparison.Ordinal);
        Assert.Contains(
            "(inputs.extensionReleaseOnly && needs.extension_tests_win.result != 'success')",
            condition,
            StringComparison.Ordinal);
        Assert.Contains(
            "(!inputs.extensionReleaseOnly && ((github.event_name == 'pull_request'",
            condition,
            StringComparison.Ordinal);

        string[] normalModeSkipChecks =
        [
            "needs.extension_tests_win.result == 'skipped'",
            "needs.extension_e2e_tests.result == 'skipped'",
            "needs.cli_starter_validation_linux_x64.result == 'skipped'",
            "needs.cli_starter_validation_linux_arm64.result == 'skipped'",
            "needs.cli_starter_validation_windows_x64.result == 'skipped'",
            "needs.cli_starter_validation_windows_arm64.result == 'skipped'",
            "needs.cli_starter_validation_macos_x64.result == 'skipped'",
            "needs.cli_starter_validation_macos_arm64.result == 'skipped'",
            "needs.typescript_sdk_tests.result == 'skipped'",
            "needs.typescript_api_compat.result == 'skipped'",
            "needs.build_cli_archive_macos_x64.result == 'skipped'",
            "needs.prepare_winget_installer_artifacts.result == 'skipped'",
            "needs.prepare_homebrew_installer_artifacts.result == 'skipped'",
            "needs.nix_package.result == 'skipped'",
            "needs.tests_no_nugets.result == 'skipped'",
            "needs.tests_requires_nugets_linux.result == 'skipped'",
            "needs.tests_requires_nugets_windows.result == 'skipped'",
            "needs.tests_requires_nugets_macos.result == 'skipped'",
            "needs.build_cli_e2e_image.result == 'skipped'",
            "needs.tests_requires_cli_archive.result == 'skipped'",
            "needs.polyglot_validation.result == 'skipped'",
        ];

        Assert.All(normalModeSkipChecks, check => Assert.Contains(check, condition, StringComparison.Ordinal));
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode job)
        => ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static string CollapseWhitespace(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static YamlMappingNode LoadWorkflow()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", "tests.yml")));
        yaml.Load(reader);

        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }
}
