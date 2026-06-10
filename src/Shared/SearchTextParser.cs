// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire;

/// <summary>
/// The comparison operation for a qualifier value.
/// </summary>
internal enum ComparisonOperator
{
    /// <summary>Field value must contain the qualifier value (default string matching).</summary>
    Contains,
    /// <summary>Numeric: field value must be greater than qualifier value.</summary>
    GreaterThan,
    /// <summary>Numeric: field value must be greater than or equal to qualifier value.</summary>
    GreaterThanOrEqual,
    /// <summary>Numeric: field value must be less than qualifier value.</summary>
    LessThan,
    /// <summary>Numeric: field value must be less than or equal to qualifier value.</summary>
    LessThanOrEqual
}

/// <summary>
/// Represents a parsed search query containing free-text fragments and structured key:value qualifiers.
/// All terms are AND'd: every text fragment and qualifier must match for an item to pass.
/// </summary>
internal sealed class SearchFilter
{
    public static readonly SearchFilter Empty = new([], [], []);

    public SearchFilter(string[] textFragments, SearchQualifier[] qualifiers, SearchQualifier[] negatedQualifiers)
    {
        TextFragments = textFragments;
        Qualifiers = qualifiers;
        NegatedQualifiers = negatedQualifiers;
    }

    /// <summary>
    /// Free-text terms that must each match at least one searchable field.
    /// </summary>
    public string[] TextFragments { get; }

    /// <summary>
    /// Positive key:value qualifiers. Each must match its targeted field.
    /// </summary>
    public SearchQualifier[] Qualifiers { get; }

    /// <summary>
    /// Negated -key:value qualifiers. Each must NOT match its targeted field.
    /// </summary>
    public SearchQualifier[] NegatedQualifiers { get; }

    public bool IsEmpty => TextFragments.Length == 0 && Qualifiers.Length == 0 && NegatedQualifiers.Length == 0;
}

/// <summary>
/// A single key:value qualifier from a search query, optionally with a comparison operator.
/// </summary>
internal sealed class SearchQualifier
{
    public SearchQualifier(string key, string value, ComparisonOperator op = ComparisonOperator.Contains, bool isAttribute = false)
    {
        Key = key;
        Value = value;
        Operator = op;
        IsAttribute = isAttribute;
    }

    /// <summary>
    /// The qualifier key (e.g., "severity", "resource", "http.method", "duration").
    /// Stored in lowercase for case-insensitive key matching.
    /// </summary>
    public string Key { get; }

    /// <summary>
    /// The qualifier value to match against the field.
    /// For comparison operators, this is the numeric string (e.g., "100").
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// The comparison operator. Defaults to <see cref="ComparisonOperator.Contains"/> for string matching.
    /// </summary>
    public ComparisonOperator Operator { get; }

    /// <summary>
    /// Whether this qualifier targets a custom attribute (prefixed with <c>@</c> in the search text).
    /// When true, matching skips the field resolver and only checks attributes.
    /// When false, matching uses the field resolver for known fields; unknown keys are treated as literal text.
    /// </summary>
    public bool IsAttribute { get; }
}

