// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace AnalyzeCiFailure;

/// <summary>Renders embedded Markdown layouts without interpreting substituted diagnostic text as template syntax.</summary>
internal static partial class MarkdownTemplate
{
    internal static string Render(string name, params (string Name, string Value)[] values)
    {
        using var stream = typeof(MarkdownTemplate).Assembly.GetManifestResourceStream($"AnalyzeCiFailure.Templates.{name}.md.tmpl")
            ?? throw new InvalidOperationException($"Embedded Markdown template '{name}' was not found.");
        using var reader = new StreamReader(stream);
        var template = reader.ReadToEnd().ReplaceLineEndings("\n");
        var replacements = values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);

        // Placeholders are ##UPPER_CASE_NAME##. Replace only the original template's
        // matches: diagnostic text containing another placeholder must remain literal.
        var rendered = NamedPlaceholderRegex().Replace(
            template,
            match =>
        {
            var placeholder = match.Groups["name"].Value;
            if (!replacements.TryGetValue(placeholder, out var value))
            {
                throw new InvalidOperationException($"Markdown template '{name}' requires placeholder '{placeholder}'.");
            }
            used.Add(placeholder);
            return value;
        });
        foreach (var placeholder in replacements.Keys)
        {
            if (!used.Contains(placeholder))
            {
                throw new InvalidOperationException($"Markdown template '{name}' does not contain placeholder '{placeholder}'.");
            }
        }

        return rendered;
    }

    [GeneratedRegex("##(?<name>[A-Z][A-Z0-9_]*)##")]
    private static partial Regex NamedPlaceholderRegex();
}
