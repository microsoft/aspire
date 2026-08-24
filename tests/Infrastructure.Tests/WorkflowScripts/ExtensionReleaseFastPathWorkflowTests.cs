// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class ExtensionReleaseFastPathWorkflowTests
{
    private static readonly YamlMappingNode s_testsWorkflow = LoadWorkflow("tests.yml");
    private static readonly YamlMappingNode s_testJobs = Mapping(s_testsWorkflow, "jobs");
    private static readonly YamlMappingNode s_ciWorkflow = LoadWorkflow("ci.yml");
    private static readonly YamlMappingNode s_ciJobs = Mapping(s_ciWorkflow, "jobs");

    [Fact]
    public void WorkflowCallDeclaresReleaseOnlyInputDisabledByDefault()
    {
        var workflowCall = Mapping(Mapping(s_testsWorkflow, "on"), "workflow_call");
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
            var condition = Scalar(Mapping(s_testJobs, jobName), "if") ?? string.Empty;
            Assert.Contains("!inputs.extensionReleaseOnly", condition, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ExtensionUnitTestsRunInlineWhenSetupIsSkippedForReleaseOnlyMode()
    {
        var job = Mapping(s_testJobs, "extension_tests_win");

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
        var steps = Steps(Mapping(s_testJobs, "extension_tests_win"));
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
        var results = Mapping(s_testJobs, "results");
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

    [Fact]
    public void ClassifierReceivesAllIdentityAndRevisionInputs()
    {
        var prepareForCi = Mapping(s_ciJobs, "prepare_for_ci");
        var classifier = Assert.Single(Steps(prepareForCi), step => Scalar(step, "id") == "classify_release_pr");
        var inputs = Mapping(classifier, "with");

        Assert.Equal(
            [
                "actor",
                "author",
                "base_ref",
                "base_sha",
                "head_ref",
                "head_repo",
                "head_sha",
                "repository",
            ],
            inputs.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value).Order());
        Assert.Equal("${{ github.repository }}", Scalar(inputs, "repository"));
        Assert.Equal("${{ github.event.pull_request.base.ref }}", Scalar(inputs, "base_ref"));
        Assert.Equal("${{ github.event.pull_request.head.repo.full_name }}", Scalar(inputs, "head_repo"));
        Assert.Equal("${{ github.event.pull_request.head.ref }}", Scalar(inputs, "head_ref"));
        Assert.Equal("${{ github.event.pull_request.user.login }}", Scalar(inputs, "author"));
        Assert.Equal("${{ github.actor }}", Scalar(inputs, "actor"));
        Assert.Equal("${{ github.event.pull_request.base.sha }}", Scalar(inputs, "base_sha"));
        Assert.Equal("${{ github.event.pull_request.head.sha }}", Scalar(inputs, "head_sha"));
    }

    [Fact]
    public void ClassifierFailureOrEmptyOutputRoutesToNormalCi()
    {
        var prepareForCi = Mapping(s_ciJobs, "prepare_for_ci");
        var classifier = Assert.Single(Steps(prepareForCi), step => Scalar(step, "id") == "classify_release_pr");

        Assert.Equal("true", Scalar(classifier, "continue-on-error"));
        Assert.All(
            Steps(prepareForCi).Where(step => Scalar(step, "id") != "classify_release_pr"),
            step => Assert.False(step.Children.ContainsKey(new YamlScalarNode("continue-on-error"))));
        Assert.Equal(
            "${{ github.event_name == 'pull_request' && steps.classify_release_pr.outputs.is_trusted == 'true' && 'true' || 'false' }}",
            Scalar(Mapping(prepareForCi, "outputs"), "is_trusted_extension_release_pr"));
        Assert.Contains(
            "needs.prepare_for_ci.outputs.is_trusted_extension_release_pr != 'true'",
            Scalar(Mapping(s_ciJobs, "tests"), "if"),
            StringComparison.Ordinal);
        Assert.Contains(
            "needs.prepare_for_ci.outputs.is_trusted_extension_release_pr != 'true'",
            Scalar(Mapping(s_ciJobs, "stabilization_check"), "if"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NormalAndTrustedReleaseTestJobsAreMutuallyExclusive()
    {
        var normalTests = Mapping(s_ciJobs, "tests");
        Assert.Equal("./.github/workflows/tests.yml", Scalar(normalTests, "uses"));
        Assert.Equal(
            "${{ github.repository_owner == 'microsoft' && needs.prepare_for_ci.outputs.skip_workflow != 'true' && needs.prepare_for_ci.outputs.is_trusted_extension_release_pr != 'true' }}",
            Scalar(normalTests, "if"));

        var releaseTests = Mapping(s_ciJobs, "extension_release_tests");
        Assert.Equal("./.github/workflows/tests.yml", Scalar(releaseTests, "uses"));
        Assert.Equal(
            "${{ github.repository_owner == 'microsoft' && needs.prepare_for_ci.outputs.skip_workflow != 'true' && needs.prepare_for_ci.outputs.is_trusted_extension_release_pr == 'true' }}",
            Scalar(releaseTests, "if"));
        Assert.Equal("true", Scalar(Mapping(releaseTests, "with"), "extensionReleaseOnly"));
    }

    [Fact]
    public void StabilizationRunsOnlyOnNormalCiGraph()
    {
        Assert.Equal(
            "${{ github.repository_owner == 'microsoft' && needs.prepare_for_ci.outputs.skip_workflow != 'true' && needs.prepare_for_ci.outputs.is_trusted_extension_release_pr != 'true' }}",
            Scalar(Mapping(s_ciJobs, "stabilization_check"), "if"));
    }

    [Fact]
    public void FinalResultsEnforcesExactlyTheTrustedOrNormalGraph()
    {
        var results = Mapping(s_ciJobs, "results");
        Assert.Equal(
            ["prepare_for_ci", "tests", "stabilization_check", "extension_release_tests"],
            SequenceScalars(results, "needs"));

        var failureStep = Assert.Single(Steps(results), step => Scalar(step, "name") == "Fail if any of the dependent jobs failed");
        Assert.Equal(
            "${{ always() && needs.prepare_for_ci.outputs.skip_workflow != 'true' && " +
            "(contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled') || " +
            "(needs.prepare_for_ci.outputs.is_trusted_extension_release_pr == 'true' && " +
            "(needs.tests.result != 'skipped' || needs.stabilization_check.result != 'skipped' || needs.extension_release_tests.result != 'success')) || " +
            "(needs.prepare_for_ci.outputs.is_trusted_extension_release_pr != 'true' && " +
            "(needs.tests.result != 'success' || needs.stabilization_check.result != 'success' || needs.extension_release_tests.result != 'skipped'))) }}",
            CollapseWhitespace(Scalar(failureStep, "if")));
    }

    [Fact]
    public void CiFailureTrackerPushResultContractIsUnchanged()
    {
        var tracker = Mapping(s_ciJobs, "ci_failure_tracker");

        Assert.Equal(["prepare_for_ci", "tests", "stabilization_check"], SequenceScalars(tracker, "needs"));
        Assert.Equal(
            "${{ always() && github.event_name == 'push' && github.repository_owner == 'microsoft' }}",
            Scalar(tracker, "if"));

        var scriptStep = Assert.Single(Steps(tracker), step => Scalar(step, "name") == "File or close the red-main issue");
        var environment = Mapping(scriptStep, "env");
        Assert.Equal("${{ contains(needs.*.result, 'failure') }}", Scalar(environment, "CI_RED"));
        Assert.Equal(
            "${{ needs.prepare_for_ci.result == 'success' && needs.tests.result == 'success' && needs.stabilization_check.result == 'success' }}",
            CollapseWhitespace(Scalar(environment, "CI_GREEN")));
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode job)
        => ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();

    private static List<string?> SequenceScalars(YamlMappingNode node, string key)
        => ((YamlSequenceNode)node.Children[new YamlScalarNode(key)]).Cast<YamlScalarNode>().Select(item => item.Value).ToList();

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static string CollapseWhitespace(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static YamlMappingNode LoadWorkflow(string workflowName)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(Path.Combine(RepoRoot.Path, ".github", "workflows", workflowName)));
        yaml.Load(reader);

        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }
}
