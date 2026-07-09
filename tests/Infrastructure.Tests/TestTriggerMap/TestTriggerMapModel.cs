// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.FileSystemGlobbing;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Strongly-typed view of <c>eng/github-ci/test-trigger-map.yml</c>. The map is curated by hand,
/// so the verifier tests in <see cref="TestTriggerMapTests"/> load this model and assert it
/// stays consistent with repo reality (referenced projects/jobs exist, every source project
/// is reachable by some rule, etc.).
/// </summary>
public sealed class TestTriggerMap
{
    public int Version { get; set; }

    public Dictionary<string, List<string>> Groups { get; set; } = new();

    public List<ConventionRule> Conventions { get; set; } = new();

    public PrefilterConfig? Prefilter { get; set; }

    public List<string> Ignore { get; set; } = new();

    public List<PathRule> PathRules { get; set; } = new();

    public List<AffectedProjectRule> AffectedProjectRules { get; set; } = new();

    public List<DerivedRule> DerivedTargets { get; set; } = new();

    /// <summary>
    /// The <c>conventions</c> patterns as plain globs (the <c>&lt;name&gt;</c> capture replaced by
    /// <c>*</c>), so coverage/dead-glob checks can match them against tracked files.
    /// </summary>
    public IEnumerable<string> ConventionGlobs() =>
        Conventions.Select(c => c.Pattern.Replace("<name>", "*", StringComparison.Ordinal));

    /// <summary>
    /// Every glob that, when matched, accounts for a changed file — the convention patterns
    /// (globified), the ignore globs, and every path-rule glob. Drives source-project coverage.
    /// </summary>
    public IEnumerable<string> AllSelectingGlobs()
    {
        foreach (var g in ConventionGlobs()) { yield return g; }
        foreach (var g in Ignore) { yield return g; }
        foreach (var r in PathRules)
        {
            foreach (var p in r.Paths) { yield return p; }
        }
    }

    /// <summary>
    /// Every concrete target string (<c>test:*</c> / <c>job:*</c> / <c>ALL</c> / group name)
    /// referenced anywhere in the map: group members, every path rule's targets, and the
    /// derived-target rules (both the keyed test and its targets). The <c>conventions</c> templates
    /// are excluded because they carry a <c>&lt;name&gt;</c> placeholder, not a concrete ref.
    /// </summary>
    public IEnumerable<string> AllReferencedTargets()
    {
        foreach (var members in Groups.Values)
        {
            foreach (var t in members) { yield return t; }
        }
        foreach (var r in PathRules)
        {
            foreach (var t in r.Targets) { yield return t; }
        }
        foreach (var r in AffectedProjectRules)
        {
            foreach (var t in r.Targets) { yield return t; }
        }
        foreach (var d in DerivedTargets)
        {
            foreach (var t in d.Tests) { yield return t; }
            foreach (var t in d.Targets) { yield return t; }
        }
    }

    /// <summary>
    /// Expands a <c>conventions</c> rule against a changed file (mirror of the tool's
    /// <c>TriggerMap.TryExpandConvention</c>): a single <c>&lt;name&gt;</c> captures one path
    /// segment and a trailing <c>/**</c> matches any file under it. The caller applies the existence
    /// guard (only a derived test that is a real matrix project is selected).
    /// </summary>
    public static bool TryExpandConvention(ConventionRule rule, string path, out string target)
    {
        target = "";

        var pattern = rule.Pattern;
        var hasGlobSuffix = pattern.EndsWith("/**", StringComparison.Ordinal);
        var core = hasGlobSuffix ? pattern[..^"/**".Length] : pattern;

        var nameIndex = core.IndexOf("<name>", StringComparison.Ordinal);
        if (nameIndex < 0)
        {
            return false;
        }

        var before = core[..nameIndex];
        var after = core[(nameIndex + "<name>".Length)..];
        var regex = "^" + System.Text.RegularExpressions.Regex.Escape(before) + "([^/]+)" +
            System.Text.RegularExpressions.Regex.Escape(after) + (hasGlobSuffix ? "/.+" : "") + "$";

        var match = System.Text.RegularExpressions.Regex.Match(path, regex);
        if (!match.Success)
        {
            return false;
        }

        target = rule.Target.Replace("<name>", match.Groups[1].Value, StringComparison.Ordinal);
        return true;
    }

    /// <summary>Matches a repo-relative '/'-separated path against a single map glob.</summary>
    public static bool GlobMatches(string glob, string path)
    {
        var matcher = new Matcher(StringComparison.Ordinal);
        matcher.AddInclude(glob);
        return matcher.Match(new[] { path }).HasMatches;
    }

    public static TestTriggerMap Load(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "eng", "github-ci", "test-trigger-map.yml");
        var yaml = File.ReadAllText(path);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        return deserializer.Deserialize<TestTriggerMap>(yaml);
    }
}

/// <summary>A rule keyed by one or more path globs that selects a set of targets.</summary>
public sealed class PathRule
{
    public List<string> Paths { get; set; } = new();

    public List<string> Targets { get; set; } = new();

    public string? Note { get; set; }

    public string? Reason { get; set; }
}

/// <summary>A <c>conventions</c> entry: a capture pattern with a single <c>&lt;name&gt;</c>
/// placeholder and a target template that substitutes the captured segment.</summary>
public sealed class ConventionRule
{
    public string Pattern { get; set; } = "";

    public string Target { get; set; } = "";
}

/// <summary>A <c>derived_targets</c> entry: selecting any of <see cref="Tests"/> adds <see cref="Targets"/>.</summary>
public sealed class DerivedRule
{
    public List<string> Tests { get; set; } = new();

    public List<string> Targets { get; set; } = new();

    public string? Reason { get; set; }
}

/// <summary>An <c>affected_project_rules</c> entry: an affected project (Layer 1) matching one of
/// <see cref="Projects"/> by name glob adds <see cref="Targets"/>.</summary>
public sealed class AffectedProjectRule
{
    public List<string> Projects { get; set; } = new();

    public List<string> Targets { get; set; } = new();

    public string? Reason { get; set; }
}

/// <summary>The <c>prefilter</c> block: the patterns file (CI skip-gate list) read at runtime and the
/// keep_routed carve-outs the selector routes to a target.</summary>
public sealed class PrefilterConfig
{
    public string? PatternsFile { get; set; }

    public List<string> KeepRouted { get; set; } = new();
}
