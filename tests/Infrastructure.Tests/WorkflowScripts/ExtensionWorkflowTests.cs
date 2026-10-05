// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class ExtensionWorkflowTests
{
    private static readonly YamlMappingNode s_testsWorkflow = LoadWorkflow("tests.yml");
    private static readonly YamlMappingNode s_testJobs = Mapping(s_testsWorkflow, "jobs");
    private static readonly YamlMappingNode s_extensionUnitWorkflow = LoadWorkflow("extension-unit-tests.yml");
    private static readonly YamlMappingNode s_extensionUnitJobs = Mapping(s_extensionUnitWorkflow, "jobs");
    private static readonly YamlMappingNode s_ciJobs = Mapping(LoadWorkflow("ci.yml"), "jobs");
    private static readonly string s_extensionPackageManager = LoadExtensionPackageManager();

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
    public void FocusedExtensionWorkflowTestsBeforePublishingVsix()
    {
        var job = Mapping(s_extensionUnitJobs, "extension_tests_win");
        Assert.Equal("windows-latest", Scalar(job, "runs-on"));

        var steps = Steps(job);
        var testIndex = StepIndex(steps, step => InvokesPackageScript(step, "test"));
        var e2ePackageIndex = StepIndex(
            steps,
            step => InvokesVscePackage(step, "out/aspire-extension.vsix"));
        var e2eValidationIndex = StepIndex(
            steps,
            step => InvokesVsixValidation(step, "out/aspire-extension.vsix", "Present"));
        var productionPackageIndex = StepIndex(
            steps,
            step => InvokesVscePackage(step, "out/aspire-extension-production.vsix"));
        var productionValidationIndex = StepIndex(
            steps,
            step => InvokesVsixValidation(step, "out/aspire-extension-production.vsix", "Absent"));
        var uploadIndex = StepIndex(
            steps,
            step => UploadsArtifact(step, "aspire-extension", "extension/out/aspire-extension.vsix"));

        Assert.False(steps[testIndex].Children.ContainsKey(new YamlScalarNode("if")));
        Assert.Equal("true", Scalar(Mapping(steps[e2ePackageIndex], "env"), "ASPIRE_EXTENSION_E2E_INCLUDE_BRIDGE"));

        var uploadInputs = Mapping(steps[uploadIndex], "with");
        Assert.Equal("aspire-extension", Scalar(uploadInputs, "name"));
        Assert.Equal("extension/out/aspire-extension.vsix", Scalar(uploadInputs, "path"));

        Assert.True(testIndex < e2ePackageIndex, "The E2E VSIX must be packaged after the unit tests execute.");
        Assert.True(e2ePackageIndex < e2eValidationIndex, "The E2E VSIX must be validated after it is packaged.");
        Assert.True(e2eValidationIndex < productionPackageIndex, "The production VSIX must be packaged after E2E validation.");
        Assert.True(productionPackageIndex < productionValidationIndex, "The production VSIX must be validated after it is packaged.");
        Assert.True(productionValidationIndex < uploadIndex, "The E2E VSIX must be uploaded only after both VSIX variants are validated.");
    }

    [Fact]
    public void NormalTestsUseFocusedExtensionWorkflowWithPackaging()
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
    }

    [Fact]
    public void FocusedWorkflowPackagesAfterTestFailuresOnlyWhenRequested()
    {
        var steps = Steps(Mapping(s_extensionUnitJobs, "extension_tests_win"));
        var versionOverride = Assert.Single(
            steps,
            step => InvokesPackageScript(step, "version"));
        Assert.Equal(
            "${{ inputs.packageVsix && !cancelled() && inputs.extensionVersionOverride != '' }}",
            Scalar(versionOverride, "if"));

        var packagingSteps = new[]
        {
            steps[StepIndex(steps, step => InvokesVscePackage(step, "out/aspire-extension.vsix"))],
            steps[StepIndex(steps, step => InvokesVsixValidation(step, "out/aspire-extension.vsix", "Present"))],
            steps[StepIndex(steps, step => InvokesVscePackage(step, "out/aspire-extension-production.vsix"))],
            steps[StepIndex(steps, step => InvokesVsixValidation(step, "out/aspire-extension-production.vsix", "Absent"))],
            steps[StepIndex(
                steps,
                step => UploadsArtifact(step, "aspire-extension", "extension/out/aspire-extension.vsix"))],
        };

        Assert.All(
            packagingSteps,
            step => Assert.Equal("${{ inputs.packageVsix && !cancelled() }}", Scalar(step, "if")));
    }

    [Fact]
    public void FullTestsFinalResultsRejectFailedCancelledOrSkippedExtensionJobs()
    {
        var results = Mapping(s_testJobs, "results");
        var failureStep = Assert.Single(
            Steps(results),
            step => Scalar(step, "run")?.Contains("exit 1", StringComparison.Ordinal) == true);
        var condition = CollapseWhitespace(Scalar(failureStep, "if"));

        Assert.Contains("contains(needs.*.result, 'failure')", condition, StringComparison.Ordinal);
        Assert.Contains("contains(needs.*.result, 'cancelled')", condition, StringComparison.Ordinal);
        Assert.Contains(
            "(needs.extension_tests_win.result == 'skipped' && (needs.setup_for_tests.outputs.run_extension_unit == 'true' || needs.setup_for_tests.outputs.run_extension_e2e == 'true'))",
            condition,
            StringComparison.Ordinal);
        Assert.Contains(
            "(needs.setup_for_tests.outputs.run_extension_e2e == 'true' && needs.extension_e2e_tests.result == 'skipped')",
            condition,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FullTestsWorkflowAlwaysAggregatesTestResults()
    {
        var steps = Steps(Mapping(s_testJobs, "results"));
        string[] logPatterns =
        [
            "logs-*-macos-latest",
            "logs-*-ubuntu-latest",
            "logs-*-windows-latest",
        ];
        Assert.All(logPatterns, pattern =>
        {
            var download = Assert.Single(
                steps,
                step => Scalar(step, "uses")?.StartsWith("actions/download-artifact@", StringComparison.Ordinal) == true &&
                        Scalar(Mapping(step, "with"), "pattern") == pattern);
            Assert.False(download.Children.ContainsKey(new YamlScalarNode("if")));
        });

        var upload = Assert.Single(
            steps,
            step => Scalar(step, "uses")?.StartsWith("actions/upload-artifact@", StringComparison.Ordinal) == true &&
                    Scalar(Mapping(step, "with"), "name") == "All-TestResults");
        Assert.Equal("All-TestResults", Scalar(Mapping(upload, "with"), "name"));
        Assert.Equal("${{ github.workspace }}/testresults/**/*.trx", Scalar(Mapping(upload, "with"), "path"));
        Assert.Equal("${{ always() }}", Scalar(upload, "if"));

        string[] reportProjects =
        [
            "tools/GenerateTestSummary/GenerateTestSummary.csproj",
            "tools/GenerateCITimeline/GenerateCITimeline.csproj",
        ];
        Assert.All(reportProjects, project =>
        {
            var step = Assert.Single(
                steps,
                candidate => Scalar(candidate, "run")?.Contains(project, StringComparison.Ordinal) == true);
            Assert.Equal("${{ always() }}", Scalar(step, "if"));
        });
    }

    [Fact]
    public void FocusedWorkflowUsesReadOnlyContentsPermission()
    {
        var workflowPermissions = Mapping(s_extensionUnitWorkflow, "permissions");

        Assert.Equal(["contents"], workflowPermissions.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value));
        Assert.Equal("read", Scalar(workflowPermissions, "contents"));
    }

    [Fact]
    public void CiRunsSelectorDrivenTestsAndStabilizationForEveryRequiredPr()
    {
        var normalTests = Mapping(s_ciJobs, "tests");
        Assert.Equal("./.github/workflows/tests.yml", Scalar(normalTests, "uses"));
        Assert.Equal(
            "${{ github.repository_owner == 'microsoft' && needs.prepare_for_ci.outputs.skip_workflow != 'true' }}",
            Scalar(normalTests, "if"));

        Assert.Equal(
            "${{ github.repository_owner == 'microsoft' && needs.prepare_for_ci.outputs.skip_workflow != 'true' }}",
            Scalar(Mapping(s_ciJobs, "stabilization_check"), "if"));
    }

    [Fact]
    public void FinalResultsRequireSelectorDrivenTestsAndStabilization()
    {
        var results = Mapping(s_ciJobs, "results");
        Assert.Equal(
            ["prepare_for_ci", "tests", "stabilization_check"],
            SequenceScalars(results, "needs"));

        var failureStep = Assert.Single(
            Steps(results),
            step => Scalar(step, "run")?.Contains("exit 1", StringComparison.Ordinal) == true);
        Assert.Equal(
            "${{ always() && needs.prepare_for_ci.outputs.skip_workflow != 'true' && " +
            "(contains(needs.*.result, 'failure') || contains(needs.*.result, 'cancelled') || " +
            "needs.tests.result != 'success' || needs.stabilization_check.result != 'success') }}",
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

        var scriptStep = Assert.Single(
            Steps(tracker),
            step => Scalar(step, "uses")?.StartsWith("actions/github-script@", StringComparison.Ordinal) == true);
        var environment = Mapping(scriptStep, "env");
        Assert.Equal("${{ contains(needs.*.result, 'failure') }}", Scalar(environment, "CI_RED"));
        Assert.Equal(
            "${{ needs.prepare_for_ci.result == 'success' && needs.tests.result == 'success' && needs.stabilization_check.result == 'success' }}",
            CollapseWhitespace(Scalar(environment, "CI_GREEN")));
    }

    private static List<YamlMappingNode> Steps(YamlMappingNode job)
        => ((YamlSequenceNode)job.Children[new YamlScalarNode("steps")]).Cast<YamlMappingNode>().ToList();

    private static int StepIndex(
        IReadOnlyList<YamlMappingNode> steps,
        Func<YamlMappingNode, bool> predicate)
        => Assert.Single(
            steps.Select((step, index) => (Step: step, Index: index)),
            candidate => predicate(candidate.Step)).Index;

    private static bool InvokesPackageScript(YamlMappingNode step, string script)
        => CommandTokenLines(step).Any(tokens =>
        {
            var commandStart = tokens is ["&", ..] ? 1 : 0;
            if (!StartsWithTokens(tokens, commandStart, "corepack", s_extensionPackageManager))
            {
                return false;
            }

            var scriptIndex = commandStart + 2;
            if (tokens.ElementAtOrDefault(scriptIndex) == "run")
            {
                scriptIndex++;
            }

            return tokens.ElementAtOrDefault(scriptIndex) == script;
        });

    private static bool InvokesVscePackage(YamlMappingNode step, string outputPath)
        => CommandTokenLines(step).Any(tokens =>
        {
            var commandStart = tokens is ["&", ..] ? 1 : 0;
            return StartsWithTokens(
                    tokens,
                    commandStart,
                    "corepack",
                    s_extensionPackageManager,
                    "run",
                    "vsce",
                    "package") &&
                ContainsTokenSequence(tokens, "-o", outputPath);
        });

    private static bool InvokesVsixValidation(YamlMappingNode step, string vsixPath, string expected)
        => CommandTokenLines(step).Any(tokens =>
        {
            var commandStart = tokens is ["&", ..] ? 1 : 0;
            var executable = tokens.ElementAtOrDefault(commandStart)?.Replace('\\', '/');
            return executable?.EndsWith(
                    "/assert-extension-e2e-bridge-vsix.ps1",
                    StringComparison.Ordinal) == true &&
                ContainsTokenSequence(tokens, "-VsixPath", vsixPath) &&
                ContainsTokenSequence(tokens, "-Expected", expected);
        });

    private static bool UploadsArtifact(YamlMappingNode step, string name, string path)
    {
        var uses = Scalar(step, "uses");
        var versionSeparator = uses?.IndexOf('@', StringComparison.Ordinal) ?? -1;
        if (versionSeparator < 0 ||
            uses![..versionSeparator] != "actions/upload-artifact" ||
            !step.Children.TryGetValue(new YamlScalarNode("with"), out var inputsNode) ||
            inputsNode is not YamlMappingNode inputs)
        {
            return false;
        }

        return Scalar(inputs, "name") == name && Scalar(inputs, "path") == path;
    }

    private static IEnumerable<string[]> CommandTokenLines(YamlMappingNode step)
    {
        var run = Scalar(step, "run");
        if (run is null)
        {
            yield break;
        }

        foreach (var rawLine in run.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            yield return line
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(Unquote)
                .ToArray();
        }
    }

    private static bool StartsWithTokens(string[] tokens, int startIndex, params string[] expected)
        => tokens.Length >= startIndex + expected.Length &&
            tokens.AsSpan(startIndex, expected.Length).SequenceEqual(expected);

    private static bool ContainsTokenSequence(string[] tokens, params string[] expected)
    {
        for (var startIndex = 0; startIndex <= tokens.Length - expected.Length; startIndex++)
        {
            if (StartsWithTokens(tokens, startIndex, expected))
            {
                return true;
            }
        }

        return false;
    }

    private static string Unquote(string token)
        => token is ['"', .., '"'] or ['\'', .., '\'']
            ? token[1..^1]
            : token;

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

    private static string LoadExtensionPackageManager()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(RepoPath("extension", "package.json")));
        var declaration = document.RootElement.GetProperty("packageManager").GetString();
        Assert.NotNull(declaration);

        return declaration[..declaration.IndexOf('@', StringComparison.Ordinal)];
    }

    private static YamlMappingNode LoadWorkflow(string workflowName)
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(RepoPath(".github", "workflows", workflowName)));
        yaml.Load(reader);

        return Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
    }
}
