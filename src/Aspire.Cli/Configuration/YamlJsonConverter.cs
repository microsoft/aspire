// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Aspire.Cli.Configuration;

/// <summary>
/// AOT-safe YAML ↔ JSON converter using YamlDotNet's low-level event-based
/// parser and emitter (no reflection).
/// </summary>
internal static class YamlJsonConverter
{
    /// <summary>
    /// Converts a YAML string to a JSON string.
    /// </summary>
    public static string YamlToJson(string yaml)
    {
        var reader = new StringReader(yaml);
        var parser = new Parser(reader);

        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();

        var jsonNode = ReadValue(parser);

        parser.TryConsume<DocumentEnd>(out _);
        parser.TryConsume<StreamEnd>(out _);

        return jsonNode?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";
    }

    /// <summary>
    /// Converts a JSON string to a YAML string.
    /// </summary>
    public static string JsonToYaml(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is null)
        {
            return "";
        }

        using var writer = new StringWriter();
        var emitter = new Emitter(writer);

        emitter.Emit(new StreamStart());
        emitter.Emit(new DocumentStart(null, null, true));

        EmitJsonNode(emitter, node);

        emitter.Emit(new DocumentEnd(true));
        emitter.Emit(new StreamEnd());

        return writer.ToString();
    }

    private static JsonNode? ReadValue(IParser parser)
    {
        if (parser.TryConsume<Scalar>(out var scalar))
        {
            return ConvertScalar(scalar);
        }

        if (parser.TryConsume<MappingStart>(out _))
        {
            return ReadMapping(parser);
        }

        if (parser.TryConsume<SequenceStart>(out _))
        {
            return ReadSequence(parser);
        }

        // Null / alias nodes we don't support — treat as null
        parser.MoveNext();
        return null;
    }

    private static JsonObject ReadMapping(IParser parser)
    {
        var obj = new JsonObject();

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>();
            var value = ReadValue(parser);
            obj[key.Value] = value;
        }

        return obj;
    }

    private static JsonArray ReadSequence(IParser parser)
    {
        var arr = new JsonArray();

        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            arr.Add(ReadValue(parser));
        }

        return arr;
    }

    private static JsonNode? ConvertScalar(Scalar scalar)
    {
        // Quoted strings are always strings
        if (scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted)
        {
            return JsonValue.Create(scalar.Value);
        }

        var value = scalar.Value;

        // Plain scalars: resolve YAML core schema types
        if (scalar.Style == ScalarStyle.Plain)
        {
            // Null
            if (string.IsNullOrEmpty(value) ||
                string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "~", StringComparison.Ordinal))
            {
                return null;
            }

            // Boolean
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return JsonValue.Create(true);
            }

            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                return JsonValue.Create(false);
            }

            // Integer
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                return JsonValue.Create(l);
            }

            // Float
            if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d))
            {
                return JsonValue.Create(d);
            }
        }

        return JsonValue.Create(value);
    }

    private static void EmitJsonNode(IEmitter emitter, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                emitter.Emit(new MappingStart(null, null, true, MappingStyle.Block));
                foreach (var (key, value) in obj)
                {
                    emitter.Emit(new Scalar(null, null, key, ScalarStyle.Plain, true, false));
                    EmitJsonNode(emitter, value);
                }
                emitter.Emit(new MappingEnd());
                break;

            case JsonArray arr:
                emitter.Emit(new SequenceStart(null, null, true, SequenceStyle.Block));
                foreach (var item in arr)
                {
                    EmitJsonNode(emitter, item);
                }
                emitter.Emit(new SequenceEnd());
                break;

            case JsonValue val:
                EmitJsonValue(emitter, val);
                break;

            default:
                // null
                emitter.Emit(new Scalar(null, null, "null", ScalarStyle.Plain, true, false));
                break;
        }
    }

    private static void EmitJsonValue(IEmitter emitter, JsonValue val)
    {
        if (val.TryGetValue<bool>(out var b))
        {
            emitter.Emit(new Scalar(null, null, b ? "true" : "false", ScalarStyle.Plain, true, false));
        }
        else if (val.TryGetValue<long>(out var l))
        {
            emitter.Emit(new Scalar(null, null, l.ToString(CultureInfo.InvariantCulture), ScalarStyle.Plain, true, false));
        }
        else if (val.TryGetValue<int>(out var i))
        {
            emitter.Emit(new Scalar(null, null, i.ToString(CultureInfo.InvariantCulture), ScalarStyle.Plain, true, false));
        }
        else if (val.TryGetValue<double>(out var d))
        {
            emitter.Emit(new Scalar(null, null, d.ToString(CultureInfo.InvariantCulture), ScalarStyle.Plain, true, false));
        }
        else if (val.TryGetValue<string>(out var s))
        {
            // Use double-quoted style for strings that could be misinterpreted as YAML types
            var style = NeedsQuoting(s) ? ScalarStyle.DoubleQuoted : ScalarStyle.Plain;
            emitter.Emit(new Scalar(null, null, s, style, true, style == ScalarStyle.DoubleQuoted));
        }
        else
        {
            var raw = val.ToJsonString();
            emitter.Emit(new Scalar(null, null, raw, ScalarStyle.Plain, true, false));
        }
    }

    /// <summary>
    /// Returns true if a string value needs quoting in YAML to avoid
    /// being interpreted as a non-string type.
    /// </summary>
    private static bool NeedsQuoting(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        // Values that look like booleans, nulls, or numbers need quoting
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "~", StringComparison.Ordinal) ||
            string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "on", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "off", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        // YAML special characters at start of value
        if (value.Length > 0 && value[0] is '{' or '[' or '&' or '*' or '!' or '|' or '>' or '%' or '@' or '`' or '#' or ',')
        {
            return true;
        }

        // Contains characters that could cause issues
        if (value.Contains(": ") || value.Contains(" #"))
        {
            return true;
        }

        return false;
    }
}
