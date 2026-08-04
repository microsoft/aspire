// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.SelectTests;

// The pre-filter stage of selective CI: drops changed files that require no CI at all (documentation,
// agent skills/instructions, loose scripts, ...), reading the SAME pattern list the top-level ci.yml
// skip gate uses -- eng/github-ci/ci-skip-entirely-patterns.txt -- so the two can never drift.
//
// Applied to the changed-file input of BOTH layers, BEFORE Layer 1 and Layer 2 run, so an excluded
// file influences no selection. That is the crucial difference from `ignore:` (which only suppresses
// the Layer 2 run-all fallback while Layer 1 still git-diffs and attributes the file -- e.g. a README.md
// is a packed <None> item, so the graph would otherwise fan it out to the owning project's test closure).
//
// keep_routed (from the map's `prefilter` block) are carve-outs: files the patterns file lists but the
// selector deliberately routes to a target (.github/workflows/** and eng/pipelines/** -> Infrastructure
// .Tests, and the patterns file itself, which is a selector input). Those are never dropped.
//
// Pattern semantics MUST match the check-changed-files action (.github/actions/check-changed-files), so
// the selector's "excluded" set equals the gate's "skip" set. The action interprets each pattern with
// its own glob_to_regex (NOT Microsoft.Extensions.FileSystemGlobbing): `**` -> any chars incl. '/',
// `*` -> any chars except '/', `.` literal, anchored. GlobToRegex below ports that verbatim. The
// keep_routed carve-outs use the ordinary map glob matcher (TriggerMap.GlobMatches).
internal sealed class ChangedFileFilter
{
    private static readonly ChangedFileFilter s_empty = new(Array.Empty<Regex>(), Array.Empty<string>());

    private readonly IReadOnlyList<Regex> _patternRegexes;
    private readonly IReadOnlyList<string> _keepRoutedGlobs;

    private ChangedFileFilter(IReadOnlyList<Regex> patternRegexes, IReadOnlyList<string> keepRoutedGlobs)
    {
        _patternRegexes = patternRegexes;
        _keepRoutedGlobs = keepRoutedGlobs;
    }

    public static ChangedFileFilter Create(string repoRoot, PrefilterConfig? prefilter)
    {
        if (prefilter?.PatternsFile is null)
        {
            return s_empty;
        }

        var patternsPath = Path.Combine(repoRoot, prefilter.PatternsFile);
        if (!File.Exists(patternsPath))
        {
            throw new InvalidOperationException($"prefilter patterns_file not found: {patternsPath}");
        }

        // Skip blank lines and '#' comments, exactly like the action.
        var regexes = File.ReadAllLines(patternsPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(GlobToRegex)
            .ToList();

        return new ChangedFileFilter(regexes, prefilter.KeepRouted.ToList());
    }

    // True when the repo-relative ('/'-separated) path should be dropped before selection: it matches a
    // patterns_file glob AND is not carved out by keep_routed. keep_routed wins, so a routed file is
    // never dropped even if the patterns file would otherwise skip it.
    public bool IsExcluded(string repoRelativePath)
    {
        foreach (var glob in _keepRoutedGlobs)
        {
            if (TriggerMap.GlobMatches(glob, repoRelativePath))
            {
                return false;
            }
        }

        foreach (var regex in _patternRegexes)
        {
            if (regex.IsMatch(repoRelativePath))
            {
                return true;
            }
        }

        return false;
    }

    // Verbatim port of .github/actions/check-changed-files/action.yml `glob_to_regex`:
    //   **  -> .*        (any chars including '/')
    //   *   -> [^/]*     (any chars except '/')
    //   .   -> \.        (literal dot)
    //   the same regex metacharacters the action escapes (\ . + ? [ ] ( ) |) are escaped; { } ^ $ are
    //   intentionally left unescaped (the action does the same), then the result is anchored ^...$.
    private static Regex GlobToRegex(string glob)
    {
        // Reserve placeholders for the two glob stars so escaping below cannot touch them. Use control
        // chars that cannot appear in a repo path.
        const char doubleStar = '\u0001';
        const char star = '\u0002';

        var withPlaceholders = glob
            .Replace("**", doubleStar.ToString(), StringComparison.Ordinal)
            .Replace("*", star.ToString(), StringComparison.Ordinal);

        var sb = new StringBuilder(withPlaceholders.Length + 8);
        foreach (var c in withPlaceholders)
        {
            switch (c)
            {
                case doubleStar:
                    sb.Append(".*");
                    break;
                case star:
                    sb.Append("[^/]*");
                    break;
                // Metacharacters the action escapes.
                case '\\':
                case '.':
                case '+':
                case '?':
                case '[':
                case ']':
                case '(':
                case ')':
                case '|':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return new Regex("^" + sb + "$", RegexOptions.CultureInvariant);
    }
}
