// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Templating;

/// <summary>
/// Declares how a template tree is transformed into a rendered project: which
/// literal strings are search-and-replaced (in file content, in path segments,
/// or both), which trailing file-name suffixes are rewritten, and which
/// conditional blocks the template uses. One <c>template.json</c> lives at the
/// root of each template directory.
/// </summary>
/// <remarks>
/// <para>
/// This is a deliberately small, incremental subset of the .NET template
/// engine's <c>template.json</c> model. The goal is to keep template authoring
/// approachable for a future "treat this git repo as a template" experience
/// without leaning on <c>dotnet new</c>/<c>nupkg</c> semantics, so only the
/// concepts our own templates actually need are implemented:
/// </para>
/// <list type="bullet">
/// <item><see cref="Replacements"/> — literal find/replace where the replacement
/// value is supplied at render time under a named symbol. Mirrors the engine's
/// <c>sourceName</c> + <c>replaces</c> idea: a distinctive literal in the
/// template (e.g. <c>Aspire-StarterApplication.1</c>) is replaced everywhere it
/// appears. Computed values (ports, GUIDs, package versions, derived names) are
/// produced by the CLI and passed in as symbol values; the manifest only maps
/// <c>from</c> → symbol name, so the JSON never has to express computation.</item>
/// <item><see cref="FileRenames"/> — trailing-suffix rewrites on path segments
/// (e.g. <c>._csproj</c> → <c>.csproj</c>). The on-disk template files use the
/// underscore-prefixed extension so the repo-wide MSBuild traversal in
/// <c>eng/Build.props</c> does not pick them up as real projects; the rename
/// restores the real extension on output.</item>
/// <item><see cref="Conditions"/> — the set of conditional-block names the
/// template uses (<c>{{#name}}</c>/<c>{{^name}}</c>); their boolean values are
/// supplied at render time and resolved by <see cref="ConditionalBlockProcessor"/>.</item>
/// </list>
/// <para>
/// Intentionally NOT modeled yet (add only when a shipped template needs it):
/// file include/exclude, condition expressions, globbing, computed/derived
/// symbols defined in JSON. Keeping the surface small is a feature.
/// </para>
/// </remarks>
internal sealed class TemplateManifest
{
    /// <summary>
    /// The fixed file name of the manifest at the root of every template tree.
    /// The renderer reads this file and excludes it from the rendered output.
    /// </summary>
    public const string FileName = "template.json";

    private TemplateManifest(
        IReadOnlyList<TemplateReplacement> replacements,
        IReadOnlyList<TemplateFileRename> fileRenames,
        IReadOnlyList<string> conditions)
    {
        Replacements = replacements;
        FileRenames = fileRenames;
        Conditions = conditions;
    }

    /// <summary>Ordered list of literal find/replace rules. Applied in a single
    /// longest-match pass; manifest order breaks equal-length ties.</summary>
    public IReadOnlyList<TemplateReplacement> Replacements { get; }

    /// <summary>Trailing path-segment suffix rewrites (e.g. <c>._csproj</c> → <c>.csproj</c>).</summary>
    public IReadOnlyList<TemplateFileRename> FileRenames { get; }

    /// <summary>Names of conditional blocks used by the template.</summary>
    public IReadOnlyList<string> Conditions { get; }

    /// <summary>
    /// Reads and validates a manifest from a UTF-8 JSON stream. Throws
    /// <see cref="TemplateManifestException"/> when the JSON is malformed or
    /// violates a structural rule (empty <c>from</c>/<c>to</c>, unknown
    /// <c>target</c>, duplicate <c>from</c> within the same target scope, …).
    /// </summary>
    public static TemplateManifest Parse(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        TemplateManifestDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(stream, TemplateManifestJsonContext.Default.TemplateManifestDocument);
        }
        catch (JsonException ex)
        {
            throw new TemplateManifestException($"The template manifest is not valid JSON: {ex.Message}", ex);
        }

        if (document is null)
        {
            throw new TemplateManifestException("The template manifest is empty.");
        }

        var replacements = new List<TemplateReplacement>();
        // Track duplicates per target scope: the same `from` is allowed once for
        // content and once for path because they are applied in independent passes.
        var seenContentFrom = new HashSet<string>(StringComparer.Ordinal);
        var seenPathFrom = new HashSet<string>(StringComparer.Ordinal);

        if (document.Replacements is not null)
        {
            for (var i = 0; i < document.Replacements.Count; i++)
            {
                var entry = document.Replacements[i];

                if (string.IsNullOrEmpty(entry.From))
                {
                    throw new TemplateManifestException($"Replacement #{i} is missing a non-empty 'from'.");
                }

                if (string.IsNullOrWhiteSpace(entry.To))
                {
                    throw new TemplateManifestException($"Replacement '{entry.From}' is missing a non-empty 'to' symbol name.");
                }

                var target = ParseTarget(entry.Target, entry.From);

                if (target is TemplateReplacementTarget.Content or TemplateReplacementTarget.Both && !seenContentFrom.Add(entry.From))
                {
                    throw new TemplateManifestException($"Replacement 'from' value '{entry.From}' is declared more than once for content.");
                }

                if (target is TemplateReplacementTarget.Path or TemplateReplacementTarget.Both && !seenPathFrom.Add(entry.From))
                {
                    throw new TemplateManifestException($"Replacement 'from' value '{entry.From}' is declared more than once for paths.");
                }

                replacements.Add(new TemplateReplacement(entry.From, entry.To, target));
            }
        }

