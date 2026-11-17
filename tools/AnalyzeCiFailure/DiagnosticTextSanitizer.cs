// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static AnalyzeCiFailure.CiFailureJson;

namespace AnalyzeCiFailure;

/// <summary>
/// Redacts secret-shaped values from CI diagnostics, removes terminal/control characters,
/// and bounds displayed Unicode text. Run metadata uses a separate policy that preserves
/// descriptions of connection-string settings without exposing their assigned values.
/// </summary>
internal static partial class DiagnosticTextSanitizer
{
    // Prefixes such as "a-" repeated can be arbitrarily long. Keep matching linear
    // without imposing a key-length limit that could leave long secret settings exposed.
    // https://learn.microsoft.com/dotnet/standard/base-types/regular-expression-options#nonbacktracking-mode
    private const RegexOptions SecretSettingRegexOptions = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private const string SecretSettingKeyPattern = @"(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?(?:password|passwd|pwd|token|api[_-]?key|access[_-]?key|account[_-]?key|primary[_-]?key|secondary[_-]?key|secret|client[_-]?secret|connection[_-]?strings?(?:(?:__|[.:])[A-Za-z0-9_.-]+)?|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)|pgpassword|_?authToken|_?auth|accessToken|refreshToken)";
    private const string NonConnectionSecretSettingKeyPattern = @"(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?(?:password|passwd|pwd|token|api[_-]?key|access[_-]?key|account[_-]?key|primary[_-]?key|secondary[_-]?key|secret|client[_-]?secret|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)|pgpassword|_?authToken|_?auth|accessToken|refreshToken)";
    private const string AnyConnectionStringKeyPattern = @"(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?connection[_-]?strings?(?:(?:__|[.:])[A-Za-z0-9_.-]+)?)";
    private const string ConnectionStringRootKeyPattern = @"(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?connection[_-]?strings?)";
    private const string NamedConnectionStringKeyPattern = @"(?i:(?:[A-Za-z][A-Za-z0-9_.-]*[_-])?connection[_-]?strings?(?:(?:__|[.:])[A-Za-z0-9_.-]+))";
    private const string SensitiveCommandLineOptionPattern = @"(?i:password|passwd|pwd|token|auth[_-]?token|access[_-]?token|refresh[_-]?token|api[_-]?key|access[_-]?key|account[_-]?key|primary[_-]?key|secondary[_-]?key|secret|client[_-]?secret|connection[_-]?strings?|sharedaccesskey|sharedaccesssignature|signature|private[_-]?key)";
    private static readonly (Regex Pattern, string Replacement)[] s_diagnosticRedactionRules =
        CreateSecretRedactionRules(preserveConnectionStringDescriptions: false);
    private static readonly (Regex Pattern, string Replacement)[] s_runMetadataRedactionRules =
        CreateSecretRedactionRules(preserveConnectionStringDescriptions: true);

    internal static string Sanitize(string text, int limit = int.MaxValue, bool multiline = false)
        => RedactAndNormalize(text, limit, multiline, s_diagnosticRedactionRules);

    internal static string SanitizeRunMetadata(string text, int limit)
        => RedactAndNormalize(text, limit, multiline: false, s_runMetadataRedactionRules);

    private static string RedactAndNormalize(string text, int limit, bool multiline, (Regex Pattern, string Replacement)[] rules)
    {
        // Redact complete values before bounding them. For example, a private key
        // crossing the display limit must not leave an apparently harmless prefix.
        foreach (var (pattern, replacement) in rules)
        {
            text = pattern.Replace(text, replacement);
        }
        text = multiline ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : SingleLineWhitespaceRegex().Replace(text, " ");
        text = TerminalEscapeRegex().Replace(text, "");
        var result = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if ((Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator &&
                !(multiline && rune.Value is '\n' or '\t')) ||
                rune.Value is >= 65024 and <= 65039 or >= 917760 and <= 917999)
            {
                continue;
            }
            if (count++ == limit)
            {
                break;
            }
            result.Append(rune);
        }

