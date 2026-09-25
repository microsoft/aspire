// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Infrastructure.Tests;

public sealed class AgenticWorkflowTests
{
    private const string ActionlintImage = "rhysd/actionlint:1.7.12@sha256:b1934ee5f1c509618f2508e6eb47ee0d3520686341fec936f3b79331f9315667";
    private static readonly string s_workflowsPath = Path.Combine(RepoRoot.Path, ".github", "workflows");

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task GeneratedWorkflowsPassActionlint()
    {
        var lockFiles = Directory.EnumerateFiles(s_workflowsPath, "*.lock.yml")
            .Select(path => Path.GetRelativePath(RepoRoot.Path, path))
            .Order()
            .ToArray();
        Assert.NotEmpty(lockFiles);

        using var process = new Process();
        process.StartInfo.FileName = "docker";
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--rm");
        process.StartInfo.ArgumentList.Add("-v");
        process.StartInfo.ArgumentList.Add($"{RepoRoot.Path}:/workdir");
        process.StartInfo.ArgumentList.Add("-w");
        process.StartInfo.ArgumentList.Add("/workdir");
        process.StartInfo.ArgumentList.Add(ActionlintImage);
        process.StartInfo.ArgumentList.Add("-pyflakes=");
        foreach (var lockFile in lockFiles)
        {
            process.StartInfo.ArgumentList.Add(lockFile);
        }
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;

        process.Start();
        // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdoutTask + await stderrTask;

        Assert.True(process.ExitCode == 0, output);
    }

