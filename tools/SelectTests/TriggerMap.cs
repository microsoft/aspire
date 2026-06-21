// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.SelectTests;

// Strongly-typed view of eng/github-ci/test-trigger-map.yml, kept internal to the tool so it does not
// add to the public API surface. The verifier tests in Infrastructure.Tests have their own
// parallel model (the test project references this tool, so the model cannot live there and be
// shared without a circular dependency); the design doc sanctions the tool owning its own parse.
internal sealed class TriggerMap
{
    public int Version { get; set; }

    public Dictionary<string, List<string>> Groups { get; set; } = new();

    public List<ConventionRule> Conventions { get; set; } = new();

    // Pre-Layer-1 exclude config: read the CI skip-gate patterns file at runtime and drop matching
    // changed files before both layers, except the keep_routed carve-outs. See ChangedFileFilter.
    public PrefilterConfig? Prefilter { get; set; }

    public List<string> Ignore { get; set; } = new();

    public List<PathRule> PathRules { get; set; } = new();

    public List<AffectedProjectRule> AffectedProjectRules { get; set; } = new();

    public List<DerivedRule> DerivedTargets { get; set; } = new();

    // Five matchers exist; a section is a key only when the selector treats it differently.
    // The graph closure (ProjectReference, CPM, Directory.Build.*, foreign <Compile Include>) is
    // computed at runtime by the Layer 1 graph (GraphAffectedProjects), so those edges are intentionally absent.
    // affected_project_rules are distinct from path_rules because they key off the affected PROJECT
    // set (Layer 1) by project name, not off changed file paths.

    // Every job: token the map can ever emit -- the "all jobs" set an ALL selection expands to.
    // Collected from every section that can carry a job: target (path-rule targets, derived
    // targets, and group members), so a job referenced only by a derived rule is still part of ALL.
    public IEnumerable<string> AllJobTokens()
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in PathRules)
        {
            foreach (var t in rule.Targets)
            {
                AddJobTokensFromTarget(tokens, t);
            }
        }

        foreach (var d in DerivedTargets)
        {
            foreach (var t in d.Targets)
            {
                AddJobTokensFromTarget(tokens, t);
            }
        }

        foreach (var rule in AffectedProjectRules)
        {
            foreach (var t in rule.Targets)
            {
                AddJobTokensFromTarget(tokens, t);
            }
        }

        foreach (var members in Groups.Values)
        {
            foreach (var m in members)
            {
                AddIfJob(tokens, m);
            }
        }

        return tokens;
    }

    // A target may be a job: token directly or a group name whose members include job: tokens.
    private void AddJobTokensFromTarget(HashSet<string> tokens, string target)
    {
        if (target.StartsWith("job:", StringComparison.Ordinal))
        {
            tokens.Add(target);
        }
        else if (Groups.TryGetValue(target, out var members))
        {
            foreach (var m in members)
            {
                AddIfJob(tokens, m);
            }
        }
    }

    private static void AddIfJob(HashSet<string> tokens, string token)
    {
        if (token.StartsWith("job:", StringComparison.Ordinal))
        {
            tokens.Add(token);
        }
    }

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

    // Matches a project NAME (the Layer 1 graph's affected name, == the .csproj base name) against a
    // project_rules pattern, supporting simple '*'/'?' wildcards (e.g. "Aspire.Hosting*" matches
    // every hosting project by name). This is over the project-NAME string space, not file paths,
    // so a project_rule survives a project moving directories and follows the graph's transitive
    // closure (a dependency change marks the project affected). Ordinal/case-sensitive to mirror CI.
    public static bool ProjectNameMatches(string pattern, string name) =>
        System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: false);

    // Expands a path_conventions rule against a changed file. The pattern carries a single <name>
    // placeholder for one path segment and a trailing "/**"; e.g. "src/Components/<name>/**" against
    // "src/Components/Aspire.Npgsql/Foo.cs" captures name="Aspire.Npgsql" and substitutes it into the
    // target template ("test:<name>.Tests" -> "test:Aspire.Npgsql.Tests"). The caller applies the
    // existence guard (only emit when the derived test project is in the matrix).
    public static bool TryExpandConvention(ConventionRule rule, string path, out string target)
    {
        target = "";

        var pattern = rule.Pattern;
        // Only the "<prefix><name>/**" shape is supported; the trailing /** matches any file under
        // the captured directory (including nested files).
        var hasGlobSuffix = pattern.EndsWith("/**", StringComparison.Ordinal);
        var core = hasGlobSuffix ? pattern[..^"/**".Length] : pattern;

        var nameIndex = core.IndexOf("<name>", StringComparison.Ordinal);
        if (nameIndex < 0)
        {
            return false;
        }

        var before = core[..nameIndex];
        var after = core[(nameIndex + "<name>".Length)..];
        var regex = "^" + Regex.Escape(before) + "([^/]+)" + Regex.Escape(after) + (hasGlobSuffix ? "/.+" : "") + "$";

        var match = Regex.Match(path, regex);
        if (!match.Success)
        {
            return false;
        }

        target = rule.Target.Replace("<name>", match.Groups[1].Value, StringComparison.Ordinal);
        return true;
    }
}

// prefilter config: read the CI skip-gate patterns file at runtime and drop matching changed files
// before both layers, except keep_routed (files the selector routes to a target). See ChangedFileFilter.
internal sealed class PrefilterConfig
{
    public string? PatternsFile { get; set; }

    public List<string> KeepRouted { get; set; } = new();
}

internal sealed class PathRule
{
    public List<string> Paths { get; set; } = new();

    public List<string> Targets { get; set; } = new();

    public string? Note { get; set; }

    public string? Reason { get; set; }
}

// affected_project_rules entry: when ANY affected project (Layer 1) matches one of Projects by name
// glob, add Targets. Replaces the duplicated src/<Project>/** path globs that previously drove the
// non-.NET jobs, and is more robust: it follows the graph's transitive closure rather than literal
// file paths.
internal sealed class AffectedProjectRule
{
    public List<string> Projects { get; set; } = new();

    public List<string> Targets { get; set; } = new();

    public string? Reason { get; set; }
}

// conventions entry: a capture pattern (with a single <name> placeholder for one path segment) and
// a target template that substitutes the captured <name>.
internal sealed class ConventionRule
{
    public string Pattern { get; set; } = "";

    public string Target { get; set; } = "";
}

// derived_targets entry: when ANY of Tests is selected (by Layer 1 or Layer 2), add Targets.
internal sealed class DerivedRule
{
    public List<string> Tests { get; set; } = new();

    public List<string> Targets { get; set; } = new();

    public string? Reason { get; set; }
}
