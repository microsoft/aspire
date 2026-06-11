// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.FileSystemGlobbing;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.SelectTests;

// Strongly-typed view of docs/ci/test-trigger-map.yml, kept internal to the tool so it does not
// add to the public API surface. The verifier tests in Infrastructure.Tests have their own
// parallel model (the test project references this tool, so the model cannot live there and be
// shared without a circular dependency); the design doc sanctions the tool owning its own parse.
internal sealed class TriggerMap
{
    public int Version { get; set; }

    public Dictionary<string, List<string>> Groups { get; set; } = new();

    public TestSelfRule? TestSelf { get; set; }

    public List<string> RunAllGlobs { get; set; } = new();

    public List<PathRule> SharedSource { get; set; } = new();

    public List<JobRule> CuratedJobs { get; set; } = new();

    public List<PathRule> LooseFileDeps { get; set; } = new();

    public List<PathRule> SharedCompiledSource { get; set; } = new();

    // gaps: documents known-uncovered source on purpose, so it is not a selecting rule.
    // ProjectReference closure, CPM, Directory.Build.*, and file-level <Compile Include> are
    // computed at runtime by dotnet-affected (Layer 1), so leaf_source/core_source/test_hubs are
    // intentionally absent here.

    public IEnumerable<PathRule> AllPathRules()
    {
        foreach (var r in SharedSource) { yield return r; }
        foreach (var r in LooseFileDeps) { yield return r; }
        foreach (var r in SharedCompiledSource) { yield return r; }
    }

    // Every job: token the map can ever emit — the "all jobs" set an ALL selection expands to.
    public IEnumerable<string> AllJobTokens() =>
        CuratedJobs.Select(j => j.Target)
            .Where(t => t.StartsWith("job:", StringComparison.Ordinal));

    public static TriggerMap Load(string mapPath)
    {
        var yaml = File.ReadAllText(mapPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<TriggerMap>(yaml);
    }

    // Matches a repo-relative '/'-separated path against a single map glob, using ordinal
    // (case-sensitive) comparison so the match mirrors git's path semantics on Linux CI.
    public static bool GlobMatches(string glob, string path)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude(glob);
        return matcher.Match(new[] { path }).HasMatches;
    }
}

internal sealed class PathRule
{
    public List<string> Paths { get; set; } = new();

    public List<string> Targets { get; set; } = new();

    public string? Note { get; set; }

    public string? Reason { get; set; }

    public int? Fanout { get; set; }
}

internal sealed class JobRule
{
    public string Target { get; set; } = "";

    public string? Reason { get; set; }

    public List<string> Paths { get; set; } = new();
}

internal sealed class TestSelfRule
{
    public string Pattern { get; set; } = "";

    public string Target { get; set; } = "";

    public string? Note { get; set; }
}
