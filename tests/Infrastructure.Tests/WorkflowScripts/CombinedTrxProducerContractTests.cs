// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias AnalyzeCiFailureTool;

using System.IO.Enumeration;
using System.Text.Json.Nodes;
using Xunit;
using Xunit.Sdk;
using YamlDotNet.RepresentationModel;
using Analyzer = AnalyzeCiFailureTool::AnalyzeCiFailure;

namespace Infrastructure.Tests;

/// <summary>Feeds discovered workflow naming and aggregate layout metadata through the real analyzer.</summary>
public sealed class CombinedTrxProducerContractTests
{
    [Theory]
    [InlineData("Hosting", "ubuntu-latest", "Hosting_net10.0_20261103065037.trx")]
    [InlineData("Hosting-2", "8-core-ubuntu-latest", "Hosting-2_net11.0_20261103065037.trx")]
    [InlineData("Hosting-2", "windows-latest", "Hosting-2.trx")]
    [InlineData("Templates", "macos-latest", "Templates-AppHostTemplateTests.trx")]
    [InlineData("Infrastructure-Collection", "8-core-ubuntu-latest", "Infrastructure-Collection_net10.0_20261103065037.trx")]
    public void WorkflowMetadataPreservesProducerAndSourceArtifactIdentity(string shortName, string runner, string reportName)
        => AssertContract(Load("run-tests.yml"), Load("tests.yml"), shortName, runner, reportName);

    [Theory]
    [InlineData("job")]
    [InlineData("artifact")]
    [InlineData("directory")]
    [InlineData("upload-root")]
    [InlineData("missing-downloads")]
    [InlineData("unsupported-expression")]
    public void NamingAndLayoutDriftBreaksTheConsumerContract(string mutation)
    {
        var producer = Load("run-tests.yml");
        var aggregate = Load("tests.yml");
        var testJob = Mapping(Mapping(producer, "jobs"), "test");
        var aggregateSteps = AggregateSteps(aggregate);
        if (mutation is "job" or "unsupported-expression")
        {
            testJob.Children["name"] = new YamlScalarNode(mutation == "job"
                ? "${{ inputs.testShortName }}-renamed (${{ inputs.os }})"
                : "${{ inputs.unknown }} (${{ inputs.os }})");
        }
        else if (mutation == "artifact")
        {
            var logs = Assert.Single(Steps(testJob), step => Text(step, "id") == "upload-logs");
            Mapping(logs, "with").Children["name"] = new YamlScalarNode("logs-renamed-${{ inputs.testShortName }}-${{ inputs.os }}");
        }
        else if (mutation == "upload-root")
        {
            var upload = Assert.Single(aggregateSteps, step => IsAction(step, "actions/upload-artifact") &&
                Text(Mapping(step, "with"), "name") == "All-TestResults");
            Mapping(upload, "with").Children["path"] = new YamlScalarNode("${{ github.workspace }}/different/**/*.trx");
        }
        else
        {
            foreach (var download in aggregateSteps.Where(step => IsAction(step, "actions/download-artifact")))
            {
                Mapping(download, "with").Children[mutation == "directory" ? "path" : "pattern"] =
                    new YamlScalarNode(mutation == "directory" ? "${{ github.workspace }}/testresults/different" : "unrelated-*");
            }
        }

        Assert.ThrowsAny<XunitException>(() => AssertContract(
            producer, aggregate, "Hosting-2", "8-core-ubuntu-latest", "Hosting-2_net11.0_20261103065037.trx"));
    }

