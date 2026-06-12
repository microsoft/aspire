// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Tests;

/// <summary>
/// Regression guardrail for the CLI identity migration (see <c>docs/specs/cli-identity-sidecar.md</c>).
/// </summary>
/// <remarks>
/// Every version/commit decision that should honor the CLI's resolved identity
/// (<c>ASPIRE_CLI_VERSION</c> / <c>ASPIRE_CLI_COMMIT</c> / the install sidecar) must read it from
/// <c>CliExecutionContext</c> rather than going to the assembly directly. The "default version"
/// helpers (<c>VersionHelper.GetDefaultTemplateVersion()</c> / <c>GetDefaultSdkVersion()</c>) and the
/// raw assembly readers (<c>PackageUpdateHelpers.GetCurrentAssemblyVersion()</c> /
/// <c>GetCurrentPackageVersion()</c>, <c>AssemblyVersionHelper.GetInformationalVersion()</c>) bypass
/// identity, so a NEW call to any of them silently reintroduces the class of bug this feature fixed.
///
/// This test fails when such a call appears in <c>src/Aspire.Cli</c> outside the curated allow-list of
/// genuinely physical-binary reads (each annotated in source with
/// <c>// physical-binary-version-by-design</c>). If you add an intentional physical read, annotate it
/// and add the file here; if you are making a behavioral version decision, read from
/// <c>CliExecutionContext.IdentityVersion</c> / <c>IdentitySdkVersion</c> / <c>IdentityCommit</c> instead.
/// </remarks>
public class IdentityCallSiteGuardrailTests
{
    // Invocations (note the trailing '(') of the assembly-backed version readers that bypass identity.
    private static readonly Regex s_physicalVersionReadPattern = new(
        @"\b(GetDefaultTemplateVersion|GetDefaultSdkVersion|GetCurrentAssemblyVersion|GetCurrentPackageVersion|GetInformationalVersion)\s*\(",
        RegexOptions.Compiled);

    // Files under src/Aspire.Cli that are PERMITTED to read the physical binary version. Each
    // corresponding source site is annotated with `// physical-binary-version-by-design`. Paths use
    // '/' separators relative to src/Aspire.Cli and are normalized before comparison.
    private static readonly HashSet<string> s_allowList = new(StringComparer.OrdinalIgnoreCase)
    {
        // The identity helper itself: defines GetDefault*Version and is the assembly-fallback source.
        "Utils/VersionHelper.cs",
        // Update check compares the REAL installed binary against the latest available package.
        "Utils/CliUpdateNotifier.cs",
        // The foundation: IdentityVersion falls back to the assembly version when no override is set.
        "CliExecutionContext.cs",
        // Template-noise filter fallback; production channels are fed IdentitySdkVersion by PackagingService.
        "Packaging/PackageChannel.cs",
        // `aspire doctor --self` reports the physical install on disk.
        "Acquisition/InstallationDiscovery.cs",
        // The resolver's own assembly-fallback source for version/commit.
        "Acquisition/IdentityResolver.cs",
        // Test-only parameterless overload; production flows IdentitySdkVersion through the explicit overload.
        "Agents/AspireSkills/AspireSkillsBundle.cs",
        // OTel service version identifies the actual running binary that produced the telemetry.
        "Telemetry/TelemetryManager.cs",
        // Binary cli.version / cli.build_id telemetry tags (identity.* tags are emitted separately).
        "Telemetry/AspireCliTelemetry.cs",
        // Fingerprints the single-file bundle's own binary to detect when re-extraction is needed.
        "Bundles/BundleService.cs",
    };

    [Fact]
    public void NoUnexpectedPhysicalVersionReadsOutsideAllowList()
    {
        var cliSourceRoot = GetCliSourceRoot();

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(cliSourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build output; only first-party source under src/Aspire.Cli matters.
            var relative = NormalizeRelative(Path.GetRelativePath(cliSourceRoot, file));
            if (relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var (lineNumber, codeText) in EnumerateCodeLines(File.ReadAllLines(file)))
            {
                if (s_physicalVersionReadPattern.IsMatch(codeText) && !s_allowList.Contains(relative))
                {
                    offenders.Add($"{relative}:{lineNumber}: {codeText.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Found assembly-backed version read(s) that bypass CLI identity. Read from " +
            "CliExecutionContext.IdentityVersion / IdentitySdkVersion / IdentityCommit instead, or — if the " +
            "read is genuinely physical — annotate it with `// physical-binary-version-by-design` and add the " +
            "file to the allow-list in IdentityCallSiteGuardrailTests. See docs/specs/cli-identity-sidecar.md." +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void AllowListHasNoStaleEntries()
    {
        var cliSourceRoot = GetCliSourceRoot();

        var stale = new List<string>();

        foreach (var entry in s_allowList)
        {
            var fullPath = Path.Combine(cliSourceRoot, entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                stale.Add($"{entry} (file not found)");
                continue;
            }

            var hasPhysicalRead = EnumerateCodeLines(File.ReadAllLines(fullPath))
                .Any(line => s_physicalVersionReadPattern.IsMatch(line.CodeText));

            if (!hasPhysicalRead)
            {
                stale.Add($"{entry} (no physical version read remains)");
            }
        }

        Assert.True(
            stale.Count == 0,
            "The identity guardrail allow-list has stale entries. Remove them so the allow-list stays tight " +
            "and accurately documents the remaining physical-binary reads:" + Environment.NewLine +
            string.Join(Environment.NewLine, stale));
    }

    // Yields (1-based line number, code-only text) for each line, with single-line and block comments
    // stripped so that prose mentions of the guarded APIs (e.g. in XML doc) are not treated as call sites.
    private static IEnumerable<(int LineNumber, string CodeText)> EnumerateCodeLines(string[] lines)
    {
        var inBlockComment = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var code = StripComments(line, ref inBlockComment);
            yield return (i + 1, code);
        }
    }

    private static string StripComments(string line, ref bool inBlockComment)
    {
        var result = new System.Text.StringBuilder(line.Length);

        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            {
                // Rest of the line is a single-line comment.
                break;
            }

            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
            {
                inBlockComment = true;
                i++;
                continue;
            }

            result.Append(line[i]);
        }

        return result.ToString();
    }

    private static string NormalizeRelative(string relativePath)
        => relativePath.Replace('\\', '/');

    private static string GetCliSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        var cliSourceRoot = Path.Combine(dir.FullName, "src", "Aspire.Cli");
        Assert.True(Directory.Exists(cliSourceRoot), $"Expected Aspire.Cli source at {cliSourceRoot}");
        return cliSourceRoot;
    }
}
