// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.CodeGeneration.Go;

/// <summary>
/// Identifies what a generated Go doc comment documents. The first sentence of a Go doc comment
/// starts with the documented identifier, and how a summary such as "Gets or sets the port." is
/// reworded to do that depends on whether the identifier is a method, an accessor, a type or a field.
/// </summary>
internal enum GoDocSubject
{
    Method,
    Getter,
    Setter,
    Type,
    Field,
}

/// <summary>
/// A documented parameter. <see cref="Name"/> is the name used by the ATS model and by
/// <c>`name`</c> references in documentation text; <see cref="GoName"/> is the identifier in the generated signature.
/// </summary>
internal sealed record GoDocParameter(string Name, string GoName, string? Description);

/// <summary>
/// Everything a generated doc comment can say about one Go identifier.
/// </summary>
internal sealed class GoDocContent
{
    public required string GoName { get; init; }

    public GoDocSubject Subject { get; init; } = GoDocSubject.Method;

    /// <summary>The documented summary. May be several sentences; only the first block is reworded to start with <see cref="GoName"/>.</summary>
    public string? Summary { get; init; }

    /// <summary>
    /// Used when <see cref="Summary"/> is empty, and as the leading sentence of a setter whose property
    /// documentation cannot be reworded. It receives the same treatment as <see cref="Summary"/>, so a plain
    /// description such as "Sets the port property." becomes a sentence that starts with <see cref="GoName"/>.
    /// </summary>
    public string? FallbackSummary { get; init; }

    public string? Remarks { get; init; }

    public IReadOnlyList<GoDocParameter> Parameters { get; init; } = [];

    public string? Returns { get; init; }

    /// <summary>Additional generator-authored paragraphs, rendered between the remarks and the parameter list.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public bool IsExperimental { get; init; }

    public bool IsObsolete { get; init; }

    public string? ObsoleteMessage { get; init; }
}

/// <summary>
/// Renders ATS documentation metadata as Go doc comments (https://go.dev/doc/comment).
/// </summary>
/// <remarks>
/// The output is the canonical form gofmt produces for doc comments, so formatting the generated
/// file never rewrites a comment: a space after <c>//</c>, <c>//</c> alone between blocks, lists as
/// <c>//   - item</c>, code as <c>//</c> plus a tab, headings as <c># Heading</c>, and no trailing whitespace.
/// The documentation text is LF-only; the caller decides how lines are terminated.
/// </remarks>
internal sealed partial class GoDocComment
{
    // Godoc reflows paragraphs, so the width only keeps the generated source readable.
    private const int MaxLineLength = 78;

    // The wording follows the gRPC-Go convention, the best-known Go precedent for marking unstable APIs:
    // https://github.com/grpc/grpc-go/blob/master/dialoptions.go. The heading renders as a section in
    // pkg.go.dev and in gopls hover, unlike a bare "Experimental:" paragraph.
    private const string ExperimentalHeading = "# Experimental";
    private const string ExperimentalNotice = "Notice: This API is EXPERIMENTAL and may be changed or removed in a later release.";

    // The Deprecated: paragraph is the marker gopls and staticcheck understand; it needs some text to be useful.
    private const string DefaultObsoleteMessage = "This API is obsolete.";

    private const string GetsOrSetsPrefix = "Gets or sets ";
    private const string ValueIndicatingPrefix = "a value indicating whether ";

