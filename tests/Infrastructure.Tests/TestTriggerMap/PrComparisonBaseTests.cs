// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

public sealed class PrComparisonBaseTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [RequiresTools(["git", "bash"])]
    public async Task PinnedBaseExcludesBaseChurn(bool mergeBaseIntoPr, bool clone)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var repo = workspace.CreateDirectory("origin").FullName;
        var (oldBase, currentBase, head, merge) = CreateHistory(repo, mergeBaseIntoPr);
        if (mergeBaseIntoPr)
        {
            Assert.Equal(
                new[] { "base-only.txt", "pr-only.txt" },
                ChangedFiles(repo, oldBase, head));
        }

        var checkout = repo;
        if (clone)
        {
            checkout = Path.Combine(workspace.Path, "checkout");
            GitCli.Run(workspace.Path, "clone", "--no-local", new Uri(repo).AbsoluteUri, checkout);
            Assert.Equal("false", GitCli.Run(checkout, "rev-parse", "--is-shallow-repository"));
        }

        var result = await Resolve(checkout, oldBase, head, "pull_request", merge, "refs/pull/42/merge");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(currentBase, result.StandardOutput.Trim());
        Assert.Equal(new[] { "pr-only.txt" }, ChangedFiles(checkout, result.StandardOutput.Trim(), head));
        Assert.Equal(merge, GitCli.Run(checkout, "rev-parse", "HEAD"));
    }

    [Theory]
    [InlineData("head")]
    [InlineData("checkout")]
    [InlineData("ref")]
    [InlineData("parents")]
    [InlineData("octopus")]
    [RequiresTools(["git", "bash"])]
    public async Task UnverifiedMergeIdentityRejectsComparison(string mismatch)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var (oldBase, currentBase, head, merge) = CreateHistory(workspace.Path, mergeBaseIntoPr: true);
        var expectedMerge = mismatch == "checkout" ? head : merge;
        var expectedHead = mismatch == "head" ? oldBase : head;
        var expectedRef = mismatch == "ref" ? "refs/heads/main" : "refs/pull/42/merge";
        if (mismatch == "parents")
        {
            expectedMerge = GitCli.Run(workspace.Path, "rev-parse", $"{head}^");
            GitCli.Run(workspace.Path, "checkout", "--detach", expectedMerge);
        }
        else if (mismatch == "octopus")
        {
            expectedMerge = GitCli.Run(workspace.Path, "commit-tree", $"{merge}^{{tree}}",
                "-p", currentBase, "-p", head, "-p", oldBase, "-m", "Three-parent merge");
            GitCli.Run(workspace.Path, "checkout", "--detach", expectedMerge);
        }

        var result = await Resolve(workspace.Path, oldBase, expectedHead, "pull_request", expectedMerge, expectedRef);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("::warning::", result.StandardError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [RequiresTools(["git", "bash"])]
    public async Task ShallowSideBranchesCannotHideProductionRevert(bool pullRequest)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var repo = workspace.CreateDirectory("origin").FullName;
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        GitCli.Run(repo, "init", "-b", "main");
        GitCli.Run(repo, "config", "user.email", "test@example.invalid");
        GitCli.Run(repo, "config", "user.name", "Test");
        var productionFile = Path.Combine(repo, "src", "Production.cs");
        File.WriteAllText(productionFile, "v0");
        GitCli.Run(repo, "add", ".");
        GitCli.Run(repo, "commit", "-m", "Root");
        var root = GitCli.Run(repo, "rev-parse", "HEAD");
        for (var i = 0; i < 9; i++)
        {
            CommitFile(repo, $"common-{i}.txt");
        }
        File.WriteAllText(productionFile, "v1");
        GitCli.Run(repo, "commit", "-am", "Production change");
        var commonAncestor = GitCli.Run(repo, "rev-parse", "HEAD");
        GitCli.Run(repo, "branch", "topic");

        foreach (var side in new[] { "main", "pr" })
        {
            GitCli.Run(repo, "checkout", "-b", $"{side}-side", root);
            Directory.CreateDirectory(Path.Combine(repo, "docs"));
            CommitFile(repo, $"docs/{side}-side.md");
        }
        GitCli.Run(repo, "checkout", "main");
        for (var i = 0; i < 9; i++)
        {
            CommitFile(repo, $"main-{i}.txt");
        }
        GitCli.Run(repo, "merge", "--no-ff", "main-side", "-m", "Merge main docs");
        var currentBase = GitCli.Run(repo, "rev-parse", "HEAD");
        GitCli.Run(repo, "checkout", "topic");
        for (var i = 0; i < 9; i++)
        {
            CommitFile(repo, $"topic-{i}.txt");
        }
        File.WriteAllText(productionFile, "v0");
        GitCli.Run(repo, "commit", "-am", "Revert production change");
        GitCli.Run(repo, "merge", "--no-ff", "pr-side", "-m", "Merge PR docs");
        var head = GitCli.Run(repo, "rev-parse", "HEAD");
        GitCli.Run(repo, "checkout", "main");
        GitCli.Run(repo, "merge", "--no-ff", "topic", "-m", "Synthetic merge");
        var merge = GitCli.Run(repo, "rev-parse", "HEAD");
        Assert.Equal(commonAncestor, GitCli.Run(repo, "merge-base", "--all", currentBase, head));
        var expected = ChangedFiles(repo, currentBase, head);
        Assert.Contains("src/Production.cs", expected);

        var checkout = Path.Combine(workspace.Path, "checkout");
        GitCli.Run(workspace.Path, "clone", "--depth=1", "--no-local", new Uri(repo).AbsoluteUri, checkout);
        // Short side branches expose the root before the true common ancestor.
        // A successful shallow merge-base therefore cannot certify a safe diff.
        GitCli.Run(checkout, "fetch", "--no-tags", "--depth=4", "origin", currentBase, head);
        Assert.Equal(root, GitCli.Run(checkout, "merge-base", currentBase, head));

        var result = await Resolve(checkout, pullRequest ? commonAncestor : currentBase, head,
            pullRequest ? "pull_request" : "workflow_dispatch", merge,
            pullRequest ? "refs/pull/42/merge" : "refs/heads/main");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("Comparison history is shallow", result.StandardError);
        Assert.Equal("true", GitCli.Run(checkout, "rev-parse", "--is-shallow-repository"));

        GitCli.Run(checkout, "fetch", "--no-tags", "--unshallow", "origin");
        result = await Resolve(checkout, pullRequest ? commonAncestor : currentBase, head,
            pullRequest ? "pull_request" : "workflow_dispatch", merge,
            pullRequest ? "refs/pull/42/merge" : "refs/heads/main");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(currentBase, result.StandardOutput.Trim());
        var actual = ChangedFiles(checkout, result.StandardOutput.Trim(), head);
        Assert.Contains("src/Production.cs", actual);
        Assert.Equal(expected, actual);
        Assert.Equal(commonAncestor, GitCli.Run(checkout, "merge-base", "--all", currentBase, head));
        Assert.Equal("false", GitCli.Run(checkout, "rev-parse", "--is-shallow-repository"));
        Assert.Equal(merge, GitCli.Run(checkout, "rev-parse", "HEAD"));
    }

    [Fact]
    [RequiresTools(["git", "bash"])]
    public async Task IncompleteHistoryCannotReturnSelectiveComparison()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var repo = workspace.CreateDirectory("origin").FullName;
        var (_, _, _, merge) = CreateHistory(repo, mergeBaseIntoPr: true);
        var checkout = Path.Combine(workspace.Path, "checkout");
        GitCli.Run(workspace.Path, "clone", "--depth=1", "--no-local", new Uri(repo).AbsoluteUri, checkout);
        GitCli.Run(checkout, "remote", "remove", "origin");

        var result = await Resolve(checkout, merge, merge, "workflow_dispatch", merge, "refs/heads/main");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("::warning::", result.StandardError);
        Assert.Equal("true", GitCli.Run(checkout, "rev-parse", "--is-shallow-repository"));
    }

    [Fact]
    [RequiresTools(["git", "bash"])]
    public async Task CrissCrossHistoryRejectsAmbiguousComparison()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var repo = workspace.CreateDirectory("origin").FullName;
        GitCli.Run(repo, "init", "-b", "main");
        GitCli.Run(repo, "config", "user.email", "test@example.invalid");
        GitCli.Run(repo, "config", "user.name", "Test");
        var productionFile = Path.Combine(repo, "Production.cs");
        File.WriteAllText(productionFile, "v0");
        GitCli.Run(repo, "add", ".");
        GitCli.Run(repo, "commit", "-m", "Root");
        var root = GitCli.Run(repo, "rev-parse", "HEAD");
        File.WriteAllText(productionFile, "v1");
        GitCli.Run(repo, "commit", "-am", "Production change");
        var productionAncestor = GitCli.Run(repo, "rev-parse", "HEAD");
        GitCli.Run(repo, "checkout", "-b", "topic", root);
        var docsAncestor = CommitFile(repo, "docs.md");
        // Each branch merges the other's earlier tip, leaving two equally good
        // common ancestors. The PR's ours merge discards the production change.
        GitCli.Run(repo, "merge", "--no-ff", "-s", "ours", productionAncestor, "-m", "Discard production change");
        var head = GitCli.Run(repo, "rev-parse", "HEAD");
        GitCli.Run(repo, "checkout", "main");
        GitCli.Run(repo, "merge", "--no-ff", docsAncestor, "-m", "Merge docs");
        var currentBase = GitCli.Run(repo, "rev-parse", "HEAD");
        Assert.Equal(
            new[] { productionAncestor, docsAncestor }.Order(StringComparer.Ordinal),
            GitCli.Run(repo, "merge-base", "--all", currentBase, head).Split('\n').Order(StringComparer.Ordinal));
        GitCli.Run(repo, "merge", "--no-ff", "-s", "ours", "topic", "-m", "Synthetic merge");
        var merge = GitCli.Run(repo, "rev-parse", "HEAD");
        var result = await Resolve(repo, root, head, "pull_request", merge, "refs/pull/42/merge");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("Multiple merge-bases", result.StandardError);
    }

    [Fact]
    [RequiresTools(["git", "bash"])]
    public async Task NonPrExplicitBaseDoesNotRequireMergeCheckout()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var (oldBase, _, head, _) = CreateHistory(workspace.Path, mergeBaseIntoPr: false);
        GitCli.Run(workspace.Path, "checkout", "--detach", head);

        var result = await Resolve(workspace.Path, oldBase, head, "workflow_dispatch", head, "refs/heads/topic");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(oldBase, result.StandardOutput.Trim());
        Assert.Equal(new[] { "pr-only.txt" }, ChangedFiles(workspace.Path, result.StandardOutput.Trim(), head));
    }

    [Fact]
    [RequiresTools(["git", "bash"])]
    public async Task UnrelatedCompleteHistoriesRejectComparison()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var (_, currentBase, _, _) = CreateHistory(workspace.Path, mergeBaseIntoPr: false);
        GitCli.Run(workspace.Path, "checkout", "--orphan", "unrelated");
        GitCli.Run(workspace.Path, "rm", "-rf", ".");
        var head = CommitFile(workspace.Path, "unrelated.txt");
        GitCli.Run(workspace.Path, "checkout", "main");
        GitCli.Run(workspace.Path, "merge", "--no-ff", "--allow-unrelated-histories", "unrelated", "-m", "Synthetic merge");
        var merge = GitCli.Run(workspace.Path, "rev-parse", "HEAD");
        GitCli.Run(workspace.Path, "remote", "add", "origin", workspace.Path);

        var result = await Resolve(workspace.Path, currentBase, head, "pull_request", merge, "refs/pull/42/merge");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("No merge-base", result.StandardError);
    }

    [Fact]
    [RequiresTools(["git", "bash"])]
    public async Task MissingExplicitBaseFailsRatherThanReturningAComparison()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var (_, _, head, merge) = CreateHistory(workspace.Path, mergeBaseIntoPr: false);
        GitCli.Run(workspace.Path, "remote", "add", "origin", workspace.Path);

        var result = await Resolve(workspace.Path, new string('1', 40), head, "workflow_dispatch", merge, "refs/heads/main");

        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.StandardOutput);
        Assert.Contains("::error::", result.StandardError);
    }

    [Fact]
    [RequiresTools(["git", "bash"])]
    public async Task ComparisonBaseSupportsDiffOfExistingFileRename()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var (_, _, _, originalMerge) = CreateHistory(workspace.Path, mergeBaseIntoPr: true);
        GitCli.Run(workspace.Path, "checkout", "topic");
        GitCli.Run(workspace.Path, "mv", "root.txt", "renamed.txt");
        GitCli.Run(workspace.Path, "commit", "-m", "Rename PR file");
        var head = GitCli.Run(workspace.Path, "rev-parse", "HEAD");
        GitCli.Run(workspace.Path, "checkout", "main");
        GitCli.Run(workspace.Path, "merge", "--no-ff", "topic", "-m", "Synthetic merge");
        var merge = GitCli.Run(workspace.Path, "rev-parse", "HEAD");

        var result = await Resolve(workspace.Path, originalMerge, head, "pull_request", merge, "refs/pull/42/merge");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(originalMerge, result.StandardOutput.Trim());
        Assert.Equal(new[] { "renamed.txt", "root.txt" }, ChangedFiles(workspace.Path, result.StandardOutput.Trim(), head));
    }

    private Task<ProcessResult> Resolve(string repo, string eventBase, string head, string eventName, string merge, string gitRef)
        => TestProcessRunner.RunAsync(output, "bash",
            [Path.Combine(RepoRoot.Path, "eng", "github-ci", "resolve-comparison-base.sh"), eventBase, head],
            repo,
            new Dictionary<string, string>
            {
                ["GITHUB_EVENT_NAME"] = eventName,
                ["GITHUB_SHA"] = merge,
                ["GITHUB_REF"] = gitRef
            });

    private static (string OldBase, string CurrentBase, string Head, string Merge) CreateHistory(string repo, bool mergeBaseIntoPr)
    {
        GitCli.Run(repo, "init", "-b", "main");
        GitCli.Run(repo, "config", "user.email", "test@example.invalid");
        GitCli.Run(repo, "config", "user.name", "Test");
        var oldBase = CommitFile(repo, "root.txt");
        GitCli.Run(repo, "checkout", "-b", "topic");
        CommitFile(repo, "pr-only.txt");
        GitCli.Run(repo, "checkout", "main");
        var currentBase = CommitFile(repo, "base-only.txt");
        GitCli.Run(repo, "checkout", "topic");
        if (mergeBaseIntoPr)
        {
            GitCli.Run(repo, "merge", "--no-ff", "main", "-m", "Update topic");
        }
        var head = GitCli.Run(repo, "rev-parse", "HEAD");
        GitCli.Run(repo, "checkout", "main");
        GitCli.Run(repo, "merge", "--no-ff", "topic", "-m", "Synthetic merge");

        return (oldBase, currentBase, head, GitCli.Run(repo, "rev-parse", "HEAD"));
    }

    private static string CommitFile(string repo, string path)
    {
        File.WriteAllText(Path.Combine(repo, path), path);
        GitCli.Run(repo, "add", "--", path);
        GitCli.Run(repo, "commit", "-m", $"Add {path}");

        return GitCli.Run(repo, "rev-parse", "HEAD");
    }

    private static string[] ChangedFiles(string repo, string from, string to)
        => GitCli.Run(repo, "diff", "--name-only", "--no-renames", $"{from}...{to}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
