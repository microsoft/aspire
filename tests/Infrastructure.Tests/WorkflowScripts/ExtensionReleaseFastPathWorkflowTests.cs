// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class ExtensionReleaseFastPathWorkflowTests
{
    private static readonly YamlMappingNode s_testsWorkflow = LoadWorkflow("tests.yml");
    private static readonly YamlMappingNode s_testJobs = Mapping(s_testsWorkflow, "jobs");
    private static readonly YamlMappingNode s_extensionUnitWorkflow = LoadWorkflow("extension-unit-tests.yml");
    private static readonly YamlMappingNode s_extensionUnitJobs = Mapping(s_extensionUnitWorkflow, "jobs");
    private static readonly YamlMappingNode s_ciWorkflow = LoadWorkflow("ci.yml");
    private static readonly YamlMappingNode s_ciJobs = Mapping(s_ciWorkflow, "jobs");

    [Fact]
    public void FocusedExtensionWorkflowSupportsOptionalPackaging()
    {
        var workflowCall = Mapping(Mapping(s_extensionUnitWorkflow, "on"), "workflow_call");
        var inputs = Mapping(workflowCall, "inputs");
        var packageVsix = Mapping(inputs, "packageVsix");

        Assert.Equal("boolean", Scalar(packageVsix, "type"));
        Assert.Equal("true", Scalar(packageVsix, "default"));

        var extensionVersionOverride = Mapping(inputs, "extensionVersionOverride");
        Assert.Equal("string", Scalar(extensionVersionOverride, "type"));
        Assert.Equal(string.Empty, Scalar(extensionVersionOverride, "default"));
    }

    [Fact]
    public void FullTestsWorkflowDoesNotExposeReleaseOnlyMode()
    {
        var workflowCall = Mapping(Mapping(s_testsWorkflow, "on"), "workflow_call");
        var inputs = Mapping(workflowCall, "inputs");

        Assert.False(inputs.Children.ContainsKey(new YamlScalarNode("extensionReleaseOnly")));
    }

    [Fact]
    public void FocusedExtensionWorkflowContainsOnlyUnitTests()
    {
        Assert.Equal(
            ["extension_tests_win"],
            s_extensionUnitJobs.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value));
    }

    [Fact]
    public void FocusedExtensionWorkflowRunsUnitTestsAndOwnsPackaging()
    {
        var job = Mapping(s_extensionUnitJobs, "extension_tests_win");

        Assert.False(job.Children.ContainsKey(new YamlScalarNode("uses")));
        Assert.Equal("windows-latest", Scalar(job, "runs-on"));

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
    public void NormalAndReleaseCallersShareFocusedWorkflowWithDifferentPackaging()
    {
        var normalExtensionTests = Mapping(s_testJobs, "extension_tests_win");
        Assert.Equal("./.github/workflows/extension-unit-tests.yml", Scalar(normalExtensionTests, "uses"));
        Assert.Equal(
            "${{ needs.setup_for_tests.outputs.run_extension_unit == 'true' || needs.setup_for_tests.outputs.run_extension_e2e == 'true' }}",
            Scalar(normalExtensionTests, "if"));
        Assert.Equal(["setup_for_tests"], SequenceScalars(normalExtensionTests, "needs"));

        var normalInputs = Mapping(normalExtensionTests, "with");
        Assert.Equal("true", Scalar(normalInputs, "packageVsix"));
        Assert.Equal("${{ inputs.extensionVersionOverride }}", Scalar(normalInputs, "extensionVersionOverride"));

        var releaseExtensionTests = Mapping(s_ciJobs, "extension_release_tests");
        Assert.Equal("./.github/workflows/extension-unit-tests.yml", Scalar(releaseExtensionTests, "uses"));
        Assert.Equal("false", Scalar(Mapping(releaseExtensionTests, "with"), "packageVsix"));
    }

    [Fact]
    public void FocusedWorkflowPackagesAfterTestFailuresOnlyWhenRequested()
    {
        var steps = Steps(Mapping(s_extensionUnitJobs, "extension_tests_win"));
        var overrideVersion = Assert.Single(steps, step => Scalar(step, "name") == "Override extension version for PR builds");
        Assert.Equal(
            "${{ inputs.packageVsix && !cancelled() && inputs.extensionVersionOverride != '' }}",
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
            Assert.Equal("${{ inputs.packageVsix && !cancelled() }}", Scalar(step, "if"));
        });
    }

    [Fact]
    public void FullTestsFinalResultsPreserveNormalSkipChecks()
    {
        var results = Mapping(s_testJobs, "results");
        var failureStep = Assert.Single(Steps(results), step => Scalar(step, "name") == "Fail if any dependency failed");
        var condition = CollapseWhitespace(Scalar(failureStep, "if"));

        Assert.Contains("contains(needs.*.result, 'failure')", condition, StringComparison.Ordinal);
        Assert.Contains("contains(needs.*.result, 'cancelled')", condition, StringComparison.Ordinal);
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
    public void FullTestsWorkflowAlwaysAggregatesTestResults()
    {
        var steps = Steps(Mapping(s_testJobs, "results"));

        Assert.All(
            steps.Where(step => Scalar(step, "name") is "Upload test results" or "Generate test results summary" or "Generate CI timeline"),
            step => Assert.Equal("${{ always() }}", Scalar(step, "if")));
        Assert.All(
            steps.Where(step => Scalar(step, "name") is "Checkout code" or "Create test results directory" || Scalar(step, "uses")?.StartsWith("actions/download-artifact@", StringComparison.Ordinal) == true),
            step => Assert.False(step.Children.ContainsKey(new YamlScalarNode("if"))));
    }

    [Fact]
    public void TestMatrixCallersUseDescriptiveLaneNames()
    {
        Dictionary<string, string> expectedNames = new()
        {
            ["tests_no_nugets"] = "No-package tests",
            ["tests_requires_nugets_linux"] = "Package tests - Linux",
            ["tests_requires_nugets_windows"] = "Package tests - Windows",
            ["tests_requires_nugets_macos"] = "Package tests - macOS",
            ["tests_requires_cli_archive"] = "CLI archive tests",
        };

        Assert.All(
            expectedNames,
            expected => Assert.Equal(expected.Value, Scalar(Mapping(s_testJobs, expected.Key), "name")));
    }

    [Fact]
    public void ReleaseCallerGrantsOnlyFocusedWorkflowPermissions()
    {
        var workflowPermissions = Mapping(s_extensionUnitWorkflow, "permissions");
        var releasePermissions = Mapping(Mapping(s_ciJobs, "extension_release_tests"), "permissions");

        Assert.Equal(["contents"], workflowPermissions.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value));
        Assert.Equal(["contents"], releasePermissions.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value));
        Assert.Equal("read", Scalar(workflowPermissions, "contents"));
        Assert.Equal("read", Scalar(releasePermissions, "contents"));
    }

    [Fact]
    public void NativeCopilotReviewIsPreservedAndReleasePlaceholderIsDocumented()
    {
        Assert.False(File.Exists(RepoPath(".github", "workflows", "copilot-review-dispatch.yml")));

        var skill = File.ReadAllText(RepoPath(".agents", "skills", "code-review", "SKILL.md"));
        string[] releaseFlowMarkers =
        [
            "extension/CHANGELOG.md",
            "extension-release.yml",
            "bot-authored `extension-release/*`",
            "asynchronously replaced",
            "extension-changelog.md",
            "extension-changelog-finalized.yml",
            "outside this exact release flow",
        ];

        Assert.All(releaseFlowMarkers, marker => Assert.Contains(marker, skill, StringComparison.Ordinal));
    }

    [Fact]
    public void ClassifierDescriptionDoesNotReferenceDeletedCopilotDispatcher()
    {
        var action = File.ReadAllText(
            RepoPath(".github", "actions", "is-trusted-extension-release-pr", "action.yml"));

        Assert.DoesNotContain("copilot-review-dispatch.yml", action, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericReviewChecksOnlyStableNonExperimentalAtsBreaks()
    {
        var skill = File.ReadAllText(RepoPath(".agents", "skills", "code-review", "SKILL.md"));
        string[] atsScopeMarkers =
        [
            "Aspire Type System (ATS)",
            "polyglot SDK generation",
            "<SuppressFinalPackageVersion>true</SuppressFinalPackageVersion>",
            "[Experimental]",
            "ATS experimental metadata",
            "dedicated `api-review` skill",
            "general .NET/C# API breaking changes",
        ];

        Assert.All(atsScopeMarkers, marker => Assert.Contains(marker, skill, StringComparison.Ordinal));
        Assert.False(skill.Contains("breaking changes to public API without justification", StringComparison.Ordinal));
    }

    [Fact]
    public void ClassifierRunsFromAdjacentTrustedBaseCheckout()
    {
        var steps = Steps(Mapping(s_ciJobs, "prepare_for_ci"));
        var classifierIndex = steps.FindIndex(step => Scalar(step, "id") == "classify_release_pr");
        var changedFilesIndex = steps.FindIndex(step => Scalar(step, "id") == "check_for_changes");

        Assert.True(classifierIndex > 0);
        Assert.True(changedFilesIndex > classifierIndex);

        var trustedCheckout = steps[classifierIndex - 1];
        Assert.Equal("Checkout trusted extension release classifier", Scalar(trustedCheckout, "name"));
        Assert.Equal("${{ github.event_name == 'pull_request' }}", Scalar(trustedCheckout, "if"));
        Assert.Equal("true", Scalar(trustedCheckout, "continue-on-error"));
        Assert.Equal(
            "actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd",
            Scalar(trustedCheckout, "uses"));

        var checkoutInputs = Mapping(trustedCheckout, "with");
        Assert.Equal("${{ github.event.pull_request.base.sha }}", Scalar(checkoutInputs, "ref"));
        Assert.Equal(".trusted-extension-release-classifier", Scalar(checkoutInputs, "path"));
        Assert.Equal("false", Scalar(checkoutInputs, "persist-credentials"));

        var classifier = steps[classifierIndex];
        Assert.Equal("true", Scalar(classifier, "continue-on-error"));
        Assert.Equal(
            "./.trusted-extension-release-classifier/.github/actions/is-trusted-extension-release-pr",
            Scalar(classifier, "uses"));
    }

    [Fact]
    public void ClassifierReceivesAllIdentityAndRevisionInputs()
    {
        var prepareForCi = Mapping(s_ciJobs, "prepare_for_ci");
        var classifier = Assert.Single(Steps(prepareForCi), step => Scalar(step, "id") == "classify_release_pr");
        var inputs = Mapping(classifier, "with");

        Assert.Equal(
            [
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
        Assert.Equal("${{ github.event.pull_request.base.sha }}", Scalar(inputs, "base_sha"));
        Assert.Equal("${{ github.event.pull_request.head.sha }}", Scalar(inputs, "head_sha"));
    }

    [Fact]
    public void ClassifierFailureOrEmptyOutputRoutesToNormalCi()
    {
        var prepareForCi = Mapping(s_ciJobs, "prepare_for_ci");
        var classifier = Assert.Single(Steps(prepareForCi), step => Scalar(step, "id") == "classify_release_pr");

        Assert.Equal("true", Scalar(classifier, "continue-on-error"));
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
        Assert.Equal("./.github/workflows/extension-unit-tests.yml", Scalar(releaseTests, "uses"));
        Assert.Equal(
            "${{ github.repository_owner == 'microsoft' && needs.prepare_for_ci.outputs.skip_workflow != 'true' && needs.prepare_for_ci.outputs.is_trusted_extension_release_pr == 'true' }}",
            Scalar(releaseTests, "if"));
        Assert.Equal("false", Scalar(Mapping(releaseTests, "with"), "packageVsix"));
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

    private static string RepoPath(params string[] path)
        => Path.Combine([RepoRoot.Path, .. path]);

    private static YamlMappingNode LoadWorkflow(string workflowName)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(RepoPath(".github", "workflows", workflowName)));
        yaml.Load(reader);

        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }
}
