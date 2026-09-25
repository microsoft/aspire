// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Guards the build-free partition discovery in <c>eng/scripts/scan-test-partitions-from-source.ps1</c>.
/// The runsheet builder now reads <c>[Trait("Partition","n")]</c> values straight from source for
/// SplitTestsOnCI projects and skips the compiled <c>GenerateTestPartitionsForCI</c> pass when the scan
/// produced a file. That source scan only recognises the STANDALONE form (the trait alone in its
/// <c>[..]</c> brackets, with a string-literal value). A class that carries a Partition trait in a form
/// the scan misses — the combined-attribute form <c>[Trait("Cat","x"), Trait("Partition","7")]</c> or a
/// non-literal value <c>[Trait("Partition", Partitions.Five)]</c> — still has the trait at runtime, so
/// the <c>uncollected:*</c> backstop (<c>--filter-not-trait Partition=*</c>) EXCLUDES it while no
/// <c>collection:&lt;value&gt;</c> shard covers it: the class then runs in NO shard, with no failure
/// signal. The previously-authoritative compiled extractor would have caught every form; the source
/// scan does not. This guard fails the build the moment such a form is introduced, so the latent gap
/// can never become a silent test skip.
/// </summary>
public class ScanTestPartitionsFromSourceGuardTests
{
    // The EXACT regex used by scan-test-partitions-from-source.ps1: a standalone
    // [<ns.>Trait[Attribute]("Partition", "<literal>")] attribute (the trait is the sole content of
    // its [..] brackets). Kept byte-for-byte in sync with the script so this guard tests the script's
    // real capability, not an approximation.
    private static readonly Regex s_standaloneLiteral = new(
        """(?i)\[\s*(?:[\w.]+\.)?Trait(?:Attribute)?\s*\(\s*"Partition"\s*,\s*"([^"]+)"\s*\)\s*\]""",
        RegexOptions.Compiled);

    // Broad detector: ANY Partition trait usage — regardless of what else shares its brackets or
    // whether the value is a string literal. xunit's Trait is only ever an attribute, so a match here
    // that the standalone regex above does not cover is exactly a form the source scan would miss.
    private static readonly Regex s_anyPartitionTrait = new(
        """(?i)(?:[\w.]+\.)?Trait(?:Attribute)?\s*\(\s*"Partition"\s*,""",
        RegexOptions.Compiled);

    [Fact]
    public void EveryPartitionTraitIsInTheStandaloneFormTheSourceScanCaptures()
    {
        var testsRoot = Path.Combine(RepoRoot.Path, "tests");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip generated/copied sources, mirroring the script's own obj/bin exclusion.
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            // This guard file documents the forbidden forms verbatim in its summary/messages, so it
            // would always match itself. It is never a SplitTestsOnCI project's source, so skip it.
            if (Path.GetFileName(file) == "ScanTestPartitionsFromSourceGuardTests.cs")
            {
                continue;
            }

            var content = File.ReadAllText(file);
            if (!content.Contains("Partition", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Index spans the standalone regex covers; any Partition-trait occurrence outside all of
            // them is a form the source scan cannot capture.
            var covered = s_standaloneLiteral.Matches(content)
                .Select(m => (Start: m.Index, End: m.Index + m.Length))
                .ToList();

            foreach (Match m in s_anyPartitionTrait.Matches(content))
            {
                var isCovered = covered.Any(span => m.Index >= span.Start && m.Index < span.End);
                if (!isCovered)
                {
                    var rel = Path.GetRelativePath(RepoRoot.Path, file).Replace('\\', '/');
                    var snippet = content.Substring(m.Index, Math.Min(70, content.Length - m.Index))
                        .ReplaceLineEndings(" ");
                    offenders.Add($"{rel}: …{snippet}…");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Partition trait(s) in a form scan-test-partitions-from-source.ps1 cannot capture (combined " +
            "attribute or non-literal value). Such a class would run in NO CI shard. Use the standalone " +
            "form [Trait(\"Partition\", \"<literal>\")], or update the script's regex AND this guard " +
            $"together:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}");
    }
}