        return result.ToString();
    }

    internal static JsonNode? SanitizeJson(JsonNode? node, int limit = 500, bool multiline = false)
        => node switch
        {
            JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, SanitizeJson(p.Value, limit, multiline)))),
            JsonArray array => new JsonArray(array.Select(n => SanitizeJson(n, limit, multiline)).ToArray()),
            _ when Text(node) is { } text => JsonValue.Create(Sanitize(text, limit, multiline)),
            _ => node?.DeepClone(),
        };

    internal static JsonNode? SanitizeJson(JsonElement node, int limit, bool multiline)
        => node.ValueKind switch
        {
            JsonValueKind.Object => new JsonObject(node.EnumerateObject().Select(property =>
                KeyValuePair.Create(property.Name, SanitizeJson(property.Value, limit, multiline)))),
            JsonValueKind.Array => new JsonArray(node.EnumerateArray().Select(value => SanitizeJson(value, limit, multiline)).ToArray()),
            JsonValueKind.String => JsonValue.Create(Sanitize(node.GetString()!, limit, multiline)),
            JsonValueKind.Null => null,
            _ => JsonValue.Create(node.Clone()),
        };

    internal static JsonNode? SanitizeCause(JsonNode? node)
    {
        node = node?.DeepClone();
        if (node is JsonObject obj)
        {
            SanitizeField(obj, "title");
            SanitizeField(obj, "test_name");
            SanitizeField(obj, "error_pattern", multiline: true);
        }

        return node;
    }

    private static void SanitizeField(JsonObject node, string field, int limit = int.MaxValue, bool multiline = false)
    {
        if (Text(node[field]) is { } text)
        {
            node[field] = Sanitize(text, limit, multiline);
        }
    }

    private static (Regex, string)[] CreateSecretRedactionRules(bool preserveConnectionStringDescriptions)
    {
        var rules = new List<(Regex, string)>();
        void Add(Regex pattern, string replacement = "${prefix}[REDACTED]${suffix}")
            => rules.Add((pattern, replacement));
        void AddQuotedValueRules(Regex doubleQuoted, Regex singleQuoted)
        {
            Add(doubleQuoted);
            Add(singleQuoted);
        }

        Add(PrivateKeyRegex(), "[REDACTED]");
        Add(AuthorizationHeaderRegex(), "${prefix}[REDACTED]");
        Add(SecretHeaderRegex(), "${prefix}[REDACTED]");
        Add(UrlCredentialsRegex(), "${scheme}[REDACTED]:[REDACTED]@");
        Add(UrlUsernameRegex(), "${scheme}[REDACTED]@");
        Add(JsonWebTokenRegex(), "[REDACTED]");
        Add(AccessTokenRegex(), "[REDACTED]");
        Add(SecretQueryParameterRegex(), "${prefix}[REDACTED]");
        Add(DoubleQuotedJsonSettingRegex());
        Add(SingleQuotedJsonSettingRegex());
        AddQuotedValueRules(DoubleQuotedCommandLineOptionRegex(), SingleQuotedCommandLineOptionRegex());
        Add(UnquotedCommandLineOptionRegex(), "${prefix}[REDACTED]");
        // A title such as "Fix ConnectionString: preserve named configuration" is prose.
        // Root-key '=' assignments and named keys such as ConnectionStrings__db still carry values.
        if (preserveConnectionStringDescriptions)
        {
            AddQuotedValueRules(DoubleQuotedRootConnectionStringRegex(), SingleQuotedRootConnectionStringRegex());
            AddQuotedValueRules(DoubleQuotedNamedConnectionStringRegex(), SingleQuotedNamedConnectionStringRegex());
            Add(UnquotedRootConnectionStringRegex(), "${prefix}[REDACTED]");
        }
        Add(preserveConnectionStringDescriptions ? UnquotedNamedConnectionStringRegex() : UnquotedConnectionStringRegex(), "${prefix}[REDACTED]");
        AddQuotedValueRules(
            preserveConnectionStringDescriptions ? DoubleQuotedNonConnectionSettingRegex() : DoubleQuotedSecretSettingRegex(),
            preserveConnectionStringDescriptions ? SingleQuotedNonConnectionSettingRegex() : SingleQuotedSecretSettingRegex());
        Add(preserveConnectionStringDescriptions ? UnquotedNonConnectionSettingRegex() : UnquotedSecretSettingRegex(), "${prefix}[REDACTED]");

        return rules.ToArray();
    }

    [GeneratedRegex("[\r\n\t]+")]
    private static partial Regex SingleLineWhitespaceRegex();

    [GeneratedRegex("\u001b\\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex TerminalEscapeRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?(?:-----END [A-Z ]*PRIVATE KEY-----|$)", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyRegex();

    [GeneratedRegex(@"(?<prefix>\b(?i:authorization|proxy-authorization)\s*:\s*(?i:basic|bearer)\s+)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationHeaderRegex();

    [GeneratedRegex(@"(?<prefix>\b(?i:x-api-key|api-key|access-token|client-secret)\s*:\s*)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretHeaderRegex();

    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.-]*://)[^\s/:@]*:[^\s/@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlCredentialsRegex();

    [GeneratedRegex(@"(?<scheme>\b[A-Za-z][A-Za-z0-9+.-]*://)[^\s/:@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UrlUsernameRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex JsonWebTokenRegex();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16}|(?:npm|pypi)-[A-Za-z0-9_-]{20,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex AccessTokenRegex();

    [GeneratedRegex(@"(?<prefix>[?&](?i:sig|signature|token|access_token|api[_-]?key|password|secret|client_secret)=)[^&\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex SecretQueryParameterRegex();

    [GeneratedRegex("(?<prefix>\"" + SecretSettingKeyPattern + "\"\\s*:\\s*\")" + @"(?:\\[^\r\n]|[^""\\\r\n])*(?<suffix>"")", SecretSettingRegexOptions)]
    private static partial Regex DoubleQuotedJsonSettingRegex();

    [GeneratedRegex("(?<prefix>'" + SecretSettingKeyPattern + "'\\s*:\\s*')" + @"(?:\\[^\r\n]|[^'\\\r\n])*(?<suffix>')", SecretSettingRegexOptions)]
    private static partial Regex SingleQuotedJsonSettingRegex();

    [GeneratedRegex("(?<prefix>" + @"(^|\s)--" + SensitiveCommandLineOptionPattern + @"\s+" + "\")" + @"(?:\\[^\r\n]|[^""\\\r\n])*(?<suffix>"")", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleQuotedCommandLineOptionRegex();

    [GeneratedRegex("(?<prefix>" + @"(^|\s)--" + SensitiveCommandLineOptionPattern + @"\s+" + "')" + @"(?:\\[^\r\n]|[^'\\\r\n])*(?<suffix>')", RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuotedCommandLineOptionRegex();

    [GeneratedRegex(@"(?<prefix>(^|[^\S\r\n])--" + SensitiveCommandLineOptionPattern + @"[^\S\r\n]+)(?![""'])[^\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnquotedCommandLineOptionRegex();

    [GeneratedRegex("(?<prefix>" + @"\b" + ConnectionStringRootKeyPattern + @"\s*=\s*" + "\")" + @"(?:\\[^\r\n]|[^""\\\r\n])*(?<suffix>"")", SecretSettingRegexOptions)]
    private static partial Regex DoubleQuotedRootConnectionStringRegex();

    [GeneratedRegex("(?<prefix>" + @"\b" + ConnectionStringRootKeyPattern + @"\s*=\s*" + "')" + @"(?:\\[^\r\n]|[^'\\\r\n])*(?<suffix>')", SecretSettingRegexOptions)]
    private static partial Regex SingleQuotedRootConnectionStringRegex();

    [GeneratedRegex("(?<prefix>" + @"\b" + NamedConnectionStringKeyPattern + @"\s*[:=]\s*" + "\")" + @"(?:\\[^\r\n]|[^""\\\r\n])*(?<suffix>"")", SecretSettingRegexOptions)]
    private static partial Regex DoubleQuotedNamedConnectionStringRegex();

    [GeneratedRegex("(?<prefix>" + @"\b" + NamedConnectionStringKeyPattern + @"\s*[:=]\s*" + "')" + @"(?:\\[^\r\n]|[^'\\\r\n])*(?<suffix>')", SecretSettingRegexOptions)]
    private static partial Regex SingleQuotedNamedConnectionStringRegex();

    [GeneratedRegex(@"(?<prefix>\b" + ConnectionStringRootKeyPattern + @"\s*=\s*)[^""'\r\n][^\r\n]*", SecretSettingRegexOptions)]
    private static partial Regex UnquotedRootConnectionStringRegex();

    [GeneratedRegex(@"(?<prefix>\b" + NamedConnectionStringKeyPattern + @"\s*[:=]\s*)[^""'\r\n][^\r\n]*", SecretSettingRegexOptions)]
    private static partial Regex UnquotedNamedConnectionStringRegex();

    [GeneratedRegex(@"(?<prefix>\b" + AnyConnectionStringKeyPattern + @"\s*[:=]\s*)[^""'\r\n][^\r\n]*", SecretSettingRegexOptions)]
    private static partial Regex UnquotedConnectionStringRegex();

    [GeneratedRegex("(?<prefix>" + @"\b" + NonConnectionSecretSettingKeyPattern + @"\s*[:=]\s*" + "\")" + @"(?:\\[^\r\n]|[^""\\\r\n])*(?<suffix>"")", SecretSettingRegexOptions)]
    private static partial Regex DoubleQuotedNonConnectionSettingRegex();

    [GeneratedRegex("(?<prefix>" + @"\b" + SecretSettingKeyPattern + @"\s*[:=]\s*" + "\")" + @"(?:\\[^\r\n]|[^""\\\r\n])*(?<suffix>"")", SecretSettingRegexOptions)]
    private static partial Regex DoubleQuotedSecretSettingRegex();

    [GeneratedRegex("(?<prefix>" + @"\b" + NonConnectionSecretSettingKeyPattern + @"\s*[:=]\s*" + "')" + @"(?:\\[^\r\n]|[^'\\\r\n])*(?<suffix>')", SecretSettingRegexOptions)]
    private static partial Regex SingleQuotedNonConnectionSettingRegex();

    [GeneratedRegex("(?<prefix>" + @"\b" + SecretSettingKeyPattern + @"\s*[:=]\s*" + "')" + @"(?:\\[^\r\n]|[^'\\\r\n])*(?<suffix>')", SecretSettingRegexOptions)]
    private static partial Regex SingleQuotedSecretSettingRegex();

    [GeneratedRegex(@"(?<prefix>\b" + NonConnectionSecretSettingKeyPattern + @"\s*[:=]\s*)[^""';&\r\n][^;&\r\n]*", SecretSettingRegexOptions)]
    private static partial Regex UnquotedNonConnectionSettingRegex();

    [GeneratedRegex(@"(?<prefix>\b" + SecretSettingKeyPattern + @"\s*[:=]\s*)[^""';&\r\n][^;&\r\n]*", SecretSettingRegexOptions)]
    private static partial Regex UnquotedSecretSettingRegex();
}