    private static void AssertContract(YamlMappingNode producer, YamlMappingNode aggregate, string shortName, string runner, string reportName)
    {
        // These representative report names are inputs to the consumer, not proof
        // that RepoTesting.targets emits them. The advisory job checks real files.
        var workspace = Path.GetFullPath(Path.Combine(RepoRoot.Path, "artifacts", "workflow-contract-workspace"));
        string Resolve(string? value)
        {
            Assert.False(string.IsNullOrEmpty(value), "Expected nonempty workflow naming/layout metadata.");
            var resolved = value.Replace("${{ inputs.testShortName }}", shortName, StringComparison.Ordinal)
                .Replace("${{ inputs.os }}", runner, StringComparison.Ordinal)
                .Replace("${{ github.workspace }}", workspace, StringComparison.Ordinal);
            Assert.False(resolved.Contains("${{", StringComparison.Ordinal),
                $"Unsupported workflow expression in contract metadata: {value}. Update this bounded fixture resolver.");
            return resolved;
        }

        var testJob = Mapping(Mapping(producer, "jobs"), "test");
        var steps = AggregateSteps(aggregate);
        var downloads = steps.Where(step => IsAction(step, "actions/download-artifact") &&
            Text(Mapping(step, "with"), "pattern") is not null).ToArray();
        var matches = (from upload in Steps(testJob)
                       where IsAction(upload, "actions/upload-artifact")
                       let artifact = Resolve(Text(Mapping(upload, "with"), "name"))
                       from download in downloads
                       where FileSystemName.MatchesSimpleExpression(Resolve(Text(Mapping(download, "with"), "pattern")), artifact, ignoreCase: false)
                       select (Upload: upload, Artifact: artifact, Download: download)).ToArray();
        Assert.True(matches.Length == 1, $"Expected exactly one producer artifact/aggregate download association for {shortName} ({runner}); found {matches.Length}.");
        var match = matches[0];
        var jobName = "Tests / No-package tests / " + Resolve(Text(testJob, "name"));
        var plan = Assert.Single(new Analyzer.AspireCiWorkflowConventions().DescribeTestArtifacts(new JsonObject
        {
            ["name"] = jobName,
            ["steps"] = new JsonArray(new JsonObject { ["name"] = Text(match.Upload, "name") }),
        }, runAttempt: 1));
        Assert.Equal(match.Artifact, plan.ArtifactName);

        var allResults = Assert.Single(steps, step => IsAction(step, "actions/upload-artifact") &&
            Text(Mapping(step, "with"), "name") == "All-TestResults");
        var uploadPath = Resolve(Text(Mapping(allResults, "with"), "path"));
        Assert.True(uploadPath.EndsWith("/**/*.trx", StringComparison.Ordinal),
            "Unsupported All-TestResults upload layout; update contract discovery to model the new glob.");
        var uploadRoot = Path.GetFullPath(uploadPath[..^"/**/*.trx".Length], workspace);
        var downloadRoot = Path.GetFullPath(Resolve(Text(Mapping(match.Download, "with"), "path")), workspace);
        var entry = Path.GetRelativePath(uploadRoot, Path.Combine(downloadRoot, "testresults", reportName))
            .Replace(Path.DirectorySeparatorChar, '/');
        var reports = new Analyzer.CombinedTrxReports([(entry, "00001.trx")], [plan]);

        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(plan));
    }

    private static YamlMappingNode[] AggregateSteps(YamlMappingNode root)
    {
        var jobs = Mapping(root, "jobs").Children.Values.OfType<YamlMappingNode>()
            .Where(job => Steps(job).Any(step => IsAction(step, "actions/upload-artifact") &&
                Text(Mapping(step, "with"), "name") == "All-TestResults")).ToArray();
        Assert.True(jobs.Length == 1, "Expected one All-TestResults producer job; update contract discovery if the workflow structure changes.");
        return Steps(jobs[0]);
    }

    private static bool IsAction(YamlMappingNode step, string action)
        => Text(step, "uses")?.Split('@')[0] == action;

    private static YamlMappingNode[] Steps(YamlMappingNode job)
        => job.Children.TryGetValue("steps", out var value) && value is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlMappingNode>().ToArray() : [];

    private static string? Text(YamlMappingNode node, string key)
        => node.Children.TryGetValue(key, out var value) ? Assert.IsType<YamlScalarNode>(value).Value : null;

    private static YamlMappingNode Mapping(YamlMappingNode node, string key)
        => Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static YamlMappingNode Load(string name)
    {
        var yaml = new YamlStream();
        using var reader = File.OpenText(Path.Combine(RepoRoot.Path, ".github", "workflows", name));
        yaml.Load(reader);
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }
}
