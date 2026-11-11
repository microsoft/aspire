// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

// Tolerant external-payload and durable-record readers. Versioned transient
// transfers use TransientJson instead; these helpers never grant proposal authority.
/// <summary>Provides tolerant historical and upstream JSON access, exact numeric comparison and compatible serialization.</summary>
internal static class CiFailureJson
{
    private static readonly JsonSerializerOptions s_contractOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters =
        {
            new ExactEnumConverter<RunScope>(),
            new ExactEnumConverter<EvidenceState>(),
            new ExactEnumConverter<CandidateHistoryState>(),
            new ExactEnumConverter<AnalysisVerdict>(),
            new ExactEnumConverter<JobClassification>(),
            new ExactEnumConverter<TestClassification>(),
            new ExactEnumConverter<CauseType>(),
            new ExactEnumConverter<TestArtifactFormat>(),
        },
    };

    private static readonly JsonSerializerOptions s_compactOptions = new(s_contractOptions) { WriteIndented = false };

    public static JsonNode? Read(string file) => Parse(File.ReadAllText(file));

    internal static T? ReadContract<T>(string file) => ToContract<T>(Read(file));

    internal static T? ToContract<T>(JsonNode? node) => node.Deserialize<T>(s_contractOptions);

    internal static T? ToContract<T>(JsonElement element) => element.Deserialize<T>(s_contractOptions);

    internal static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, s_contractOptions);

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, s_compactOptions);

    internal static string EnumText<T>(T value) where T : struct, Enum
        => Enum.IsDefined(value) ? JsonSerializer.SerializeToElement(value, s_contractOptions).GetString()! : value.ToString();

    internal static bool TryReadEnum<T>(string? text, out T value) where T : struct, Enum
    {
        if (text is null)
        {
            value = default;
            return false;
        }
        try
        {
            value = ToContract<T>(JsonValue.Create(text));
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }

    internal static T ReadEnum<T>(string? text, string message) where T : struct, Enum
    {
        if (!TryReadEnum<T>(text, out var value))
        {
            throw new ValidationException(message);
        }

        return value;
    }

    internal static void WriteContract<T>(string file, T value)
        => File.WriteAllText(file, JsonSerializer.Serialize(value, s_contractOptions) + "\n");

    internal static NormalizedJsonNumber? AsNormalizedNumber(JsonElement? value)
        => value is { ValueKind: JsonValueKind.Number } number ? NormalizedJsonNumber.Parse(number.GetRawText()) : null;

    internal static string? AsString(JsonElement? value)
        => value is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    public static JsonNode? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
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
                    // Duplicate property names use the last value, including escaped spellings
                    // of the same name. Assigning eagerly avoids lazy duplicate-key exceptions.
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
                // Preserve lexical numeric forms in existing durable records.
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
    public static NormalizedJsonNumber? ReadNormalizedNumber(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.Number ? NormalizedJsonNumber.Parse(value.ToJsonString()) : null;

    public static bool IsPositiveInteger(JsonNode? node)
    {
        // Retain the existing tolerant count-metadata check. GitHub identities
        // must use GitHubIdentifiers, never floating-point integrality.
        return ReadNormalizedNumber(node) is { Negative: false, Digits: not "0" } &&
            double.TryParse(node!.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) &&
            number == Math.Floor(number);
    }

    public static bool IsFalse(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var boolean) && !boolean;

    // Strings contribute decoded content; other values retain their JSON representation,
    // including exact numeric tokens. A null node retains the explicit "null" marker.
    public static string GetValueText(JsonNode? node)
    {
        return node switch
        {
            null => "null",
            JsonValue value when Text(value) is { } text => text,
            JsonNode value => value.ToJsonString(),
        };
    }

    public static void Write(string file, JsonNode? node) => File.WriteAllText(
        file,
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

    // Optional upstream objects such as {"user":false} remain unavailable rather
    // than making unrelated run evidence fail. Only annotated properties opt in.
    /// <summary>Reads optional upstream objects while treating other JSON shapes as unavailable.</summary>
    internal sealed class OptionalObjectConverter<T> : JsonConverter<T> where T : class
    {
        public OptionalObjectConverter()
        {
        }

        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                return JsonSerializer.Deserialize<T>(ref reader, options);
            }
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, options);
    }

    // The framework converter also accepts case-insensitive names and numeric
    // strings. Our closed wire vocabularies accept only the explicit JSON names.
    /// <summary>Accepts only explicitly named enum wire values, rejecting numeric and case-insensitive alternatives.</summary>
    private sealed class ExactEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        private readonly JsonConverter<T> _converter = (JsonConverter<T>)
            new JsonStringEnumConverter<T>(allowIntegerValues: false).CreateConverter(typeof(T), new JsonSerializerOptions());

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"{typeof(T).Name} must be a string");
            }
            var text = reader.GetString();
            var value = _converter.Read(ref reader, typeToConvert, options);
            if (text != EnumText(value))
            {
                throw new JsonException($"Unrecognized {typeof(T).Name}: {text}");
            }

            return value;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => _converter.Write(writer, value, options);
    }

    // Partial upstream contracts treat non-string metadata (for example,
    // {"title":false}) as unavailable, just as Text does at the raw JSON boundary.
    /// <summary>Treats non-string optional metadata as unavailable without coercing it into text.</summary>
    internal sealed class OptionalTextConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString();
            }
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
            => writer.WriteStringValue(value);
    }
}

// Keep mathematical JSON number equality without rounding distinct large IDs through double,
// or narrowing the accepted numeric syntax to Int64/decimal. For example 123, 123.0 and 1.23e2
// normalize to the same digits and exponent; 9007199254740992 and 9007199254740993 stay distinct.
/// <summary>Compares mathematical JSON-number values exactly without rounding large identifiers.</summary>
internal readonly record struct NormalizedJsonNumber(string Digits, BigInteger Exponent, bool Negative)
{
    public static NormalizedJsonNumber Parse(string raw)
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
            return new NormalizedJsonNumber("0", BigInteger.Zero, Negative: false);
        }

        var significantDigits = digits.TrimEnd('0');
        exponent += digits.Length - significantDigits.Length;

        return new NormalizedJsonNumber(significantDigits, exponent, negative);
    }
}

/// <summary>Reports invalid analyzer inputs, evidence or operations while preserving an available underlying cause.</summary>
internal sealed class ValidationException : Exception
{
    internal ValidationException(string message) : base(message)
    {
    }

    internal ValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
