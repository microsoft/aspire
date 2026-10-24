// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.CodeGeneration.Go;

/// <summary>
/// The kinds of Go declarations <see cref="GoDeclarationScanner"/> reports.
/// </summary>
internal enum GoDeclarationKind
{
    Type,
    Function,
    Method,
    Field,
    Constant
}

/// <summary>
/// An exported Go declaration, reduced to the parts the API export documents.
/// </summary>
internal sealed record GoDeclaration
{
    public required GoDeclarationKind Kind { get; init; }

    /// <summary>
    /// Gets the declared identifier.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets the type a method, field or constant belongs to, without a pointer or type parameters.
    /// Empty for types and functions.
    /// </summary>
    public string Owner { get; init; } = string.Empty;

    /// <summary>
    /// Gets the declaration without its doc comment or body. Types keep <c>type</c> and functions keep
    /// <c>func</c>; methods, fields and constants start at their own name, for example
    /// <c>Count() (int, error)</c>, <c>Format string</c> or <c>Redis RedisKind = "redis"</c>.
    /// </summary>
    public required string Signature { get; init; }

    public string? Summary { get; init; }

    public string? Remarks { get; init; }

    /// <summary>
    /// Gets the unquoted value of a constant, or the struct tag of a field.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Gets the receiver type of a method, for example <c>*List[T]</c>.
    /// </summary>
    public string Receiver { get; init; } = string.Empty;
}

/// <summary>
/// Reads the exported declarations out of Go source the SDK generator controls: the embedded runtime
/// files and the text <see cref="AtsGoCodeGenerator"/> emits.
/// </summary>
/// <remarks>
/// <para>
/// This is a line scanner, not a Go parser. It relies on the layout gofmt produces and the generator
/// writes: declarations start in column 0, struct and const bodies are tab-indented and closed by a
/// column-0 <c>}</c> or <c>)</c>, and a doc comment is the run of <c>//</c> lines directly above its
/// declaration. A blank line detaches a comment from what follows it.
/// </para>
/// <para>
/// Only top-level declarations are recognized, and only the exported ones, except that methods are
/// reported with the name of their receiver even when the receiver type is unexported. Callers filter
/// on <see cref="GoDeclaration.Owner"/>.
/// </para>
/// </remarks>
internal static class GoDeclarationScanner
{
    /// <summary>
    /// Scans Go source and returns its exported declarations in source order.
    /// </summary>
    /// <param name="source">The Go source.</param>
    /// <param name="includeBodyDocs">
    /// Whether comments inside struct and const bodies document the field or constant below them. The
    /// hand-written runtime uses those comments as group headings instead, so it passes
    /// <see langword="false"/>.
    /// </param>
    public static IReadOnlyList<GoDeclaration> Scan(string source, bool includeBodyDocs)
    {
        ArgumentNullException.ThrowIfNull(source);

        var lines = source.ReplaceLineEndings("\n").Split('\n');
        var declarations = new List<GoDeclaration>();
        var doc = new List<string>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.StartsWith("//", StringComparison.Ordinal))
            {
                doc.Add(StripCommentMarker(line));
                continue;
            }

            if (line.StartsWith("type ", StringComparison.Ordinal))
            {
                i = ScanType(lines, i, doc, includeBodyDocs, declarations);
            }
            else if (line.StartsWith("func ", StringComparison.Ordinal))
            {
                ScanFunction(line, doc, declarations);
            }
            else if (line.StartsWith("const (", StringComparison.Ordinal))
            {
                i = ScanConstBlock(lines, i, includeBodyDocs, declarations);
            }