        var fileRenames = new List<TemplateFileRename>();
        if (document.FileRenames is not null)
        {
            for (var i = 0; i < document.FileRenames.Count; i++)
            {
                var entry = document.FileRenames[i];

                if (string.IsNullOrEmpty(entry.FromSuffix))
                {
                    throw new TemplateManifestException($"File rename #{i} is missing a non-empty 'fromSuffix'.");
                }

                // `toSuffix` may legitimately be empty (a suffix that is simply stripped).
                fileRenames.Add(new TemplateFileRename(entry.FromSuffix, entry.ToSuffix ?? string.Empty));
            }
        }

        var conditions = new List<string>();
        if (document.Conditions is not null)
        {
            foreach (var condition in document.Conditions)
            {
                if (string.IsNullOrWhiteSpace(condition))
                {
                    throw new TemplateManifestException("A condition name must be non-empty.");
                }

                // Keep condition names within the grammar that EnsureNoConditionalMarkers
                // can detect ({{#name}}/{{^name}}/{{/name}} where name is
                // [a-zA-Z][A-Za-z0-9_-]*). Allowing a name outside this grammar would
                // let a leftover marker for that condition slip past the safety check.
                if (!IsValidConditionName(condition))
                {
                    throw new TemplateManifestException(
                        $"Condition '{condition}' is not a valid name. Condition names must start with a letter and contain only letters, digits, '_' or '-'.");
                }

                if (conditions.Contains(condition, StringComparer.Ordinal))
                {
                    throw new TemplateManifestException($"Condition '{condition}' is declared more than once.");
                }

                conditions.Add(condition);
            }
        }

        return new TemplateManifest(replacements, fileRenames, conditions);
    }

    /// <summary>
    /// Verifies that every symbol referenced by a replacement and every declared
    /// condition has a value supplied by the caller. Catches manifest/code drift
    /// (a renamed symbol, a forgotten condition) before any file is written.
    /// </summary>
    public void EnsureSatisfiedBy(IReadOnlyDictionary<string, string> symbols, IReadOnlyDictionary<string, bool> conditions)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(conditions);

        foreach (var replacement in Replacements)
        {
            if (!symbols.ContainsKey(replacement.To))
            {
                throw new TemplateManifestException(
                    $"Replacement '{replacement.From}' references symbol '{replacement.To}', but no value was supplied for it.");
            }
        }

        foreach (var condition in Conditions)
        {
            if (!conditions.ContainsKey(condition))
            {
                throw new TemplateManifestException(
                    $"Condition '{condition}' is declared in the manifest, but no value was supplied for it.");
            }
        }
    }

    private static bool IsValidConditionName(string name)
    {
        // Mirrors the marker grammar used by ConditionalBlockProcessor's leftover
        // detector: [a-zA-Z][A-Za-z0-9_-]*.
        if (!char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static TemplateReplacementTarget ParseTarget(string? target, string from)
    {
        // Default to content: the common case is a content-only token. Paths opt
        // in explicitly because only a handful of replacements (the project-name
        // source string) flow into file/directory names.
        return target switch
        {
            null or "" or "content" => TemplateReplacementTarget.Content,
            "path" => TemplateReplacementTarget.Path,
            "both" => TemplateReplacementTarget.Both,
            _ => throw new TemplateManifestException(
                $"Replacement '{from}' has an unknown target '{target}'. Expected 'content', 'path', or 'both'.")
        };
    }
}

/// <summary>Where a <see cref="TemplateReplacement"/> is applied.</summary>
internal enum TemplateReplacementTarget
{
    /// <summary>Applied to file content only.</summary>
    Content,
    /// <summary>Applied to path segments (file and directory names) only.</summary>
    Path,
    /// <summary>Applied to both file content and path segments.</summary>
    Both
}

/// <summary>
/// A single literal find/replace rule. <see cref="From"/> is a literal string in
/// the template; <see cref="To"/> is the name of a symbol whose value is supplied
/// at render time.
/// </summary>
internal sealed record TemplateReplacement(string From, string To, TemplateReplacementTarget Target)
{
    public bool AppliesToContent => Target is TemplateReplacementTarget.Content or TemplateReplacementTarget.Both;

    public bool AppliesToPath => Target is TemplateReplacementTarget.Path or TemplateReplacementTarget.Both;
}

/// <summary>
/// A trailing-suffix rewrite applied to path segments. For example
/// <c>FromSuffix = "._csproj"</c>, <c>ToSuffix = ".csproj"</c> turns
/// <c>MyApp.AppHost._csproj</c> into <c>MyApp.AppHost.csproj</c>.
/// </summary>
internal sealed record TemplateFileRename(string FromSuffix, string ToSuffix);

/// <summary>Thrown when a <c>template.json</c> manifest is invalid or cannot be satisfied.</summary>
internal sealed class TemplateManifestException : Exception
{
    public TemplateManifestException(string message)
        : base(message)
    {
    }

    public TemplateManifestException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

// JSON shape of template.json. Kept separate from the validated domain model so
// the wire format (nullable, lenient) is distinct from the in-memory invariants.
internal sealed class TemplateManifestDocument
{
    public List<TemplateReplacementEntry>? Replacements { get; set; }

    public List<TemplateFileRenameEntry>? FileRenames { get; set; }

    public List<string>? Conditions { get; set; }
}

internal sealed class TemplateReplacementEntry
{
    public string? From { get; set; }

    public string? To { get; set; }

    public string? Target { get; set; }
}

internal sealed class TemplateFileRenameEntry
{
    public string? FromSuffix { get; set; }

    public string? ToSuffix { get; set; }
}

// Source-generated (de)serialization so the parser is trim/NativeAOT safe — the
// CLI is published with NativeAOT and must not rely on reflection-based STJ.
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TemplateManifestDocument))]
internal sealed partial class TemplateManifestJsonContext : JsonSerializerContext
{
}
