// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Templating;

/// <summary>
/// Renders a template tree that carries a <see cref="TemplateManifest"/>
/// (<c>template.json</c>) at its root. The manifest declares the literal
/// search-and-replace rules, file-name suffix rewrites, and conditional blocks;
/// the caller supplies the symbol values (project name, ports, versions, derived
/// names, …) and condition values. This is the manifest-driven layer on top of
/// the generic <see cref="TemplateRenderer"/>: it converts the declarative
/// manifest plus the supplied symbol/condition tables into the content and path
/// transformers the renderer needs.
/// </summary>
/// <remarks>
/// Keeping computation (ports, GUIDs, package versions, derived names) in C# and
/// keeping declaration (which literals map to which symbol) in <c>template.json</c>
/// is deliberate: the JSON never has to express logic, which keeps it simple for
/// a future third-party template-authoring experience while the CLI remains the
/// single source of truth for how values are produced.
/// </remarks>
internal sealed class ManifestTemplateRenderer
{
    private readonly ILogger _logger;

    public ManifestTemplateRenderer(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Loads the manifest from <paramref name="source"/>, validates that every
    /// referenced symbol and condition has a supplied value, then renders the
    /// tree (excluding the manifest itself) into <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="source">The template source; must contain a <c>template.json</c> at its root.</param>
    /// <param name="outputPath">Directory to render into; created if missing.</param>
    /// <param name="symbols">Values for every symbol referenced by the manifest's replacements.</param>
    /// <param name="conditions">Values for every condition declared by the manifest.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task RenderAsync(
        ITemplateSource source,
        string outputPath,
        IReadOnlyDictionary<string, string> symbols,
        IReadOnlyDictionary<string, bool> conditions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(conditions);

        var files = source.EnumerateFiles();
        var manifestFile = files.FirstOrDefault(f => string.Equals(f.RelativePath, TemplateManifest.FileName, StringComparison.Ordinal))
            ?? throw new TemplateManifestException(
                $"The template source does not contain a '{TemplateManifest.FileName}' manifest at its root.");

        TemplateManifest manifest;
        using (var manifestStream = manifestFile.OpenRead())
        {
            manifest = TemplateManifest.Parse(manifestStream);
        }

        manifest.EnsureSatisfiedBy(symbols, conditions);

        // Process only the conditions the manifest declares, in declared order, so
        // a stray supplied condition can't silently alter rendering and a missing
        // one is caught by EnsureSatisfiedBy above.
        var effectiveConditions = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var condition in manifest.Conditions)
        {
            effectiveConditions[condition] = conditions[condition];
        }

        // Resolve each replacement's `from` literal to its supplied symbol VALUE
        // once, split by where it applies. The lists preserve manifest order so a
        // tie between two equal-length literals is broken deterministically.
        var contentRules = manifest.Replacements
            .Where(static r => r.AppliesToContent)
            .Select(r => (r.From, To: symbols[r.To]))
            .ToArray();
        var pathRules = manifest.Replacements
            .Where(static r => r.AppliesToPath)
            .Select(r => (r.From, To: symbols[r.To]))
            .ToArray();

        string ApplyContentTransform(string content)
        {
            content = ReplaceAll(content, contentRules);
            // ConditionalBlockProcessor.Process resolves the declared blocks and
            // throws on any leftover marker (a condition the manifest didn't declare).
            content = ConditionalBlockProcessor.Process(content, effectiveConditions);
            return content;
        }

        string ApplyPathTransform(string segment)
        {
            // Decide the suffix rewrite from the ORIGINAL authored segment so a
            // symbol value that happens to end with a rename suffix (e.g. a project
            // name ending in "._csproj") can't trigger a spurious rewrite. Only a
            // suffix the template author actually wrote on disk is rewritten.
            TemplateFileRename? matchedRename = null;
            foreach (var rename in manifest.FileRenames)
            {
                if (segment.EndsWith(rename.FromSuffix, StringComparison.Ordinal))
                {
                    matchedRename = rename;
                    break;
                }
            }

            var transformed = ReplaceAll(segment, pathRules);

            if (matchedRename is { } fr && transformed.EndsWith(fr.FromSuffix, StringComparison.Ordinal))
            {
                transformed = string.Concat(transformed.AsSpan(0, transformed.Length - fr.FromSuffix.Length), fr.ToSuffix);
            }

            return transformed;
        }

        _logger.LogDebug(
            "Rendering manifest-driven template to '{OutputPath}' ({ReplacementCount} replacements, {ConditionCount} conditions).",
            outputPath,
            manifest.Replacements.Count,
            manifest.Conditions.Count);

        var renderSource = new FilteringTemplateSource(
            source,
            f => !string.Equals(f.RelativePath, TemplateManifest.FileName, StringComparison.Ordinal));
        var renderer = new TemplateRenderer(_logger);
        await renderer.RenderAsync(renderSource, outputPath, ApplyContentTransform, cancellationToken, ApplyPathTransform);
    }

    /// <summary>
    /// Replaces every occurrence of each rule's <c>From</c> literal with its
    /// <c>To</c> value in a single left-to-right pass. Crucially, the text emitted
    /// for a match is NOT rescanned, so a replacement value that happens to contain
    /// another rule's <c>From</c> literal cannot be rewritten again (the hazard of
    /// chained <see cref="string.Replace(string, string)"/> calls). At each position
    /// the longest matching literal wins so a short literal cannot shadow a longer
    /// one; ties between equal-length literals are resolved by manifest order.
    /// This mirrors how the .NET template engine applies its replacement tokens.
    /// </summary>
    private static string ReplaceAll(string input, IReadOnlyList<(string From, string To)> rules)
    {
        if (rules.Count == 0)
        {
            return input;
        }

        var builder = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            var matchedLength = 0;
            string? matchedTo = null;
            foreach (var (from, to) in rules)
            {
                if (from.Length > matchedLength
                    && i + from.Length <= input.Length
                    && string.CompareOrdinal(input, i, from, 0, from.Length) == 0)
                {
                    matchedLength = from.Length;
                    matchedTo = to;
                }
            }

            if (matchedTo is not null)
            {
                builder.Append(matchedTo);
                i += matchedLength;
            }
            else
            {
                builder.Append(input[i]);
                i++;
            }
        }

        return builder.ToString();
    }
}
