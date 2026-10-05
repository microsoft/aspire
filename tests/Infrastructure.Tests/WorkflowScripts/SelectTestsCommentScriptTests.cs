// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

/// <summary>
/// Executes the privileged selected-tests comment script with controlled GitHub fixtures.
/// The workflow intentionally keeps the script inline so its write-scoped job never checks out
/// pull request code.
/// </summary>
public sealed class SelectTestsCommentScriptTests : IDisposable
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TemporaryWorkspace _workspace;
    private readonly string _repoRoot = RepoRoot.Path;
    private readonly string _harnessPath;
    private readonly ITestOutputHelper _output;

    public SelectTestsCommentScriptTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _harnessPath = Path.Combine(_repoRoot, "tests", "Infrastructure.Tests", "WorkflowScripts", "select-tests-comment.harness.js");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public void CommentJobKeepsWriteTokenSeparateFromPullRequestCode()
    {
        var job = LoadCommentJob();
        Assert.Equal("setup_for_tests", Scalar(job, "needs"));
        var permissions = Mapping(job, "permissions");
        Assert.Equal("read", Scalar(permissions, "contents"));
        Assert.Equal("write", Scalar(permissions, "issues"));
        Assert.Equal("write", Scalar(permissions, "pull-requests"));

        var steps = Sequence(job, "steps").Children.Cast<YamlMappingNode>().ToList();
        Assert.DoesNotContain(
            steps,
            step => ScalarOrNull(step, "uses")?.StartsWith("actions/checkout@", StringComparison.Ordinal) == true);
        var download = Assert.Single(
            steps,
            step => ScalarOrNull(step, "uses")?.StartsWith("actions/download-artifact@", StringComparison.Ordinal) == true);
        var comment = Assert.Single(
            steps,
            step => ScalarOrNull(step, "uses")?.StartsWith("actions/github-script@", StringComparison.Ordinal) == true);
        Assert.True(steps.IndexOf(comment) > steps.IndexOf(download));
        Assert.Equal(
            "${{ github.workspace }}/select-tests-comment/select-tests-comment.md",
            Scalar(Mapping(comment, "env"), "SELECT_TESTS_COMMENT_FILE"));
        Assert.DoesNotContain("${{", Scalar(Mapping(comment, "with"), "script"), StringComparison.Ordinal);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task NewCommitCreatesCommentLinkingPrHeadAndMinimizesSuperseded()
    {
        var summaryPath = WriteSummary("## Tests selector (audit mode)\n\nSENTINEL-BODY-CONTENT");
        const string headSha = "abcdef1234567890abcdef1234567890abcdef12";
        const string olderSha = "1111111111111111111111111111111111111111";

        // A comment from an earlier commit exists; this is a brand-new commit.
        var older = MarkerComment(id: 41, nodeId: "NODE_OLDER", sha: olderSha);
        var result = await RunCommentScriptAsync(summaryPath, PullRequestContext(headSha, contextSha: "0000000fa11bac0fa11bac0fa11bac0fa11bac00"), older);

        var comment = Assert.Single(result.Created);
        Assert.Empty(result.Updated);
        Assert.Contains("<!-- select-tests-comment -->", comment.Body);
        Assert.Contains("SENTINEL-BODY-CONTENT", comment.Body);
        // Links the full head SHA and renders the 7-char short SHA; must not use the context.sha fallback.
        Assert.Contains($"/commit/{headSha}", comment.Body);
        Assert.Contains("[`abcdef1`]", comment.Body);
        Assert.DoesNotContain("0000000", comment.Body);

        // The older commit's comment is collapsed, not deleted.
        Assert.Equal(["NODE_OLDER"], result.Minimized);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task RerunOfSameCommitUpdatesInPlaceWithoutNewComment()
    {
        var summaryPath = WriteSummary("## Tests selector (audit mode)\n\nFRESH-CONTENT");
        const string headSha = "abcdef1234567890abcdef1234567890abcdef12";
        const string olderSha = "2222222222222222222222222222222222222222";

        // The comment for THIS commit already exists (a prior run of the same commit), plus an older one.
        var current = MarkerComment(id: 50, nodeId: "NODE_CURRENT", sha: headSha);
        var older = MarkerComment(id: 49, nodeId: "NODE_OLDER", sha: olderSha);
        var result = await RunCommentScriptAsync(summaryPath, PullRequestContext(headSha, contextSha: "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef"), current, older);

        // Re-run updates the existing same-commit comment in place; no new comment is created.
        Assert.Empty(result.Created);
        var update = Assert.Single(result.Updated);
        Assert.Equal(50, update.CommentId);
        Assert.Contains("FRESH-CONTENT", update.Body);

        // The kept (current) comment is not minimized; the older commit's comment is.
        Assert.Equal(["NODE_OLDER"], result.Minimized);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task FallsBackToContextShaWhenNotPullRequest()
    {
        var summaryPath = WriteSummary("## Tests selector (audit mode)");
        const string contextSha = "1234567deadbeefdeadbeefdeadbeefdeadbeef0";

        // No pull_request on the payload (e.g. a non-PR trigger) -> the script uses context.sha.
        var result = await RunCommentScriptAsync(summaryPath, NonPullRequestContext(contextSha));

        var comment = Assert.Single(result.Created);
        Assert.Contains($"/commit/{contextSha}", comment.Body);
        Assert.Contains("[`1234567`]", comment.Body);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task SkipsCommentWhenSummaryFileMissing()
    {
        var missingPath = Path.Combine(_workspace.Path, "does-not-exist.md");

        var result = await RunCommentScriptAsync(missingPath, PullRequestContext("abcdef1234567890", contextSha: "fedcba0987654321"));

        Assert.Empty(result.Created);
        Assert.Empty(result.Updated);
        Assert.Empty(result.Minimized);
        Assert.Contains(result.Infos, info => info.Contains("skipping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task MigratesLegacyStickyCommentByCreatingFreshAndCollapsingIt()
    {
        var summaryPath = WriteSummary("## Tests selector (audit mode)\n\nMIGRATED");
        const string headSha = "abcdef1234567890abcdef1234567890abcdef12";

        // The pre-migration state: a single legacy sticky comment (marker, no commit footer). It
        // matches no SHA, so the run posts a fresh per-commit comment and collapses the legacy one.
        var legacy = LegacyMarkerComment(id: 7, nodeId: "NODE_LEGACY");
        var result = await RunCommentScriptAsync(summaryPath, PullRequestContext(headSha, contextSha: headSha), legacy);

        var comment = Assert.Single(result.Created);
        Assert.Contains($"/commit/{headSha}", comment.Body);
        Assert.Empty(result.Updated);
        Assert.Equal(["NODE_LEGACY"], result.Minimized);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task NewCommitMinimizesAllSupersededComments()
    {
        var summaryPath = WriteSummary("## Tests selector (audit mode)");
        const string headSha = "abcdef1234567890abcdef1234567890abcdef12";

        // Several superseded comments from earlier commits -> all collapsed, none kept.
        var older1 = MarkerComment(id: 11, nodeId: "NODE_1", sha: "1111111111111111111111111111111111111111");
        var older2 = MarkerComment(id: 12, nodeId: "NODE_2", sha: "2222222222222222222222222222222222222222");
        var older3 = MarkerComment(id: 13, nodeId: "NODE_3", sha: "3333333333333333333333333333333333333333");
        var result = await RunCommentScriptAsync(summaryPath, PullRequestContext(headSha, contextSha: headSha), older1, older2, older3);

        Assert.Single(result.Created);
        Assert.Equal(3, result.Minimized.Length);
        Assert.Contains("NODE_1", result.Minimized);
        Assert.Contains("NODE_2", result.Minimized);
        Assert.Contains("NODE_3", result.Minimized);
    }

    [Fact]
    [RequiresTools(["node"])]
    public async Task StaleRerunUpdatesOwnCommentButMinimizesNothing()
    {
        var summaryPath = WriteSummary("## Tests selector (audit mode)\n\nSTALE-REFRESH");
        const string staleSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string liveSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        // A manual re-run of the run for staleSha after a newer commit (liveSha) already posted. The
        // run's payload still carries staleSha; pulls.get reports liveSha as the head. It must refresh
        // its own comment but NOT minimize -- otherwise it would collapse the newer commit's live one.
        var ownComment = MarkerComment(id: 60, nodeId: "NODE_OWN", sha: staleSha);
        var liveComment = MarkerComment(id: 61, nodeId: "NODE_LIVE", sha: liveSha);
        var result = await RunCommentScriptAsync(
            summaryPath,
            PullRequestContext(staleSha, contextSha: staleSha),
            liveHeadSha: liveSha,
            [ownComment, liveComment]);

        Assert.Empty(result.Created);
        var update = Assert.Single(result.Updated);
        Assert.Equal(60, update.CommentId);
        Assert.Empty(result.Minimized);
    }

    private string WriteSummary(string content)
    {
        var path = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.md");
        File.WriteAllText(path, content);
        return path;
    }

    // A prior select-tests comment as the REST listing returns it: the marker plus a footer linking
    // its commit (the body the script matches on for idempotency).
    private static object MarkerComment(int id, string nodeId, string sha) => new
    {
        id,
        node_id = nodeId,
        body = $"<!-- select-tests-comment -->\n## Tests selector (audit mode)\n\n---\n_Selection computed for commit [`{sha[..7]}`](https://github.com/microsoft/aspire/commit/{sha})._",
    };

    // The legacy single sticky comment from before per-commit posting: it has the marker but no
    // commit footer, so it matches no SHA and must be collapsed on migration.
    private static object LegacyMarkerComment(int id, string nodeId) => new
    {
        id,
        node_id = nodeId,
        body = "<!-- select-tests-comment -->\n## Tests selector (audit mode)\n\n_Legacy sticky comment with no commit footer._",
    };

    private static object PullRequestContext(string headSha, string contextSha) => new
    {
        repo = new { owner = "microsoft", repo = "aspire" },
        issue = new { number = 18127 },
        payload = new { pull_request = new { head = new { sha = headSha } } },
        sha = contextSha,
        serverUrl = "https://github.com",
    };

    private static object NonPullRequestContext(string contextSha) => new
    {
        repo = new { owner = "microsoft", repo = "aspire" },
        issue = new { number = 18127 },
        payload = new { },
        sha = contextSha,
        serverUrl = "https://github.com",
    };

    private async Task<CommentScriptResult> RunCommentScriptAsync(string commentFile, object context, params object[] existingComments)
        => await RunCommentScriptAsync(commentFile, context, liveHeadSha: null, existingComments);

    // liveHeadSha overrides the PR head the script sees via pulls.get (the minimize gate). Passing a
    // value different from this run's commit simulates a stale re-run (an older run replayed after a
    // newer commit). null -> the harness defaults it to this run's head, i.e. "this run is live".
    private async Task<CommentScriptResult> RunCommentScriptAsync(string commentFile, object context, string? liveHeadSha, object[] existingComments)
    {
        var script = ExtractCommentScript();
        var requestPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.json");
        var outputPath = Path.Combine(_workspace.Path, $"{Guid.NewGuid():N}.result.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new { script, commentFile, context, liveHeadSha, existingComments }, s_jsonOptions));

        using var command = new NodeCommand(_output, "select-tests-comment");
        command.WithWorkingDirectory(_repoRoot);

        // Result goes to a file, not stdout, so a stray node warning on stderr/stdout can't corrupt
        // the JSON (NodeCommand merges both streams into result.Output).
        var result = await command.ExecuteScriptAsync(_harnessPath, requestPath, outputPath);
        Assert.Equal(0, result.ExitCode);

        var response = JsonSerializer.Deserialize<HarnessResponse<CommentScriptResult>>(await File.ReadAllTextAsync(outputPath), s_jsonOptions);
        Assert.NotNull(response);
        return response!.Result;
    }

    private static string ExtractCommentScript()
        => Scalar(
            Mapping(
                Assert.Single(
                    Sequence(LoadCommentJob(), "steps").Children.Cast<YamlMappingNode>(),
                    step => ScalarOrNull(step, "uses")?.StartsWith("actions/github-script@", StringComparison.Ordinal) == true),
                "with"),
            "script");

    private static YamlMappingNode LoadCommentJob()
    {
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", "tests.yml"));
        var yaml = new YamlStream();
        yaml.Load(reader);
        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        return Mapping(Mapping(root, "jobs"), "comment_selection");
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static YamlSequenceNode Sequence(YamlMappingNode node, string key)
        => Assert.IsType<YamlSequenceNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key)
        => Assert.IsType<YamlScalarNode>(node.Children[new YamlScalarNode(key)]).Value!;

    private static string? ScalarOrNull(YamlMappingNode node, string key)
        => node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? Assert.IsType<YamlScalarNode>(value).Value
            : null;

    private sealed record HarnessResponse<T>(T Result);

    private sealed record CommentScriptResult(CreatedComment[] Created, UpdatedComment[] Updated, string[] Minimized, string[] Infos);

    private sealed record CreatedComment(string Body);

    private sealed record UpdatedComment(int CommentId, string Body);
}