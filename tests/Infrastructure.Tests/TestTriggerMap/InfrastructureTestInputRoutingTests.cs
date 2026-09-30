// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.SelectTests;
using Aspire.TestUtilities;
using Microsoft.Extensions.FileSystemGlobbing;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.TestTriggerMap;

public sealed class InfrastructureTestInputRoutingTests(ITestOutputHelper output)
{
    private const string SemanticInputsWorkflow = ".github/workflows/validate-infrastructure-test-inputs.yml";
    private const string AgenticWorkflow = ".github/workflows/validate-agentic-workflows.yml";
    private const string CiWorkflow = ".github/workflows/ci.yml";
    private const string SemanticInputPatterns = "eng/github-ci/infrastructure-test-input-patterns.txt";
    private const string ClassifierScript = "eng/github-ci/classify-ci-changes.sh";

    private static readonly string[] s_pipelineInputs =
    [
        "eng/pipelines/azure-pipelines.yml",
        "eng/pipelines/common-variables.yml",
        "eng/pipelines/release-publish-nuget.yml",
        "eng/pipelines/templates/build_sign_native.yml",
        "eng/pipelines/templates/prepare-npm-cli-packages.yml",
        "eng/pipelines/templates/prepare-winget-manifest.yml",
        "eng/pipelines/templates/publish-winget.yml",
    ];

    private static readonly string[] s_pipelineAlignmentDocuments =
    [
        "docs/release-process.md",
        "docs/specs/npm-cli-package.md",
    ];

    public static TheoryData<string, string[], bool, bool, bool> ClassificationCases => new()
    {
        {
            "no changed files",
            [],
            false,
            true,
            false
        },
        {
            "semantic infrastructure only",
            ["eng/pipelines/release-publish-nuget.yml"],
            true,
            false,
            false
        },
        {
            "semantic infrastructure plus documentation",
            ["eng/pipelines/release-publish-nuget.yml", "docs/README.md"],
            true,
            false,
            false
        },
        {
            "semantic infrastructure plus source",
            ["eng/pipelines/release-publish-nuget.yml", "src/Aspire.Hosting/ApplicationModel/Resource.cs"],
            true,
            false,
            true
        },
        {
            "unrelated pipeline only",
            ["eng/pipelines/azure-pipelines-public.yml"],
            false,
            true,
            false
        },
        {
            "documentation only",
            ["docs/README.md"],
            false,
            true,
            false
        },
        {
            "generated agentic workflow",
            [".github/workflows/analyze-ci-failure.lock.yml"],
            false,
            true,
            false
        },
    };

