// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.SelectTests;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.TestTriggerMap;

public sealed class SelectiveArtifactBuildWorkflowTests
{
    private static readonly IReadOnlyDictionary<string, string> s_producerRequiredOutputs =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["build_packages"] = "build_packages_required",
            ["build_cli_archive_linux"] = "build_cli_archive_linux_required",
            ["build_cli_archive_linux_arm64"] = "build_cli_archive_linux_arm64_required",
            ["build_cli_archive_windows"] = "build_cli_archive_windows_required",
            ["build_cli_archive_windows_arm64"] = "build_cli_archive_windows_arm64_required",
            ["build_cli_archive_macos"] = "build_cli_archive_macos_required",
            ["build_cli_archive_macos_x64"] = "build_cli_archive_macos_x64_required",
        };

    public static TheoryData<string, string[], string[]> RepresentativeSelections => new()
    {
        {
            "package tests on Linux",
            ["tests_matrix_requires_nugets_linux"],
            ["build_packages", "build_cli_archive_linux"]
        },
        {
            "package tests on Windows",
            ["tests_matrix_requires_nugets_windows"],
            ["build_packages", "build_cli_archive_windows"]
        },
        {
            "package tests on macOS",
            ["tests_matrix_requires_nugets_macos"],
            ["build_packages", "build_cli_archive_macos"]
        },
        {
            "CLI archive tests",
            ["tests_matrix_requires_cli_archive"],
            ["build_packages", "build_cli_archive_linux"]
        },
        {
            "WinGet installer",
            ["run_winget_installer"],
            ["build_packages", "build_cli_archive_windows", "build_cli_archive_windows_arm64"]
        },
        {
            "Homebrew installer",
            ["run_homebrew_installer"],
            ["build_packages", "build_cli_archive_macos", "build_cli_archive_macos_x64"]
        },
        {
            "extension E2E",
            ["run_extension_e2e"],
            ["build_packages", "build_cli_archive_linux", "build_cli_archive_windows"]
        },
        {
            "native dashboard validation",
            ["run_native_dashboard_validation"],
            [
                "build_cli_archive_linux",
                "build_cli_archive_linux_arm64",
                "build_cli_archive_windows",
                "build_cli_archive_windows_arm64",
                "build_cli_archive_macos",
                "build_cli_archive_macos_x64",
            ]
        },
        {
            "CLI starter validation",
            ["run_cli_starter_validation"],
            [
                "build_packages",
                "build_cli_archive_linux",
                "build_cli_archive_linux_arm64",
                "build_cli_archive_windows",
                "build_cli_archive_windows_arm64",
                "build_cli_archive_macos",
                "build_cli_archive_macos_x64",
            ]
        },
    };

    public static TheoryData<string, string[]> RealSelectorJobFixtures => new()
    {
        {
            ".github/workflows/prepare-installer-artifacts.yml",
            [
                "build_packages",
                "build_cli_archive_windows",
                "build_cli_archive_windows_arm64",
                "build_cli_archive_macos",
                "build_cli_archive_macos_x64",
            ]
        },
        {
            ".github/workflows/extension-e2e-tests.yml",
            ["build_packages", "build_cli_archive_linux", "build_cli_archive_windows"]
        },
        {
            ".github/workflows/native-dashboard-validation.yml",
            [
                "build_cli_archive_linux",
                "build_cli_archive_linux_arm64",
                "build_cli_archive_windows",
                "build_cli_archive_windows_arm64",
                "build_cli_archive_macos",
                "build_cli_archive_macos_x64",
            ]
        },
    };

    [Fact]
    public void InfrastructureOnlySelectionRequiresNoArtifactProducers()
    {
        var selection = SelectWithRealMap(".github/workflows/ci.yml");

        Assert.False(selection.SelectsAll);
        Assert.Equal(["Infrastructure.Tests"], selection.TestProjects);
        Assert.Empty(selection.Jobs);

        var activeInputs = new HashSet<string>(StringComparer.Ordinal)
        {
            "tests_matrix_no_nugets",
        };

        Assert.Empty(RequiredProducers(activeInputs));
    }

    [Theory]
    [MemberData(nameof(RepresentativeSelections))]
    public void RepresentativeSelectionsRequireExactArtifactProducers(
        string _,
        string[] activeInputs,
        string[] expectedProducers)
    {
        Assert.Equal(
            expectedProducers.Order(StringComparer.Ordinal),
            RequiredProducers(activeInputs).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(RealSelectorJobFixtures))]
    public void RealSelectorJobFixturesRequireExactArtifactProducers(
        string changedPath,
        string[] expectedProducers)
    {
        var selection = SelectWithRealMap(changedPath);
        var activeInputs = selection.Jobs
            .Select(job => "run_" + job["job:".Length..].Replace('-', '_'))
            .ToList();

        Assert.Contains("Infrastructure.Tests", selection.TestProjects);
        Assert.Equal(
            expectedProducers.Order(StringComparer.Ordinal),
            RequiredProducers(activeInputs).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProducerRequirementsMatchEverySelectedDownstreamConsumer()
    {
        var jobs = WorkflowJobs();
        var setupOutputs = Mapping(Mapping(jobs, "setup_for_tests"), "outputs");

        foreach (var (producerId, requiredOutput) in s_producerRequiredOutputs)
        {
            var requiredInputs = SelectionInputs(Scalar(setupOutputs, requiredOutput));
            var consumerInputs = jobs.Children
                .Select(entry => (
                    Id: Assert.IsType<YamlScalarNode>(entry.Key).Value!,
                    Job: Assert.IsType<YamlMappingNode>(entry.Value)))
                .Where(entry => entry.Id != "results" && Needs(entry.Job).Contains(producerId, StringComparer.Ordinal))
                .SelectMany(entry =>
                {
                    var inputs = SelectionInputs(Scalar(entry.Job, "if"));
                    Assert.True(
                        inputs.Count > 0,
                        $"Artifact consumer '{entry.Id}' has no structured selector or split-matrix input.");
                    return inputs;
                })
                .ToHashSet(StringComparer.Ordinal);

            Assert.NotEmpty(consumerInputs);
            Assert.Equal(
                consumerInputs.Order(StringComparer.Ordinal),
                requiredInputs.Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void ArtifactProducersDependOnSetupAndUseTheirRequiredOutput()
    {
        var jobs = WorkflowJobs();

        foreach (var (producerId, requiredOutput) in s_producerRequiredOutputs)
        {
            var producer = Mapping(jobs, producerId);

            Assert.Equal(["setup_for_tests"], Needs(producer));
            Assert.Equal(
                $"${{{{ needs.setup_for_tests.outputs.{requiredOutput} == 'true' }}}}",
                Scalar(producer, "if"));
        }
    }

    [Fact]
    public void FinalResultsRejectOnlyRequiredProducerSkips()
    {
        var jobs = WorkflowJobs();
        var results = Mapping(jobs, "results");
        var failureStep = Assert.Single(
            Sequence(results, "steps").Cast<YamlMappingNode>(),
            step => Scalar(step, "name") == "Fail if any dependency failed");
        var condition = CollapseWhitespace(Scalar(failureStep, "if"));

        Assert.Contains("contains(needs.*.result, 'failure')", condition, StringComparison.Ordinal);
        Assert.Contains("contains(needs.*.result, 'cancelled')", condition, StringComparison.Ordinal);

        foreach (var (producerId, requiredOutput) in s_producerRequiredOutputs)
        {
            var skipCheck = $"needs.{producerId}.result == 'skipped'";
            var guardedSkipCheck =
                $"({skipCheck} && needs.setup_for_tests.outputs.{requiredOutput} == 'true')";

            Assert.Contains(guardedSkipCheck, condition, StringComparison.Ordinal);
            Assert.Equal(1, condition.Split(skipCheck, StringSplitOptions.None).Length - 1);
        }
    }

    private static IReadOnlyCollection<string> RequiredProducers(IEnumerable<string> activeInputs)
    {
        var active = activeInputs.ToHashSet(StringComparer.Ordinal);
        var outputs = Mapping(Mapping(WorkflowJobs(), "setup_for_tests"), "outputs");

        return s_producerRequiredOutputs
            .Where(entry => SelectionInputs(Scalar(outputs, entry.Value)).Overlaps(active))
            .Select(entry => entry.Key)
            .ToList();
    }

    private static HashSet<string> SelectionInputs(string? expression)
    {
        var inputs = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(expression))
        {
            return inputs;
        }

        foreach (Match match in Regex.Matches(expression, @"\btests_matrix_[a-z0-9_]+\b|\brun_[a-z0-9_]+\b"))
        {
            inputs.Add(match.Value);
        }

        return inputs;
    }

    private static SelectionResult SelectWithRealMap(string path)
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
        var selector = new TestSelector(
            Path.Combine(RepoRoot.Path, "eng", "github-ci", "test-trigger-map.yml"),
            testProjects,
            projectDirectories,
            new HashSet<string>(StringComparer.Ordinal));

        return selector.Select([path], [], new SelectorOptions());
    }

    private static YamlMappingNode WorkflowJobs()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(
            Path.Combine(RepoRoot.Path, ".github", "workflows", "tests.yml")));
        yaml.Load(reader);

        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        return Mapping(root, "jobs");
    }

    private static IReadOnlyList<string> Needs(YamlMappingNode job)
    {
        if (!job.Children.TryGetValue(new YamlScalarNode("needs"), out var needs))
        {
            return [];
        }

        return needs switch
        {
            YamlScalarNode scalar => [scalar.Value!],
            YamlSequenceNode sequence => sequence.Children
                .Cast<YamlScalarNode>()
                .Select(node => node.Value!)
                .ToList(),
            _ => throw new InvalidOperationException($"Unexpected needs node type: {needs.GetType().Name}"),
        };
    }

    private static YamlMappingNode Mapping(YamlMappingNode parent, string key)
        => Assert.IsType<YamlMappingNode>(parent.Children[new YamlScalarNode(key)]);

    private static YamlSequenceNode Sequence(YamlMappingNode parent, string key)
        => Assert.IsType<YamlSequenceNode>(parent.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode parent, string key)
        => parent.Children.TryGetValue(new YamlScalarNode(key), out var node)
            ? Assert.IsType<YamlScalarNode>(node).Value
            : null;

    private static string CollapseWhitespace(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