/// <summary>
/// Parses search text into fragments and structured qualifiers, splitting on whitespace while treating
/// quoted text as a single token. Supports key:value qualifiers, -key:value negation, and comparison
/// operators (e.g., duration:&gt;100, duration:&gt;=500).
/// </summary>
/// <remarks>
/// Behavior mirrors <c>gh</c> CLI search with Datadog-style attribute prefixing:
///   - Unquoted words are split on whitespace into individual fragments.
///   - Text enclosed in double quotes is treated as a single fragment (quotes are stripped).
///   - <c>key:value</c> tokens are parsed as structured qualifiers targeting a named/known field.
///   - <c>@key:value</c> tokens target custom attributes (the <c>@</c> prefix is required for attributes).
///   - <c>-key:value</c> or <c>-@key:value</c> tokens are parsed as negated qualifiers (exclude matches).
///   - <c>key:"value with spaces"</c> allows quoted values in qualifiers.
///   - Comparison operators in the value: <c>key:&gt;N</c>, <c>key:&gt;=N</c>, <c>key:&lt;N</c>, <c>key:&lt;=N</c>.
///   - All terms are AND'd: every fragment and qualifier must independently match.
///   - Unknown bare qualifiers (no <c>@</c> prefix, not a known field) are treated as literal text.
///
/// Examples:
///   "hello world"                            → text: ["hello", "world"]
///   "severity:error \"connection failed\""   → qualifiers: [severity=error], text: ["connection failed"]
///   "-severity:debug resource:api"           → negated: [severity=debug], qualifiers: [resource=api]
///   "@http.method:GET"                       → attribute qualifier: [http.method=GET]
///   "-@db.system:redis"                      → negated attribute qualifier: [db.system=redis]
///   "duration:&gt;100"                       → qualifiers: [duration &gt; 100]
///   "duration:&gt;=500 status:error"         → qualifiers: [duration &gt;= 500, status=error]
/// </remarks>
internal static class SearchTextParser
{
    /// <summary>
    /// Parses the search text into a <see cref="SearchFilter"/> containing text fragments,
    /// positive qualifiers, and negated qualifiers.
    /// </summary>
    /// <param name="search">The raw search text to parse.</param>
    /// <param name="isKnownKey">
    /// Optional predicate that returns <c>true</c> when a qualifier key (lowercase) is recognized.
    /// When provided, a <c>key:value</c> token whose key is not recognized (and has no <c>@</c>
    /// attribute prefix) is treated as a literal text fragment rather than a structured qualifier.
    /// Pass <c>null</c> to accept any key.
    /// </param>
    public static SearchFilter ParseSearch([NotNullWhen(true)] string? search, Func<string, bool>? isKnownKey = null)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return SearchFilter.Empty;
        }

        var textFragments = new List<string>();
        var qualifiers = new List<SearchQualifier>();
        var negatedQualifiers = new List<SearchQualifier>();

        var span = search.AsSpan().Trim();
        var current = 0;

        while (current < span.Length)
        {
            // Skip whitespace between tokens
            while (current < span.Length && char.IsWhiteSpace(span[current]))
            {
                current++;
            }

            if (current >= span.Length)
            {
                break;
            }

            if (span[current] == '"')
            {
                // Quoted free-text fragment
                var value = ReadQuotedValue(span, ref current);
                if (value.Length > 0)
                {
                    textFragments.Add(value);
                }
            }
            else
            {
                // Read unquoted token (could be qualifier or free-text)
                var tokenStart = current;
                var isNegated = span[current] == '-' && current + 1 < span.Length && !char.IsWhiteSpace(span[current + 1]);

                if (isNegated)
                {
                    current++; // skip the '-' prefix for now
                }

                // Detect @ prefix for attribute qualifiers (e.g., @http.method:GET or -@status:error).
                var isAttribute = current < span.Length && span[current] == '@';
                if (isAttribute)
                {
                    current++; // skip the '@' prefix
                }

                // Find the colon that separates key from value in a qualifier.
                // A qualifier requires: non-empty key, colon not at start/end of the logical token.
                var keyStart = current;
                var colonIndex = -1;

                while (current < span.Length && !char.IsWhiteSpace(span[current]))
                {
                    if (span[current] == ':' && colonIndex == -1 && current > keyStart)
                    {
                        colonIndex = current;
                        break;
                    }

                    // If we hit a quote mid-token without finding a colon yet, it's not a qualifier pattern
                    if (span[current] == '"')
                    {
                        break;
                    }

                    current++;
                }

                if (colonIndex > keyStart)
                {
                    // We found a qualifier pattern: key:value or key:"quoted value"
                    var key = span[keyStart..colonIndex].ToString().ToLowerInvariant();

                    // If a known-keys set is provided and this is not an @-attribute qualifier,
                    // verify the key is recognized. Unrecognized keys (e.g., "http" from a URL
                    // like "http://example.com") are treated as literal text fragments.
                    if (isKnownKey is not null && !isAttribute && !isKnownKey(key))
                    {
                        // Rewind to token start and consume the whole token as free text
                        current = tokenStart;
                        while (current < span.Length && !char.IsWhiteSpace(span[current]))
                        {
                            current++;
                        }

                        var fragment = span[tokenStart..current].ToString();
                        if (fragment.Length > 0)
                        {
                            textFragments.Add(fragment);
                        }
                    }
                    else
                    {
                        current = colonIndex + 1; // move past the colon

                        string value;
                        var op = ComparisonOperator.Contains;

                        if (current < span.Length && span[current] == '"')
                        {
                            // Quoted value: key:"value with spaces"
                            value = ReadQuotedValue(span, ref current);
                        }
                        else
                        {
                            // Check for comparison operator prefix: >, >=, <, <=
                            if (current < span.Length && (span[current] == '>' || span[current] == '<'))
                            {
                                var opChar = span[current];
                                current++;

                                if (current < span.Length && span[current] == '=')
                                {
                                    op = opChar == '>' ? ComparisonOperator.GreaterThanOrEqual : ComparisonOperator.LessThanOrEqual;
                                    current++;
                                }
                                else
                                {
                                    op = opChar == '>' ? ComparisonOperator.GreaterThan : ComparisonOperator.LessThan;
                                }
                            }

                            // Unquoted value: read until whitespace
                            var valueStart = current;
                            while (current < span.Length && !char.IsWhiteSpace(span[current]))
                            {
                                current++;
                            }
                            value = span[valueStart..current].ToString();
                        }

                        if (value.Length > 0)
                        {
                            var qualifier = new SearchQualifier(key, value, op, isAttribute);
                            if (isNegated)
                            {
                                negatedQualifiers.Add(qualifier);
                            }
                            else
                            {
                                qualifiers.Add(qualifier);
                            }
                        }
                        else
                        {
                            // Empty value after colon (e.g., "key:" or "key:\"\"") → treat whole thing as text
                            var fragment = span[tokenStart..current].ToString();
                            if (fragment.Length > 0)
                            {
                                textFragments.Add(fragment);
                            }
                        }
                    }
                }
                else
                {
                    // Not a qualifier — read as free-text fragment (reset to token start if negation prefix was consumed)
                    current = tokenStart;
                    var start = current;
                    while (current < span.Length && !char.IsWhiteSpace(span[current]) && span[current] != '"')
                    {
                        current++;
                    }

                    var fragment = span[start..current].ToString();
                    if (fragment.Length > 0)
                    {
                        textFragments.Add(fragment);
                    }
                }
            }
        }

        if (textFragments.Count == 0 && qualifiers.Count == 0 && negatedQualifiers.Count == 0)
        {
            return SearchFilter.Empty;
        }

        return new SearchFilter(
            textFragments.ToArray(),
            qualifiers.ToArray(),
            negatedQualifiers.ToArray());
    }

    /// <summary>
    /// Parses the search text into individual text fragments (legacy API).
    /// Qualifier values are included as text fragments for backward-compatible full-text matching.
    /// Returns an empty array if the search text is null or empty.
    /// </summary>
    public static string[] ParseFragments([NotNullWhen(true)] string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return [];
        }

        var filter = ParseSearch(search);
        if (filter.IsEmpty)
        {
            return [];
        }

        // Include qualifier values as text fragments for backward-compatible full-text matching
        var allFragments = new List<string>(filter.TextFragments);
        foreach (var q in filter.Qualifiers)
        {
            allFragments.Add(q.Value);
        }
        foreach (var q in filter.NegatedQualifiers)
        {
            allFragments.Add(q.Value);
        }

        return allFragments.ToArray();
    }

    /// <summary>
    /// Returns true if all fragments match against the given state, using a delegate to test each fragment.
    /// The delegate should return true if the fragment is found in any searchable field of <paramref name="state"/>.
    /// </summary>
    public static bool MatchesAllFragments<TState>(string[] fragments, TState state, Func<TState, string, bool> matchesFragment)
    {
        foreach (var fragment in fragments)
        {
            if (!matchesFragment(state, fragment))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a quoted string starting at the current position (which should be the opening quote).
    /// Advances <paramref name="current"/> past the closing quote.
    /// </summary>
    private static string ReadQuotedValue(ReadOnlySpan<char> span, ref int current)
    {
        current++; // skip opening quote
        var start = current;
        while (current < span.Length && span[current] != '"')
        {
            current++;
        }

        var value = span[start..current].ToString();

        // Skip closing quote if present
        if (current < span.Length)
        {
            current++;
        }

        return value;
    }
}
