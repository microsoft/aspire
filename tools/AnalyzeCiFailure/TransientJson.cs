// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure;

/// <summary>Strict decoding for versioned tool-owned transfers; never used to rewrite durable history.</summary>
internal static class TransientJson
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions s_options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        AllowOutOfOrderMetadataProperties = true,
        WriteIndented = true,
        Converters =
        {
            new CanonicalIdConverter(),
            new PositiveIntegerConverter(),
            new StrictEnumConverter<RunScope>(),
            new StrictEnumConverter<EvidenceState>(),
            new StrictEnumConverter<CandidateHistoryState>(),
            new StrictEnumConverter<JobClassification>(),
            new StrictEnumConverter<TestClassification>(),
            new StrictEnumConverter<CauseType>(),
            new StrictEnumConverter<TestArtifactFormat>(),
            new StrictEnumConverter<AnalysisVerdict>(),
        },
    };

    internal static T Read<T>(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            CheckPropertiesAndStrings(document.RootElement);
            var value = document.RootElement.Deserialize<T>(s_options) ?? throw new JsonException("The document must be an object, not null.");
            if (value is EvidenceBundle { SchemaVersion: not 1 } or AnalysisProposal { SchemaVersion: not 1 } or
                AnalysisOutcome { SchemaVersion: not 1 })
            {
                throw new JsonException("Unsupported contract schema version.");
            }
            if (value is AnalysisOutcome outcome)
            {
                CheckOutcome(outcome);
            }
            return value;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ValidationException("Cannot decode the versioned analysis contract: " +
                CiFailureJson.Display(DiagnosticTextSanitizer.Sanitize(ex.Message, limit: 1000)));
        }
    }

    internal static string Write<T>(T value) => JsonSerializer.Serialize(value, s_options) + "\n";
    internal static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, s_options);
    internal static T ReadUtf8<T>(ReadOnlySpan<byte> bytes) => Read<T>(DecodeUtf8(bytes));

    internal static string DecodeUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var text = s_utf8.GetString(bytes);
            return text.StartsWith('\uFEFF') ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            throw new ValidationException("Cannot decode JSON: files must contain valid UTF-8 text.");
        }
    }

    private static void CheckOutcome(AnalysisOutcome outcome)
    {
        if (outcome.Status is not ("ready" or "no-work" or "degraded" or "failed") ||
            outcome.Problems.IsDefault || outcome.Problems.Any(problem => problem is null || problem.Stage is null || problem.Message is null) ||
            outcome.Status is "ready" or "degraded" && (outcome.HasRelevantFailures != true || outcome.RunId is null || outcome.RunAttempt is null) ||
            outcome.Status == "no-work" && (outcome.HasRelevantFailures != false || outcome.RunId is null) ||
            outcome.Status == "failed" && outcome.Problems.Length == 0)
        {
            throw new JsonException("The collection outcome has an invalid status, facts, or problem array.");
        }
    }

    internal static long ParseId(string text)
    {
        if (text.Length == 0 || text[0] is < '1' or > '9' || text.Any(character => character is < '0' or > '9') ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new ValidationException("An identifier must be canonical positive 64-bit decimal text without signs, spaces, or leading zeros.");
        }
        return value;
    }

    private static void CheckPropertiesAndStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                CheckString(property.Name);
                if (!names.Add(property.Name))
                {
                    throw new JsonException("A JSON object contains a duplicate decoded property name.");
                }
                CheckPropertiesAndStrings(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CheckPropertiesAndStrings(item);
            }
        }
        else if (element.ValueKind == JsonValueKind.String)
        {
            CheckString(element.GetString()!);
        }
    }

    private static void CheckString(string value)
    {
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done)
            {
                throw new JsonException("JSON strings must contain well-formed Unicode.");
            }
            remaining = remaining[consumed..];
        }
    }

    /// <summary>Transfers positive 64-bit identifiers as canonical decimal strings without numeric coercion.</summary>
    private sealed class CanonicalIdConverter : JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Identifiers must be JSON strings containing canonical decimal digits.");
            }

            return ParseId(reader.GetString()!);
        }

        public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
        {
            if (value <= 0)
            {
                throw new JsonException("Identifiers must be positive.");
            }
            writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }
    }

    /// <summary>Requires a positive 32-bit JSON integer for versioned attempt and step-number fields.</summary>
    private sealed class PositiveIntegerConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) && value > 0
                ? value : throw new JsonException("Versions, attempts, and step numbers must be positive 32-bit JSON integers.");

        public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        {
            if (value <= 0)
            {
                throw new JsonException("Versions, attempts, and step numbers must be positive.");
            }
            writer.WriteNumberValue(value);
        }
    }

    /// <summary>Decodes only the exact string values defined by a versioned contract&apos;s enum vocabulary.</summary>
    private sealed class StrictEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String &&
                CiFailureJson.TryReadEnum<T>(reader.GetString(), out var value))
            {
                return value;
            }
            throw new JsonException($"Unrecognized {typeof(T).Name}; use its documented string value.");
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value))
            {
                throw new JsonException($"Unrecognized {typeof(T).Name}.");
            }
            writer.WriteStringValue(CiFailureJson.EnumText(value));
        }
    }
}