    [Fact]
    public void GeneratedWorkflowsMatchBootstrapCompiler()
    {
        var bootstrap = LoadWorkflow("copilot-setup-steps.yml");
        var setup = Assert.Single(Mappings(bootstrap), node => Scalar(node, "uses").StartsWith("github/gh-aw-actions/setup-cli@", StringComparison.Ordinal));
        var version = Scalar(Mapping(setup, "with"), "version");
        var setupSha = Scalar(setup, "uses").Split('@')[1];
        Assert.Matches(@"^v\d+\.\d+\.\d+$", version);
        Assert.Matches("^[a-f0-9]{40}$", setupSha);

        var sources = Directory.EnumerateFiles(s_workflowsPath, "*.md")
            .Where(path => File.ReadLines(path).First() == "---")
            .ToArray();
        Assert.NotEmpty(sources);
        foreach (var source in sources)
        {
            var compiledName = Path.GetFileNameWithoutExtension(source) + ".lock.yml";
            var metadataLine = File.ReadLines(Path.Combine(s_workflowsPath, compiledName)).First();
            const string prefix = "# gh-aw-metadata: ";
            Assert.StartsWith(prefix, metadataLine);
            using var metadata = JsonDocument.Parse(metadataLine[prefix.Length..]);
            Assert.Equal(version, metadata.RootElement.GetProperty("compiler_version").GetString());

            var setupSteps = Mappings(LoadWorkflow(compiledName))
                .Where(node => Scalar(node, "uses").StartsWith("github/gh-aw-actions/setup@", StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(setupSteps);
            Assert.All(setupSteps, step => Assert.Equal("github/gh-aw-actions/setup@" + setupSha, Scalar(step, "uses")));
        }

        var maintenanceHeader = File.ReadLines(Path.Combine(s_workflowsPath, "agentics-maintenance-microsoft-aspire.dev.yml")).First();
        Assert.Contains($"side_repo_maintenance.go ({version})", maintenanceHeader, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".md")]
    [InlineData(".lock.yml")]
    public void CiAnalysisTransfersOnlyItsPublishedFiles(string extension)
    {
        var root = LoadWorkflow("analyze-ci-failure" + extension);
        var upload = Step(root, "Upload CI analysis files");
        AssertArtifact(upload, "actions/upload-artifact", "ci-analysis-output");
        Assert.Equal(
            ["/tmp/gh-aw/agent/analysis-result.json", "/tmp/gh-aw/agent/causes/*.json"],
            Scalar(Mapping(upload, "with"), "path").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("error", Scalar(Mapping(upload, "with"), "if-no-files-found"));

        var download = Step(root, "Download CI analysis files");
        AssertArtifact(download, "actions/download-artifact", "ci-analysis-output");
        Assert.Equal("download-analysis", Scalar(download, "id"));
        Assert.Equal("${{ runner.temp }}/ci-analysis-output", Scalar(Mapping(download, "with"), "path"));

        var validation = Step(root, "Validate analysis scope");
        Assert.Equal("${{ steps.download-analysis.outputs.download-path }}", Scalar(Mapping(validation, "env"), "ANALYSIS_DIR"));
        Assert.Contains("analyze-ci-failure-validation.sh", Scalar(validation, "run"), StringComparison.Ordinal);

        var publish = Step(root, "Publish analysis data and comment on PR");
        Assert.Equal("${{ steps.download-analysis.outputs.download-path }}", Scalar(Mapping(publish, "env"), "ANALYSIS_DIR"));
        var script = Scalar(publish, "run");
        Assert.Contains("ANALYSIS_FILE=\"$ANALYSIS_DIR/analysis-result.json\"", script, StringComparison.Ordinal);
        Assert.Contains("CAUSES_DIR=\"$ANALYSIS_DIR/causes\"", script, StringComparison.Ordinal);

        // The comment step runs in the same job but needs its own env wiring; without it the
        // analysis file is unreadable and the step fails before any comment is posted.
        var comment = Step(root, "Comment on PR");
        Assert.Equal("${{ steps.download-analysis.outputs.download-path }}", Scalar(Mapping(comment, "env"), "ANALYSIS_DIR"));
        Assert.Contains(
            "ANALYSIS_FILE=\"$ANALYSIS_DIR/analysis-result.json\"",
            Scalar(comment, "run"),
            StringComparison.Ordinal);

        // The rerun job is a separate job, so it must download the artifact itself.
        var rerunDownload = Step(root, "Download CI analysis files for rerun");
        AssertArtifact(rerunDownload, "actions/download-artifact", "ci-analysis-output");
        Assert.Equal("download-analysis", Scalar(rerunDownload, "id"));
        Assert.Equal("${{ runner.temp }}/ci-analysis-output", Scalar(Mapping(rerunDownload, "with"), "path"));

        var rerun = Step(root, "Rerun failed jobs");
        Assert.Equal("${{ steps.download-analysis.outputs.download-path }}", Scalar(Mapping(rerun, "env"), "ANALYSIS_DIR"));

        AssertUploadOrdering(root, extension, upload, download, publish);
    }

    [Theory]
    [InlineData(".md")]
    [InlineData(".lock.yml")]
    public void MilestoneChangelogTransfersBodyAndMemory(string extension)
    {
        var root = LoadWorkflow("milestone-changelog" + extension);
        var upload = Step(root, "Upload changelog files");
        AssertArtifact(upload, "actions/upload-artifact", "changelog-output");
        Assert.Equal(
            ["/tmp/gh-aw/agent/new-body.md", "/tmp/gh-aw/agent/memory/${{ env.MILESTONE }}/"],
            Scalar(Mapping(upload, "with"), "path").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("error", Scalar(Mapping(upload, "with"), "if-no-files-found"));

        var download = Step(root, "Download changelog files");
        AssertArtifact(download, "actions/download-artifact", "changelog-output");
        Assert.Equal("download-changelog", Scalar(download, "id"));
        Assert.Equal("${{ runner.temp }}/changelog-output", Scalar(Mapping(download, "with"), "path"));

        var publish = Step(root, "Publish changelog and update memory branch");
        Assert.Equal("${{ steps.download-changelog.outputs.download-path }}", Scalar(Mapping(publish, "env"), "CHANGELOG_DIR"));
        var script = Scalar(publish, "run");
        Assert.Contains("BODY_FILE=\"$CHANGELOG_DIR/new-body.md\"", script, StringComparison.Ordinal);
        Assert.Contains("MEMORY_DIR=\"$CHANGELOG_DIR/memory/$MILESTONE\"", script, StringComparison.Ordinal);
        AssertUploadOrdering(root, extension, upload, download, publish);
    }

    [Fact]
    public void WorkflowAppTokensUseClientId()
    {
        var workflows = Directory.EnumerateFiles(s_workflowsPath)
            .Where(path => path.EndsWith(".yml", StringComparison.Ordinal) ||
                path.EndsWith(".md", StringComparison.Ordinal) && File.ReadLines(path).First() == "---");
        var inputs = workflows.SelectMany(path => Mappings(LoadWorkflow(Path.GetFileName(path))))
            .SelectMany(node => node.Children
                .Where(pair => pair.Key.ToString() == "github-app" ||
                    pair.Key.ToString() == "with" && Scalar(node, "uses").StartsWith("actions/create-github-app-token@", StringComparison.Ordinal))
                .Select(pair => Assert.IsType<YamlMappingNode>(pair.Value)))
            .ToArray();
        Assert.NotEmpty(inputs);
        Assert.All(inputs, input =>
        {
            Assert.Equal(
                ["client-id"],
                input.Children.Keys.Select(key => key.ToString()).Where(key => key is "client-id" or "app-id"));
            Assert.NotEmpty(Scalar(input, "client-id"));
            Assert.NotEmpty(Scalar(input, "private-key"));
        });
    }

    private static void AssertArtifact(YamlMappingNode step, string action, string artifactName)
    {
        Assert.StartsWith(action + "@", Scalar(step, "uses"));
        Assert.Equal(artifactName, Scalar(Mapping(step, "with"), "name"));
    }

    private static void AssertUploadOrdering(YamlMappingNode root, string extension, YamlMappingNode upload, YamlMappingNode download, YamlMappingNode publish)
    {
        var mappings = Mappings(root).ToList();
        Assert.True(mappings.IndexOf(download) < mappings.IndexOf(publish));
        if (extension == ".lock.yml")
        {
            var redaction = Step(root, "Redact secrets in logs");
            Assert.True(mappings.IndexOf(redaction) < mappings.IndexOf(upload));
            Assert.Equal("${{ runner.temp }}/gh-aw/safe-jobs/agent_output.json", Scalar(Mapping(publish, "env"), "GH_AW_AGENT_OUTPUT"));
        }
        else
        {
            Assert.Contains(upload, Assert.IsType<YamlSequenceNode>(root.Children[new YamlScalarNode("post-steps")]).Children);
        }
    }

    private static YamlMappingNode LoadWorkflow(string fileName)
    {
        var content = File.ReadAllText(Path.Combine(s_workflowsPath, fileName)).ReplaceLineEndings("\n");
        if (fileName.EndsWith(".md", StringComparison.Ordinal))
        {
            content = content[4..content.IndexOf("\n---", 4, StringComparison.Ordinal)];
        }

        var yaml = new YamlStream();
        yaml.Load(new StringReader(content));
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }

    private static YamlMappingNode Step(YamlMappingNode root, string name) =>
        Assert.Single(Mappings(root), node => Scalar(node, "name") == name &&
            (node.Children.ContainsKey(new YamlScalarNode("uses")) || node.Children.ContainsKey(new YamlScalarNode("run"))));

    private static YamlMappingNode Mapping(YamlMappingNode node, string key) =>
        Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value.ToString() : "";

    private static IEnumerable<YamlMappingNode> Mappings(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
        {
            yield return mapping;
        }

        var children = node switch
        {
            YamlMappingNode parent => parent.Children.Values,
            YamlSequenceNode sequence => sequence.Children,
            _ => Enumerable.Empty<YamlNode>()
        };
        foreach (var child in children.SelectMany(Mappings))
        {
            yield return child;
        }
    }
}
