// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.FileSystemGlobbing;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Guards on the CI wiring that surrounds the SelectTests engine but lives in YAML rather than C#:
/// the <c>run-full-ci</c> label kill switch (computed in <c>.github/workflows/tests.yml</c>, consumed by
/// <c>.github/actions/select-tests/action.yml</c>), the top-level changed-file skip gate, and the
/// selection-comment posting in <c>tests.yml</c>. These are not exercised by the CLI tests, yet are easy to silently regress
/// (loosen the kill switch, or revert the comment to update-in-place), so they are pinned here.
/// </summary>
public sealed class SelectTestsWorkflowTests
{
    [Theory]
    [InlineData("tests-outerloop.yml", "eng/testing/CITestsProperties.props", true)]
    [InlineData("tests-quarantine.yml", "eng/testing/CITestsProperties.props", true)]
    [InlineData("tests-outerloop.yml", ".github/workflows/specialized-test-runner.yml", true)]
    [InlineData("tests-quarantine.yml", ".github/workflows/specialized-test-runner.yml", true)]
    [InlineData("tests-outerloop.yml", ".github/workflows/filter-nonblocking-test-artifacts.sh", true)]
    [InlineData("tests-quarantine.yml", ".github/workflows/filter-nonblocking-test-artifacts.sh", false)]
    [InlineData("tests-outerloop.yml", "README.md", false)]
    [InlineData("tests-quarantine.yml", "README.md", false)]
    public void SpecializedWorkflowRunsForChangedInput(string workflow, string changedPath, bool expected)
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", workflow));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var triggers = Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("on")]);
        var pullRequest = Assert.IsType<YamlMappingNode>(triggers.Children[new YamlScalarNode("pull_request")]);
        var paths = Assert.IsType<YamlSequenceNode>(pullRequest.Children[new YamlScalarNode("paths")]);

        // These workflows currently use literal paths; this checks one-file
        // eligibility, not full equivalence with GitHub's glob semantics.
        // https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#onpushpull_requestpull_request_targetpathspaths-ignore
        var matcher = new Matcher(StringComparison.Ordinal, preserveFilterOrder: true);
        foreach (var node in paths)
        {
            var pattern = node.ToString();
            if (pattern.StartsWith('!'))
            {
                matcher.AddExclude(pattern[1..]);
            }
            else
            {
                matcher.AddInclude(pattern);
            }
        }

        Assert.Equal(expected, matcher.Match(changedPath).HasMatches);
    }

    // tests.yml must compute forceAll from the presence of the 'run-full-ci' label on the PR, read from
    // the event-payload snapshot. If the label name or the contains() expression drifts, the kill
    // switch silently stops working (it would just never force-all), so pin the exact wiring.
    [Fact]
    public void TestsWorkflowComputesForceAllFromFullCiLabel()
    {
        var testsYml = File.ReadAllText(TestsWorkflowPath);

        Assert.Contains(
            "forceAll: ${{ contains(github.event.pull_request.labels.*.name, 'run-full-ci') }}",
            testsYml);
    }

    [Fact]
    public void TestsWorkflowEnforcesSelectedTestSubset()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(File.ReadAllText(TestsWorkflowPath));
        yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var jobs = (YamlMappingNode)root.Children[new YamlScalarNode("jobs")];
        var setupForTests = (YamlMappingNode)jobs.Children[new YamlScalarNode("setup_for_tests")];
        var steps = (YamlSequenceNode)setupForTests.Children[new YamlScalarNode("steps")];
        var selectTests = Assert.Single(
            steps.Cast<YamlMappingNode>(),
            step => step.Children.TryGetValue(new YamlScalarNode("uses"), out var uses) &&
                    uses.ToString() == "./.github/actions/select-tests");
        var inputs = (YamlMappingNode)selectTests.Children[new YamlScalarNode("with")];

        Assert.Equal("true", inputs.Children[new YamlScalarNode("enforce")].ToString());
    }

    // The native ARM64 MSVC linker in the VS 2022 image fails on the Native AOT Dashboard object
    // with LNK1322. Keep win-arm64 on the newer VS 2026 ARM image in both the PR override and the
    // reusable workflow default so scheduled/direct callers do not regress to windows-11-arm.
    [Fact]
    public void WindowsArm64NativeArchiveUsesVs2026ArmRunner()
    {
        var testsYml = File.ReadAllText(TestsWorkflowPath);
        var nativeArchivesYml = File.ReadAllText(BuildCliNativeArchivesWorkflowPath);
        const string expectedTarget = "{\"os\": \"windows-latest\", \"runner\": \"windows-11-vs2026-arm\", \"rids\": \"win-arm64\"}";

        Assert.Contains(expectedTarget, testsYml);
        Assert.Contains(expectedTarget, nativeArchivesYml);
        Assert.DoesNotContain("\"runner\": \"windows-11-arm\", \"rids\": \"win-arm64\"", testsYml);
        Assert.DoesNotContain("\"runner\": \"windows-11-arm\", \"rids\": \"win-arm64\"", nativeArchivesYml);
    }

    // The comment_selection job posts one comment per pushed commit (createComment for a new commit,
    // updateComment for a re-run of the same commit) and collapses superseded comments with
    // minimizeComment -- it must never delete. This guard fails if deletion is introduced or the
    // head-commit link (also the idempotency key) is dropped.
    [Fact]
    public void CommentSelectionJobIsIdempotentPerCommitAndCollapsesSuperseded()
    {
        var job = ExtractCommentSelectionJob();

        Assert.Contains("github.rest.issues.createComment", job);
        Assert.Contains("github.rest.issues.updateComment", job);
        Assert.Contains("minimizeComment", job);
        Assert.DoesNotContain("deleteComment", job);

        // Minimization is gated on this run being the PR's live head (a stale re-run must not collapse
        // a newer commit's comment), resolved via pulls.get.
        Assert.Contains("github.rest.pulls.get", job);

        // The marker is read back to find prior comments; the head SHA links the commit and keys
        // create-vs-update.
        Assert.Contains("<!-- select-tests-comment -->", job);
        Assert.Contains("pull_request?.head?.sha", job);
        Assert.Contains("/commit/", job);
    }

    private static string TestsWorkflowPath
        => Path.Combine(RepoRoot.Path, ".github", "workflows", "tests.yml");

    private static string BuildCliNativeArchivesWorkflowPath
        => Path.Combine(RepoRoot.Path, ".github", "workflows", "build-cli-native-archives.yml");

    private static string ExtractCommentSelectionJob()
    {
        var testsYml = File.ReadAllText(TestsWorkflowPath);

        var start = testsYml.IndexOf("comment_selection:", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected a comment_selection job in {TestsWorkflowPath}.");

        // Bound the slice at the next top-level job so assertions can't match other jobs' scripts.
        var end = testsYml.IndexOf("build_packages:", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Expected the comment_selection job to precede build_packages in {TestsWorkflowPath}.");

        return testsYml[start..end];
    }
}
