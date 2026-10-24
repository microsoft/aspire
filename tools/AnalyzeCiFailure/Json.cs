// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnalyzeCiFailure;

// Inspect JSON before projecting rule inputs. In particular, jq accepts fractional job IDs,
// unknown analysis fields, missing optional diagnostics, and null/false fallback values.
// Deserializing directly into required, integer-valued DTOs would change that contract.
internal static class Json
{
    public static JsonNode? Read(string file)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return Project(document.RootElement);
        }
        catch (InvalidOperationException ex)
        {
            // System.Text.Json decodes property names and string values lazily.
            // A key such as "bad\ud800" must fail at the input boundary, not escape as a crash.
            throw new JsonException("JSON contains malformed Unicode text", ex);
        }
    }

    private static JsonNode? Project(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in node.EnumerateObject())
                {
                    // jq accepts duplicate names with the last value winning. Constructing the
                    // object explicitly avoids JsonObject's lazy duplicate-key exception.
                    obj[property.Name] = Project(property.Value);
                }
                return obj;
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in node.EnumerateArray())
                {
                    array.Add(Project(item));
                }
                return array;
            case JsonValueKind.String:
                return JsonValue.Create(node.GetString());
            default:
                // Preserve the lexical numeric form for the summary/trusted ID comparison.
                return JsonNode.Parse(node.GetRawText());
        }
    }

    public static JsonNode? Field(JsonNode? node, string key) => node is JsonObject obj ? obj[key] : null;
    public static string? Text(JsonNode? node)
    {
        try
        {
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }
        catch (InvalidOperationException ex)
        {
            // System.Text.Json can parse "\ud800" but throws only when its string is read.
            // Report malformed Unicode as input rejection, not an unhandled runtime failure.
            throw new JsonException("JSON contains malformed Unicode text", ex);
        }
    }
    public static NumericId? Number(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? NumericId.Parse(value.ToJsonString()) : null;

    public static bool IsPositiveInteger(JsonNode? node)
    {
        // jq's ". > 0 and . == floor" compares positivity at decimal precision but floor
        // coerces equality to double: 123.000000000000000001 and even positive 1e-400 pass.
        // Keep this existing predicate separate from exact numeric cause-reference identity.
        return Number(node) is { Negative: false, Digits: not "0" } &&
            double.TryParse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            number == Math.Floor(number);
    }
    public static bool IsFalse(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var boolean) && !boolean;
    public static JsonNode? Fallback(JsonNode? node) => IsFalse(node) ? null : node;

    // Match jq -r scalar comparisons used for run identity (e.g. 123 and "123").
    public static string Raw(JsonNode? node) => Text(node) ?? (node?.ToJsonString() ?? "null");
    public static string RawFallback(JsonNode? node) => Raw(Fallback(node) ?? JsonValue.Create(""));

    // Apply only at scalar extraction sites that used Bash command substitution, not to
    // structured JSON comparisons, exact test/job names, diagnostics, or serialized IDs.
    public static string ShellText(JsonNode? node) => Raw(node).Replace("\0", "", StringComparison.Ordinal).TrimEnd('\n');

    public static void Write(string file, JsonNode? node) => File.WriteAllText(file,
        (node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null") + "\n");

    // Workflow-command values must remain on one physical line, even in rejected input.
    public static string Display(string text)
    {
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            {
                result.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:x4}");
            }
            else
            {
                result.Append(rune);
            }
        }

        return result.ToString();
    }

    public static bool Safe(JsonNode? node, int max, bool multiline = false)
    {
        if (Text(node) is not { } text)
        {
            return false;
        }

        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            count++;
            if (multiline && rune.Value is '\t' or '\n')
            {
                continue;
            }

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ||
                rune.Value is >= 65024 and <= 65039 or >= 917760 and <= 917999)
            {
                return false;
            }
        }

        return count <= max;
    }
}

// Keep mathematical JSON number equality without rounding distinct large IDs through double,
// or narrowing the accepted numeric syntax to Int64/decimal. For example 123, 123.0 and 1.23e2
// normalize to the same digits and exponent; 9007199254740992 and 9007199254740993 stay distinct.
internal readonly record struct NumericId(string Digits, BigInteger Exponent, bool Negative)
{
    public static NumericId Parse(string raw)
    {
        var negative = raw.StartsWith('-');
        var unsigned = negative ? raw[1..] : raw;
        var exponentIndex = unsigned.IndexOfAny(['e', 'E']);
        var exponent = exponentIndex < 0 ? BigInteger.Zero : BigInteger.Parse(unsigned[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var significand = exponentIndex < 0 ? unsigned : unsigned[..exponentIndex];
        var decimalIndex = significand.IndexOf('.');
        if (decimalIndex >= 0)
        {
            exponent -= significand.Length - decimalIndex - 1;
        }

        var digits = significand.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0)
        {
            return new NumericId("0", BigInteger.Zero, false);
        }

        var significantDigits = digits.TrimEnd('0');
        exponent += digits.Length - significantDigits.Length;

        return new NumericId(significantDigits, exponent, negative);
    }
}

internal sealed class ValidationException(string message) : Exception(message);