            doc.Clear();
        }

        return declarations;
    }

    /// <summary>
    /// Returns the results of a function or method signature, or <see langword="null"/> when it declares
    /// none. For <c>Count() (int, error)</c> that is <c>(int, error)</c>.
    /// </summary>
    /// <param name="signature">A <see cref="GoDeclaration.Signature"/> of a function or method.</param>
    public static string? GetReturnType(string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        var open = signature.IndexOf('(');
        if (open < 0)
        {
            return null;
        }

        // Parameters can hold parentheses of their own (`callback func(...any) any`), so the parameter
        // list ends at the parenthesis that balances the first one.
        var depth = 0;
        for (var i = open; i < signature.Length; i++)
        {
            if (signature[i] == '(')
            {
                depth++;
            }
            else if (signature[i] == ')' && --depth == 0)
            {
                var results = signature[(i + 1)..].Trim();
                return results.Length > 0 ? results : null;
            }
        }

        return null;
    }

    // Examples of the headers handled here:
    //   type AtsErrorCode string
    //   type List[T any] struct {
    //   type DistributedApplication interface { handleReference }
    private static int ScanType(
        string[] lines,
        int index,
        List<string> doc,
        bool includeBodyDocs,
        List<GoDeclaration> declarations)
    {
        var header = lines[index];
        var rest = header["type ".Length..];
        var nameEnd = rest.IndexOfAny([' ', '[']);
        if (nameEnd <= 0)
        {
            return index;
        }

        var name = rest[..nameEnd];
        if (!IsExported(name))
        {
            return index;
        }

        var signature = CutBody(header);
        var (summary, remarks) = SplitDoc(doc);
        declarations.Add(new GoDeclaration
        {
            Kind = GoDeclarationKind.Type,
            Name = name,
            Signature = signature,
            Summary = summary,
            Remarks = remarks
        });

        // Interface bodies carry no declarations of their own and one-line types have no body.
        if (!header.EndsWith('{') || !signature.EndsWith(" struct", StringComparison.Ordinal))
        {
            return index;
        }

        var bodyDoc = new List<string>();
        var end = index + 1;
        for (; end < lines.Length && lines[end] != "}"; end++)
        {
            var bodyLine = lines[end].Trim();
            if (bodyLine.Length == 0)
            {
                bodyDoc.Clear();
            }
            else if (bodyLine.StartsWith("//", StringComparison.Ordinal))
            {
                bodyDoc.Add(StripCommentMarker(bodyLine));
            }
            else
            {
                AddField(name, bodyLine, includeBodyDocs ? bodyDoc : [], declarations);
                bodyDoc.Clear();
            }
        }

        return end;
    }

    // Examples of the field lines handled here:
    //   Format         string
    //   Name string `json:"name,omitempty"`
    //   *resourceBuilderBase                       (embedded; skipped)
    private static void AddField(string owner, string line, List<string> doc, List<GoDeclaration> declarations)
    {
        var tagStart = line.IndexOf('`');
        var tagEnd = line.LastIndexOf('`');
        var tag = tagStart >= 0 && tagEnd > tagStart ? line[(tagStart + 1)..tagEnd] : null;

        var declaration = tagStart >= 0 ? line[..tagStart] : TrimTrailingComment(line);
        declaration = CollapseWhitespace(declaration);

        var space = declaration.IndexOf(' ');
        if (space <= 0 || !IsExported(declaration[..space]))
        {
            return;
        }

        var (summary, remarks) = SplitDoc(doc);
        declarations.Add(new GoDeclaration
        {
            Kind = GoDeclarationKind.Field,
            Name = declaration[..space],
            Owner = owner,
            Signature = declaration,
            Summary = summary,
            Remarks = remarks,
            Value = tag
        });
    }

    // Examples of the function headers handled here:
    //   func RefExpr(format string, valueProviders ...any) *ReferenceExpression {
    //   func (l *List[T]) Count() (int, error) {
    //   func (e *CapabilityError) Code() string       { return e.err.Code }
    private static void ScanFunction(string line, List<string> doc, List<GoDeclaration> declarations)
    {
        var rest = line["func ".Length..];
        var (summary, remarks) = SplitDoc(doc);

        if (rest.StartsWith('('))
        {
            var close = rest.IndexOf(')');
            if (close < 0)
            {
                return;
            }

            var receiver = rest[1..close].Trim();
            var afterReceiver = rest[(close + 1)..].TrimStart();
            var nameEnd = afterReceiver.IndexOf('(');
            if (nameEnd <= 0)
            {
                return;
            }

            var name = afterReceiver[..nameEnd];
            if (!IsExported(name))
            {
                return;
            }

            // The receiver variable is optional and a generic receiver type holds spaces
            // (`d *Dict[K, V]`), so only a space before the type parameters ends the variable name.
            var firstSpace = receiver.IndexOf(' ');
            var bracket = receiver.IndexOf('[');
            var receiverType = firstSpace >= 0 && (bracket < 0 || firstSpace < bracket)
                ? receiver[(firstSpace + 1)..].Trim()
                : receiver;
            var typeName = receiverType.TrimStart('*');
            var typeParameters = typeName.IndexOf('[');
            var owner = typeParameters >= 0 ? typeName[..typeParameters] : typeName;

            declarations.Add(new GoDeclaration
            {
                Kind = GoDeclarationKind.Method,
                Name = name,
                Owner = owner,
                Signature = CollapseWhitespace(CutBody(afterReceiver)),
                Summary = summary,
                Remarks = remarks,
                Receiver = receiverType
            });

            return;
        }

        var functionNameEnd = rest.IndexOfAny(['(', '[']);
        if (functionNameEnd <= 0)
        {
            return;
        }

        var functionName = rest[..functionNameEnd];
        if (!IsExported(functionName))
        {
            return;
        }

        declarations.Add(new GoDeclaration
        {
            Kind = GoDeclarationKind.Function,
            Name = functionName,
            Signature = "func " + CollapseWhitespace(CutBody(rest)),
            Summary = summary,
            Remarks = remarks
        });
    }

    // Example of the entries handled here, one per line between `const (` and `)`:
    //   CapabilityNotFound AtsErrorCode = "CAPABILITY_NOT_FOUND"
    // Only entries that name their type are reported; the generators always do.
    private static int ScanConstBlock(
        string[] lines,
        int index,
        bool includeBodyDocs,
        List<GoDeclaration> declarations)
    {
        var bodyDoc = new List<string>();
        var end = index + 1;
        for (; end < lines.Length && lines[end] != ")"; end++)
        {
            var entry = lines[end].Trim();
            if (entry.Length == 0)
            {
                bodyDoc.Clear();
                continue;
            }

            if (entry.StartsWith("//", StringComparison.Ordinal))
            {
                bodyDoc.Add(StripCommentMarker(entry));
                continue;
            }

            var equals = entry.IndexOf('=');
            if (equals > 0)
            {
                var left = CollapseWhitespace(entry[..equals]);
                var literal = TrimTrailingComment(entry[(equals + 1)..]).Trim();
                var parts = left.Split(' ');
                if (parts.Length == 2 && IsExported(parts[0]))
                {
                    var (summary, remarks) = SplitDoc(includeBodyDocs ? bodyDoc : []);
                    declarations.Add(new GoDeclaration
                    {
                        Kind = GoDeclarationKind.Constant,
                        Name = parts[0],
                        Owner = parts[1],
                        Signature = $"{left} = {literal}",
                        Summary = summary,
                        Remarks = remarks,
                        Value = literal.Length >= 2 && literal[0] == '"' && literal[^1] == '"' ? literal[1..^1] : literal
                    });
                }
            }

            bodyDoc.Clear();
        }

        return end;
    }

    // Go exports every identifier that starts with a Unicode upper case letter (category Lu), and the
    // generator's naming can produce non-ASCII ones. A first letter outside the BMP is not recognized.
    private static bool IsExported(string name) => name.Length > 0 && char.IsUpper(name[0]);

    // The first " {" ends a header: a one-line body follows it, and gofmt never puts a space before the
    // brace of the empty `struct{}` and `interface{}` types.
    private static string CutBody(string header)
    {
        var brace = header.IndexOf(" {", StringComparison.Ordinal);
        return (brace >= 0 ? header[..brace] : header).TrimEnd();
    }

    private static string TrimTrailingComment(string text)
    {
        var comment = text.IndexOf(" //", StringComparison.Ordinal);
        return comment >= 0 ? text[..comment] : text;
    }

    // gofmt aligns the columns of neighboring declarations with runs of spaces.
    private static string CollapseWhitespace(string text)
    {
        var parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static string StripCommentMarker(string line)
    {
        var text = line.TrimStart()[2..].TrimEnd();
        return text.StartsWith(' ') ? text[1..] : text;
    }

    // The summary is the first paragraph, joined onto one line; everything after the first blank comment
    // line is the remarks and keeps its line breaks, because it can hold indented code.
    private static (string? Summary, string? Remarks) SplitDoc(IReadOnlyList<string> doc)
    {
        var paragraph = 0;
        while (paragraph < doc.Count && doc[paragraph].Length > 0)
        {
            paragraph++;
        }

        var summary = string.Join(' ', doc.Take(paragraph));
        var remarks = string.Join('\n', doc.Skip(paragraph + 1)).Trim();

        return (summary.Length > 0 ? summary : null, remarks.Length > 0 ? remarks : null);
    }
}