    // Third-person verbs that open ATS summaries ("Adds a Redis container...", "Represents a connection...").
    // The list is deliberately explicit: a summary that starts with a noun such as "Options for ..." must
    // keep reading naturally instead of being treated as a sentence.
    private static readonly HashSet<string> s_leadingVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Acquires", "Adds", "Allocates", "Allows", "Appends", "Applies", "Assigns", "Associates", "Attaches", "Attempts",
        "Binds", "Builds", "Calls", "Cancels", "Captures", "Carries", "Changes", "Checks", "Cleans", "Clears",
        "Collects", "Compares", "Completes", "Computes", "Configures", "Constructs", "Contains", "Controls", "Converts", "Coordinates",
        "Copies", "Creates", "Declares", "Defines", "Deletes", "Derives", "Describes", "Deserializes", "Detects", "Determines",
        "Disables", "Discovers", "Displays", "Disposes", "Emits", "Enables", "Encapsulates", "Ensures", "Enumerates", "Evaluates",
        "Executes", "Exports", "Exposes", "Extracts", "Fails", "Fetches", "Filters", "Finds", "Fires", "Flattens",
        "Formats", "Forwards", "Gathers", "Generates", "Gets", "Handles", "Hides", "Holds", "Identifies", "Implements",
        "Includes", "Indicates", "Initializes", "Injects", "Inserts", "Inspects", "Installs", "Invokes", "Is", "Launches",
        "Links", "Lists", "Loads", "Logs", "Looks", "Makes", "Manages", "Maps", "Marks", "Matches",
        "Merges", "Models", "Modifies", "Opens", "Orchestrates", "Overrides", "Owns", "Parses", "Polls", "Populates",
        "Prepares", "Presents", "Processes", "Produces", "Projects", "Prompts", "Provides", "Provisions", "Publishes", "Pushes",
        "Queries", "Reads", "Records", "References", "Registers", "Rejects", "Releases", "Removes", "Renders", "Replaces",
        "Reports", "Represents", "Requests", "Resolves", "Retrieves", "Returns", "Routes", "Runs", "Sanitizes", "Saves",
        "Scans", "Selects", "Sends", "Serializes", "Serves", "Sets", "Shares", "Signals", "Signifies", "Skips",
        "Specifies", "Splits", "Starts", "Stops", "Stores", "Subscribes", "Supports", "Suppresses", "Throws", "Tracks",
        "Translates", "Tries", "Unregisters", "Updates", "Uses", "Validates", "Waits", "Walks", "Warns", "Watches",
        "Wires", "Wraps", "Writes",
    };

    // Words that make "Name is <summary>" read correctly ("The default port." -> "Port is the default port.").
    private static readonly HashSet<string> s_leadingDeterminers = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "this", "that",
    };

    // A word ending in "s" followed by one of these is a verb phrase even when the verb is not in the list above.
    private static readonly HashSet<string> s_objectIntroducers = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "this", "that", "these", "those", "its", "their", "each", "all", "any", "whether", "how",
    };

    // "The resource is pending." is a sentence, so "Name is the resource is pending." would not read. A clause that
    // follows one of the subordinators ("a value indicating whether it is enabled") is part of a noun phrase and does.
    private static readonly HashSet<string> s_auxiliaryVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "is", "are", "was", "were", "will", "can", "may", "must", "should", "does", "do", "has", "have",
    };

    private static readonly HashSet<string> s_subordinators = new(StringComparer.OrdinalIgnoreCase)
    {
        "whether", "that", "which", "who", "whose", "if", "when", "where", "while", "how", "because", "unless",
    };

    private readonly Func<string, string?> _resolveTypeName;
    private readonly Func<string, string> _toExportedName;

    /// <param name="resolveTypeName">Maps a short ATS or C# type name to the generated Go type identifier, or <see langword="null"/>.</param>
    /// <param name="toExportedName">Maps a member name in ATS casing to its exported Go identifier.</param>
    public GoDocComment(Func<string, string?> resolveTypeName, Func<string, string> toExportedName)
    {
        _resolveTypeName = resolveTypeName;
        _toExportedName = toExportedName;
    }

    /// <summary>
    /// Renders the complete comment. Each returned line starts with <c>//</c> and carries no indentation.
    /// </summary>
    public IReadOnlyList<string> Render(GoDocContent content)
    {
        var lines = new List<string>();
        var parameterNames = BuildParameterMap(content.Parameters);

        AddBlocks(lines, GetSummaryBlocks(content, parameterNames), guardImplicitHeadings: false);
        AddBlocks(lines, ToBlocks(content.Remarks, parameterNames), guardImplicitHeadings: true);

        foreach (var note in content.Notes)
        {
            var text = Flatten(ConvertInline(note, parameterNames));
            if (text.Length > 0)
            {
                AddBlocks(lines, [new ParagraphBlock(text)], guardImplicitHeadings: false);
            }
        }

        AddParameters(lines, content.Parameters, parameterNames);

        var returns = Flatten(ConvertInline(content.Returns, parameterNames));
        if (returns.Length > 0)
        {
            AddSeparator(lines);
            AddWrapped(lines, $"Returns: {returns}", " ", " ");
        }

        if (content.IsExperimental)
        {
            AddSeparator(lines);
            lines.Add($"// {ExperimentalHeading}");
            AddSeparator(lines);
            AddWrapped(lines, ExperimentalNotice, " ", " ");
        }

        if (content.IsObsolete)
        {
            var message = Flatten(ConvertInline(content.ObsoleteMessage, parameterNames));
            AddSeparator(lines);
            AddWrapped(lines, $"Deprecated: {(message.Length > 0 ? message : DefaultObsoleteMessage)}", " ", " ");
        }

        return lines;
    }

    /// <summary>
    /// Renders only the summary, for implementations whose full documentation lives on the public interface.
    /// </summary>
    public IReadOnlyList<string> RenderSummary(GoDocContent content)
    {
        var lines = new List<string>();
        AddBlocks(lines, GetSummaryBlocks(content, BuildParameterMap(content.Parameters)), guardImplicitHeadings: false);
        return lines;
    }

    private List<Block> GetSummaryBlocks(GoDocContent content, Dictionary<string, string> parameterNames)
    {
        var blocks = ToBlocks(content.Summary, parameterNames);

        // Property documentation describes the value, not the call that sets it. Unless it can be reworded
        // ("Gets or sets the port." -> "SetPort sets the port."), the generated sentence leads and the
        // property text follows it.
        if (blocks.Count > 0 &&
            content.Subject == GoDocSubject.Setter &&
            blocks[0] is ParagraphBlock property &&
            !TryRewritePropertySummary(content.GoName, property.Text, content.Subject, out _))
        {
            blocks.InsertRange(0, ToBlocks(content.FallbackSummary, parameterNames).Take(1));
        }
        else if (blocks.Count == 0)
        {
            blocks = ToBlocks(content.FallbackSummary, parameterNames);
        }

        if (blocks.Count > 0 && blocks[0] is ParagraphBlock paragraph)
        {
            blocks[0] = new ParagraphBlock(StartWithName(content.GoName, paragraph.Text, content.Subject));
        }

        return blocks;
    }

    private static Dictionary<string, string> BuildParameterMap(IReadOnlyList<GoDocParameter> parameters)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            map.TryAdd(parameter.Name, parameter.GoName);
        }

        return map;
    }

    private void AddParameters(List<string> lines, IReadOnlyList<GoDocParameter> parameters, Dictionary<string, string> parameterNames)
    {
        var items = new List<string>();
        foreach (var parameter in parameters)
        {
            var description = Flatten(ConvertInline(parameter.Description, parameterNames));
            if (description.Length > 0)
            {
                items.Add($"{parameter.GoName}: {description}");
            }
        }

        if (items.Count == 0)
        {
            return;
        }

        AddSeparator(lines);
        lines.Add("// Parameters:");
        foreach (var item in items)
        {
            AddWrapped(lines, item, "   - ", "     ");
        }
    }

    private static void AddSeparator(List<string> lines)
    {
        if (lines.Count > 0)
        {
            lines.Add("//");
        }
    }

    private static void AddBlocks(List<string> lines, List<Block> blocks, bool guardImplicitHeadings)
    {
        foreach (var block in blocks)
        {
            AddSeparator(lines);

            switch (block)
            {
                case ParagraphBlock paragraph:
                    AddWrapped(lines, guardImplicitHeadings ? GuardImplicitHeading(paragraph.Text) : paragraph.Text, " ", " ");
                    break;
                case CodeBlock code:
                    foreach (var line in code.Lines)
                    {
                        lines.Add($"//\t{line}");
                    }

                    break;
                case ListBlock list:
                    foreach (var item in list.Items)
                    {
                        AddWrapped(lines, item, "   - ", "     ");
                    }

                    break;
            }
        }
    }

    // Splits documentation text into paragraphs, lists and code blocks. The scanner flattens each XML
    // doc paragraph to a single line and keeps one line per list item and per code line:
    //   Adds a container.\nThe name must be unique.\n- port: The host port.\n```\nbuilder.Run()\n```
    // Paragraph boundaries are not preserved by the scanner, so consecutive prose lines form one paragraph.
    private List<Block> ToBlocks(string? text, Dictionary<string, string> parameterNames)
    {
        var blocks = new List<Block>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return blocks;
        }

        var segments = Sanitize(text).Split("```");
        for (var i = 0; i < segments.Length; i++)
        {
            if (i % 2 == 1)
            {
                var codeLines = segments[i].Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList();

                if (codeLines.Count > 0)
                {
                    blocks.Add(new CodeBlock(codeLines));
                }

                continue;
            }

            AddProseBlocks(blocks, segments[i], parameterNames);
        }

        return blocks;
    }

    private void AddProseBlocks(List<Block> blocks, string text, Dictionary<string, string> parameterNames)
    {
        var paragraph = new List<string>();
        var items = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                blocks.Add(new ParagraphBlock(ConvertInline(string.Join(' ', paragraph), parameterNames)));
                paragraph.Clear();
            }
        }

        void FlushList()
        {
            if (items.Count > 0)
            {
                blocks.Add(new ListBlock(items.Select(item => ConvertInline(item, parameterNames)).ToList()));
                items.Clear();
            }
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = CollapseWhitespace(rawLine);
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushParagraph();
                items.Add(line[2..]);
                continue;
            }

            FlushList();
            paragraph.Add(line);
        }

        FlushParagraph();
        FlushList();
    }

    // Rewrites the first sentence so the comment starts with the identifier, as `go doc`, gopls hover
    // and linters expect ("Adds a container." -> "AddRedis adds a container.").
    private static string StartWithName(string name, string text, GoDocSubject subject)
    {
        if (StartsWithIdentifier(text, name))
        {
            return text;
        }

        if (TryRewritePropertySummary(name, text, subject, out var rewritten))
        {
            return rewritten;
        }

        var separator = text.IndexOf(' ');
        var first = separator < 0 ? text : text[..separator];
        var rest = separator < 0 ? string.Empty : text[(separator + 1)..];
        var word = first.TrimEnd(',', ';', ':');

        if (rest.Length > 0)
        {
            if (s_leadingDeterminers.Contains(word))
            {
                return IsIndependentClause(rest) ? $"{name} - {text}" : $"{name} is {LowerFirst(first)} {rest}";
            }

            if (word.Equals("Whether", StringComparison.OrdinalIgnoreCase))
            {
                return $"{name} indicates whether {rest}";
            }

            if (IsLeadingVerb(word, rest))
            {
                return $"{name} {LowerFirst(first)} {rest}";
            }
        }
        else if (s_leadingVerbs.Contains(word))
        {
            return $"{name} {LowerFirst(first)}";
        }

        return $"{name} - {text}";
    }

    // Property documentation reads "Gets or sets the port." or "Gets a value indicating whether X is enabled."
    // regardless of whether the Go identifier is a getter, a setter or a field, so the verb is chosen per subject.
    private static bool TryRewritePropertySummary(string name, string text, GoDocSubject subject, out string rewritten)
    {
        string body;
        if (text.StartsWith(GetsOrSetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            body = text[GetsOrSetsPrefix.Length..];
        }
        else if (text.StartsWith("Gets ", StringComparison.OrdinalIgnoreCase) && subject is GoDocSubject.Setter or GoDocSubject.Field)
        {
            body = text["Gets ".Length..];
        }
        else
        {
            rewritten = string.Empty;
            return false;
        }

        var isBoolean = false;
        if (body.StartsWith(ValueIndicatingPrefix, StringComparison.OrdinalIgnoreCase))
        {
            body = string.Concat("whether ", body.AsSpan(ValueIndicatingPrefix.Length));
            isBoolean = true;
        }
        else if (body.StartsWith("whether ", StringComparison.OrdinalIgnoreCase))
        {
            isBoolean = true;
        }

        rewritten = subject switch
        {
            GoDocSubject.Setter => $"{name} sets {body}",
            GoDocSubject.Getter or GoDocSubject.Method => isBoolean ? $"{name} reports {body}" : $"{name} gets {body}",
            _ => isBoolean ? $"{name} indicates {body}" : $"{name} is {body}",
        };

        return true;
    }

    private static bool IsIndependentClause(string text)
    {
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = word.Trim(',', ';', ':', '.', '(', ')');
            if (s_subordinators.Contains(token))
            {
                return false;
            }

            if (s_auxiliaryVerbs.Contains(token))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLeadingVerb(string word, string rest)
    {
        if (s_leadingVerbs.Contains(word))
        {
            return true;
        }

        // Verbs missing from the list still have the "Verbs the thing" shape; requiring an object
        // introducer keeps plural nouns such as "Options for ..." from being treated as verbs.
        var nextWord = rest.Split(' ', 2)[0];
        return word.Length > 3 &&
            char.IsUpper(word[0]) &&
            word.All(char.IsAsciiLetter) &&
            word.EndsWith('s') &&
            !word.EndsWith("ss", StringComparison.Ordinal) &&
            !word.EndsWith("us", StringComparison.Ordinal) &&
            !word.EndsWith("is", StringComparison.Ordinal) &&
            s_objectIntroducers.Contains(nextWord);
    }

    private static bool StartsWithIdentifier(string text, string name)
    {
        return text.StartsWith(name, StringComparison.Ordinal) &&
            (text.Length == name.Length || !IsIdentifierCharacter(text[name.Length]));
    }

    private static string LowerFirst(string text)
    {
        return text.Length == 0 ? text : string.Concat(char.ToLowerInvariant(text[0]).ToString(), text.AsSpan(1));
    }

    // gofmt rewrites a single-line paragraph into a "# Heading" when it sits between two paragraphs, starts with a
    // capital letter, ends with a letter or digit and contains none of the characters below (see isOldHeading in
    // go/doc/comment). A period followed by a non-space ("Requires Redis 7.2") and a possessive "'s" are allowed in
    // a heading, so they do not exempt the text here. Ending the paragraph with a period keeps it a paragraph.
    private static string GuardImplicitHeading(string text)
    {
        if (text.Length == 0 ||
            !char.IsUpper(text[0]) ||
            !char.IsLetterOrDigit(text[^1]) ||
            text.Length > MaxLineLength - 1)
        {
            return text;
        }

        foreach (var c in text)
        {
            if (";:!?+*/=[]{}_^°&§~%#@<\">\\".Contains(c))
            {
                return text;
            }
        }

        return $"{text}.";
    }

    private static void AddWrapped(List<string> lines, string text, string firstPrefix, string continuationPrefix)
    {
        var line = new StringBuilder(firstPrefix);
        var wordsOnLine = 0;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (wordsOnLine > 0 && line.Length + 1 + word.Length > MaxLineLength && !IsUnsafeLineStart(word))
            {
                lines.Add($"//{line}");
                line.Clear().Append(continuationPrefix);
                wordsOnLine = 0;
            }

            if (wordsOnLine > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
            wordsOnLine++;
        }

        lines.Add($"//{line}");
    }

    // A wrapped line must not turn into a heading ("# Title"), a link definition ("[Name]: url") or a list item ("- text", "1. text").
    private static bool IsUnsafeLineStart(string word)
    {
        return word.StartsWith('#')
            || (word.StartsWith('[') && word.Contains("]:", StringComparison.Ordinal))
            || word is "-" or "*" or "+" or "\u2022"
            || (word.Length >= 2 && word[^1] is '.' or ')' && word[..^1].All(char.IsAsciiDigit));
    }

    private string ConvertInline(string? text, Dictionary<string, string> parameterNames)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // The scanner keeps the CLR generic arity in cref text, e.g. `IResourceBuilder`1` or `WithPersistentLifetime``1`.
        // Dropping it up front keeps the arity backtick from pairing with the next code span.
        var withoutArity = GenericArityRegex().Replace(text, "`${name}`");
        var withoutReferences = AtsReferenceRegex().Replace(withoutArity, match => ConvertReference(withoutArity, match));
        return CodeSpanRegex().Replace(withoutReferences, match => ConvertCodeSpan(withoutReferences, match, parameterNames));
    }

    // The scanner renders <ats-see cref="!:kind:target" /> as {@ats-ref kind:target|label}; targets are dotted
    // identifiers such as RedisResource.withPersistence. Only types become [Name] doc links, because go doc
    // can resolve a link to a generated type but not to a member of an exported value.
    private string ConvertReference(string input, Match match)
    {
        var kind = match.Groups["kind"].Value;
        var segments = match.Groups["target"].Value.Split('.');
        var label = match.Groups["label"].Success ? CollapseWhitespace(match.Groups["label"].Value) : string.Empty;

        if (kind == "type")
        {
            var typeName = segments[^1];
            return _resolveTypeName(typeName) is { } goType
                ? CanLink(input, match) ? $"[{goType}]" : goType
                : label.Length > 0 ? label : typeName;
        }

        if (label.Length > 0)
        {
            return label;
        }

        return string.Join('.', segments.Select((segment, index) => index == 0 && _resolveTypeName(segment) is { } goType ? goType : _toExportedName(segment)));
    }

    private string ConvertCodeSpan(string input, Match match, Dictionary<string, string> parameterNames)
    {
        var code = match.Groups["code"].Value.Trim();

        if (code == "null")
        {
            return "nil";
        }

        if (parameterNames.TryGetValue(code, out var goParameter))
        {
            return goParameter;
        }

        return _resolveTypeName(code) is { } goType
            ? CanLink(input, match) ? $"[{goType}]" : goType
            : code;
    }

    // A doc link is only recognized when the characters around the brackets are not letters, digits or
    // underscores (see docLink in go/doc/comment); otherwise the brackets would be printed literally.
    private static bool CanLink(string input, Match match)
    {
        var before = match.Index > 0 ? input[match.Index - 1] : ' ';
        var afterIndex = match.Index + match.Length;
        var after = afterIndex < input.Length ? input[afterIndex] : ' ';
        return !IsIdentifierCharacter(before) && !IsIdentifierCharacter(after);
    }

    private static bool IsIdentifierCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    // Code fences have no meaning inside a parameter, return or deprecation message, which are single sentences.
    private static string Flatten(string? text)
    {
        return string.IsNullOrEmpty(text)
            ? string.Empty
            : CollapseWhitespace(Sanitize(text).Replace("```", " ", StringComparison.Ordinal));
    }

    private static string CollapseWhitespace(string text) => WhitespaceRegex().Replace(text, " ").Trim();

    // XML documentation can contain CRLF, control characters and Unicode line separators. A Go source file
    // must not contain NUL or a byte order mark, and a stray carriage return would end up inside a comment.
    private static string Sanitize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'))
        {
            if (c is '\n' or '\t')
            {
                builder.Append(c);
            }
            else if (c is '\u2028' or '\u2029')
            {
                builder.Append(' ');
            }
            else if (!char.IsControl(c) && c != '\uFEFF')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\{@ats-ref (?<kind>type|method|field):(?<target>[^|}\s]+)(?:\|(?<label>[^}]*))?\}")]
    private static partial Regex AtsReferenceRegex();

    [GeneratedRegex(@"`(?<name>[A-Za-z_][A-Za-z0-9_]*)`{1,2}\d+`")]
    private static partial Regex GenericArityRegex();

    [GeneratedRegex(@"`(?<code>[^`\n]+)`")]
    private static partial Regex CodeSpanRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private abstract record Block;

    private sealed record ParagraphBlock(string Text) : Block;

    private sealed record CodeBlock(IReadOnlyList<string> Lines) : Block;

    private sealed record ListBlock(IReadOnlyList<string> Items) : Block;
}