    [Fact]
    public void SemanticInputsUseNormalCiInsteadOfADedicatedWorkflow()
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot.Path, SemanticInputsWorkflow)));
    }

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    [RequiresTools(["bash", "jq"])]
    public async Task ChangedFilesProduceExpectedCiAndStabilizationDecisions(
        string _,
        string[] changedFiles,
        bool expectedHasSemanticInputs,
        bool expectedSkipWorkflow,
        bool expectedStabilizationRequired)
    {
        var classification = await ClassifyAsync(changedFiles);

        Assert.Equal(expectedHasSemanticInputs, classification.HasSemanticInputs);
        Assert.Equal(expectedSkipWorkflow, classification.SkipWorkflow);
        Assert.Equal(expectedStabilizationRequired, classification.StabilizationRequired);
    }

    [Fact]
    public void NormalCiGraphUsesSemanticClassification()
    {
        var jobs = WorkflowJobs(CiWorkflow);
        var prepareForCi = Mapping(jobs, "prepare_for_ci");
        var outputs = Mapping(prepareForCi, "outputs");

        Assert.True(outputs.Children.ContainsKey(new YamlScalarNode("has_semantic_inputs")));
        Assert.True(outputs.Children.ContainsKey(new YamlScalarNode("skip_workflow")));
        Assert.True(outputs.Children.ContainsKey(new YamlScalarNode("stabilization_required")));
        Assert.Contains(
            "github.event_name != 'pull_request'",
            Scalar(outputs, "stabilization_required"),
            StringComparison.Ordinal);

        var steps = Sequence(prepareForCi, "steps").Children.Cast<YamlMappingNode>().ToArray();
        Assert.Contains(steps, step => ScalarOrNull(step, "id") == "check_for_changes");
        var semanticInputsStep = Assert.Single(steps, step => ScalarOrNull(step, "id") == "check_for_semantic_inputs");
        Assert.Equal(SemanticInputPatterns, Scalar(Mapping(semanticInputsStep, "with"), "patterns_file"));
        var classifierStep = Assert.Single(steps, step => ScalarOrNull(step, "id") == "classify_changes");
        Assert.Contains(ClassifierScript, Scalar(classifierStep, "run"), StringComparison.Ordinal);

        var testsCondition = Scalar(Mapping(jobs, "tests"), "if");
        Assert.Contains("needs.prepare_for_ci.outputs.skip_workflow != 'true'", testsCondition, StringComparison.Ordinal);

        var stabilizationCondition = Scalar(Mapping(jobs, "stabilization_check"), "if");
        Assert.Contains("needs.prepare_for_ci.outputs.skip_workflow != 'true'", stabilizationCondition, StringComparison.Ordinal);
        Assert.Contains("needs.prepare_for_ci.outputs.stabilization_required == 'true'", stabilizationCondition, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalResultsAcceptOnlyIntentionalStabilizationSkips()
    {
        var jobs = WorkflowJobs(CiWorkflow);
        var results = Mapping(jobs, "results");
        var failureStep = Assert.Single(
            Sequence(results, "steps").Children.Cast<YamlMappingNode>(),
            step => Scalar(step, "name") == "Fail if any of the dependent jobs failed");
        var condition = CollapseWhitespace(Scalar(failureStep, "if"));

        Assert.Contains("needs.tests.result != 'success'", condition, StringComparison.Ordinal);
        Assert.Contains("needs.prepare_for_ci.outputs.stabilization_required == 'true'", condition, StringComparison.Ordinal);
        Assert.Contains("needs.stabilization_check.result != 'success'", condition, StringComparison.Ordinal);

        if (jobs.Children.ContainsKey(new YamlScalarNode("actionlint")))
        {
            Assert.Contains("actionlint", Sequence(results, "needs").Children.Select(node => node.ToString()));
            Assert.Contains("needs.actionlint.result != 'success'", condition, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SemanticPipelineInputsStayAlignedAcrossClassificationAndSelector()
    {
        var expected = s_pipelineInputs.Concat(s_pipelineAlignmentDocuments).Order(StringComparer.Ordinal);
        var classifiedInputs = ReadPatterns(SemanticInputPatterns)
            .Where(IsPipelineSemanticInput)
            .Order(StringComparer.Ordinal);
        var map = TestTriggerMap.Load(RepoRoot.Path);
        var selectorRule = Assert.Single(
            map.PathRules,
            rule => rule.Targets.Contains("test:Infrastructure.Tests", StringComparer.Ordinal)
                && rule.Paths.Contains("eng/pipelines/release-publish-nuget.yml", StringComparer.Ordinal));
        var selectorInputs = selectorRule.Paths
            .Where(IsPipelineSemanticInput)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, classifiedInputs);
        Assert.Equal(expected, selectorInputs);
    }

    [Fact]
    public void EverySemanticInputPatternRoutesToInfrastructureTests()
    {
        var patterns = ReadPatterns(SemanticInputPatterns);
        var files = GitCli.Run(RepoRoot.Path, "ls-files", "--cached", "--others", "--exclude-standard")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var semanticFiles = files
            .Where(path => IsNormalCiSemanticInput(patterns, path))
            .ToArray();
        var map = TestTriggerMap.Load(RepoRoot.Path);

        Assert.All(
            patterns,
            pattern => Assert.Contains(semanticFiles, path => TestTriggerMap.GlobMatches(pattern, path)));
        Assert.All(
            semanticFiles,
            path => Assert.Contains(
                map.PathRules,
                rule => rule.Targets.Contains("test:Infrastructure.Tests", StringComparer.Ordinal)
                    && rule.Paths.Any(pattern => TestTriggerMap.GlobMatches(pattern, path))));
    }

    [Fact]
    public void MainSelectorPrefilterKeepsOnlyConsumedPipelineInputs()
    {
        var map = TriggerMap.Load(Path.Combine(RepoRoot.Path, "eng", "github-ci", "test-trigger-map.yml"));
        var filter = ChangedFileFilter.Create(RepoRoot.Path, map.Prefilter);

        Assert.False(filter.IsExcluded("eng/pipelines/release-publish-nuget.yml"));
        Assert.False(filter.IsExcluded("docs/specs/npm-cli-package.md"));
        Assert.True(filter.IsExcluded("eng/pipelines/azure-pipelines-public.yml"));
        Assert.True(filter.IsExcluded("eng/pipelines/templates/BuildAndTest.yml"));
        Assert.True(filter.IsExcluded("eng/Publishing.props"));
    }

    [Theory]
    [InlineData("eng/pipelines/release-publish-nuget.yml")]
    [InlineData("docs/specs/npm-cli-package.md")]
    [InlineData(".github/workflows/backport.yml")]
    [InlineData("eng/github-ci/classify-ci-changes.sh")]
    public void SemanticInfrastructureInputsSelectOnlyInfrastructureTests(string path)
    {
        var selection = SelectWithRealMap(path);

        Assert.False(selection.SelectsAll);
        Assert.Equal(["Infrastructure.Tests"], selection.TestProjects);
        Assert.Empty(selection.Jobs);
    }

    [Theory]
    [InlineData("eng/pipelines/azure-pipelines-public.yml")]
    [InlineData("docs/README.md")]
    [InlineData("eng/Publishing.props")]
    public void InputsWithoutSemanticConsumersSelectNoNormalCiTargets(string path)
    {
        var selection = SelectWithRealMap(path);

        Assert.False(selection.SelectsAll);
        Assert.Empty(selection.TestProjects);
        Assert.Empty(selection.Jobs);
    }

    [Fact]
    public void EverySkippedRootWorkflowIsOwnedByNormalCiOrAgenticValidation()
    {
        var skipPatterns = ReadPatterns("eng/github-ci/ci-skip-entirely-patterns.txt");
        var semanticPatterns = ReadPatterns(SemanticInputPatterns);
        var workflowFiles = Directory.EnumerateFiles(Path.Combine(RepoRoot.Path, ".github", "workflows"), "*.yml")
            .Select(path => Path.GetRelativePath(RepoRoot.Path, path).Replace('\\', '/'))
            .Where(path => MatchesAny(skipPatterns, path))
            .ToArray();

        Assert.NotEmpty(workflowFiles);
        Assert.All(
            workflowFiles,
            path => Assert.True(
                IsNormalCiSemanticInput(semanticPatterns, path) || WorkflowRunsFor(AgenticWorkflow, path),
                $"{path} is skipped by the main CI gate but has no semantic validation owner."));
    }

    [Fact]
    public async Task GeneratedWorkflowInputsRemainOwnedByAgenticLane()
    {
        const string generatedWorkflow = ".github/workflows/analyze-ci-failure.lock.yml";

        Assert.True(WorkflowRunsFor(AgenticWorkflow, generatedWorkflow));
        var classification = await ClassifyAsync([generatedWorkflow]);
        Assert.True(classification.SkipWorkflow);
    }

    [Fact]
    public void GeneratedWorkflowConsumersRunInAgenticLane()
    {
        Type[] consumers =
        [
            typeof(AgenticWorkflowTests),
            typeof(AnalyzeCiFailureWorkflowTests),
            typeof(ExtensionReleaseWorkflowTests),
            typeof(PrDocsCheckWorkflowTests),
            typeof(ValidateAgenticWorkflowsTests),
        ];

        Assert.All(consumers, type =>
        {
            var traits = type.GetCustomAttributesData()
                .Where(attribute => attribute.AttributeType.FullName == "Xunit.TraitAttribute")
                .Select(attribute => attribute.ConstructorArguments.Select(argument => argument.Value as string).ToArray());

            Assert.Contains(traits, values => values is ["Category", "AgenticWorkflow"]);
        });
    }

    private async Task<CiClassification> ClassifyAsync(IReadOnlyList<string> changedFiles)
    {
        var skipPatterns = ReadPatterns("eng/github-ci/ci-skip-entirely-patterns.txt");
        var semanticPatterns = ReadPatterns(SemanticInputPatterns);
        var skippableFiles = changedFiles.Where(path => MatchesAny(skipPatterns, path)).ToArray();
        var semanticFiles = changedFiles.Where(path => MatchesAny(semanticPatterns, path)).ToArray();
        var result = await ProcessRunner.RunAsync(
            output,
            "bash",
            [
                Path.Combine(RepoRoot.Path, ClassifierScript),
                JsonSerializer.Serialize(changedFiles),
                JsonSerializer.Serialize(skippableFiles),
                JsonSerializer.Serialize(semanticFiles),
            ],
            RepoRoot.Path);

        Assert.Equal(0, result.ExitCode);
        var outputs = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        return new CiClassification(
            bool.Parse(outputs["has_semantic_inputs"]),
            bool.Parse(outputs["skip_workflow"]),
            bool.Parse(outputs["stabilization_required"]));
    }

    private static bool WorkflowRunsFor(string relativePath, params string[] changedPaths)
    {
        var matcher = new Matcher(StringComparison.Ordinal, preserveFilterOrder: true);
        foreach (var path in WorkflowPaths(relativePath))
        {
            if (path.StartsWith('!'))
            {
                matcher.AddExclude(path[1..]);
            }
            else
            {
                matcher.AddInclude(path);
            }
        }

        return matcher.Match(changedPaths).HasMatches;
    }

    private static IReadOnlyList<string> WorkflowPaths(string relativePath)
    {
        var workflowPath = Path.Combine(RepoRoot.Path, relativePath);
        Assert.True(File.Exists(workflowPath), $"Expected workflow file at '{relativePath}'.");

        using var reader = File.OpenText(workflowPath);
        var yaml = new YamlStream();
        yaml.Load(reader);

        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var on = Mapping(root, "on");
        var pullRequest = Mapping(on, "pull_request");
        return Sequence(pullRequest, "paths").Children.Select(path => path.ToString()).ToArray();
    }

    private static YamlMappingNode WorkflowJobs(string relativePath)
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, relativePath));
        var yaml = new YamlStream();
        yaml.Load(reader);

        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        return Mapping(root, "jobs");
    }

    private static IReadOnlyList<string> ReadPatterns(string relativePath)
    {
        var path = Path.Combine(RepoRoot.Path, relativePath);
        Assert.True(File.Exists(path), $"Expected patterns file at '{relativePath}'.");
        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }

    private static bool MatchesAny(IEnumerable<string> patterns, string path)
        => patterns.Any(pattern => TestTriggerMap.GlobMatches(pattern, path));

    private static bool IsNormalCiSemanticInput(IEnumerable<string> patterns, string path)
        => !path.EndsWith(".lock.yml", StringComparison.Ordinal)
            && !Path.GetFileName(path).StartsWith("agentics-maintenance", StringComparison.Ordinal)
            && MatchesAny(patterns, path);

    private static bool IsPipelineSemanticInput(string path)
        => path.StartsWith("eng/pipelines/", StringComparison.Ordinal)
            || s_pipelineAlignmentDocuments.Contains(path, StringComparer.Ordinal);

    private static SelectionResult SelectWithRealMap(params string[] paths)
    {
        var projectPaths = Regex.Matches(
                File.ReadAllText(Path.Combine(RepoRoot.Path, "Aspire.slnx")),
                "Path=\"([^\"]+\\.csproj)\"")
            .Select(match => match.Groups[1].Value.Replace('\\', '/'))
            .ToList();
        var testProjects = projectPaths
            .Where(projectPath => projectPath.StartsWith("tests/", StringComparison.Ordinal))
            .Select(projectPath => Path.GetFileNameWithoutExtension(projectPath)!)
            .Where(name => name.EndsWith(".Tests", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var projectDirectories = projectPaths
            .Select(projectPath => Path.GetDirectoryName(projectPath)!.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        var mapPath = Path.Combine(RepoRoot.Path, "eng", "github-ci", "test-trigger-map.yml");
        var map = TriggerMap.Load(mapPath);
        var filter = ChangedFileFilter.Create(RepoRoot.Path, map.Prefilter);
        var filteredPaths = paths.Where(path => !filter.IsExcluded(path)).ToArray();
        var selector = new TestSelector(
            mapPath,
            testProjects,
            projectDirectories,
            new HashSet<string>(StringComparer.Ordinal));

        return selector.Select(filteredPaths, [], new SelectorOptions());
    }

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
        => Assert.IsType<YamlMappingNode>(parent.Children[new YamlScalarNode(key)]);

    private static YamlSequenceNode Sequence(YamlMappingNode parent, string key)
        => Assert.IsType<YamlSequenceNode>(parent.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode parent, string key)
        => Assert.IsType<YamlScalarNode>(parent.Children[new YamlScalarNode(key)]).Value!;

    private static string? ScalarOrNull(YamlMappingNode parent, string key)
        => parent.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? Assert.IsType<YamlScalarNode>(value).Value
            : null;

    private static string CollapseWhitespace(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private readonly record struct CiClassification(
        bool HasSemanticInputs,
        bool SkipWorkflow,
        bool StabilizationRequired);
}
