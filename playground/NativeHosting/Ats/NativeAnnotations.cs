// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting;

namespace NativeHosting;

/// <summary>Declares one scalar field in a portable singleton annotation.</summary>
[AspireDto]
public sealed class AnnotationFieldOptions
{
    public required string Name { get; init; }
    /// <summary>Gets the field kind: string, number, or boolean.</summary>
    public required string Type { get; init; }
    public bool Required { get; init; } = true;
}

internal sealed class NativeAnnotations
{
    private readonly Dictionary<string, JsonObject> _schemas = new(StringComparer.Ordinal);

    public bool Define(string id, AnnotationFieldOptions[] fields)
    {
        if (id.Length is 0 or > 128 || id.Count(character => character == '/') != 1 ||
            id.Split('/').Any(part => part.Length == 0) ||
            id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '/')))
        {
            throw new ArgumentException("Annotation IDs must have the form 'namespace/name' using ASCII letters, digits, dots, or hyphens.");
        }
        if (fields.Length is 0 or > 32)
        {
            throw new ArgumentException("An annotation requires between 1 and 32 scalar fields.");
        }

        var schema = new JsonObject();
        foreach (var field in fields.OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            if (field.Name.Length is 0 or > 64 ||
                !char.IsAsciiLetter(field.Name[0]) ||
                field.Name.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
            {
                throw new ArgumentException("Annotation field names must start with an ASCII letter and contain only ASCII letters, digits, or underscores.");
            }
            if (field.Type is not ("string" or "number" or "boolean"))
            {
                throw new ArgumentException($"Unsupported annotation field type '{field.Type}'.");
            }
            if (schema.ContainsKey(field.Name))
            {
                throw new ArgumentException($"Duplicate annotation field '{field.Name}'.");
            }
            schema[field.Name] = new JsonObject { ["type"] = field.Type, ["required"] = field.Required };
        }

        if (_schemas.TryGetValue(id, out var existing))
        {
            if (!JsonNode.DeepEquals(existing, schema))
            {
                throw new InvalidOperationException($"Annotation '{id}' is already registered with a different schema.");
            }
            return true;
        }

        _schemas.Add(id, schema);
        return true;
    }

    public void RequireDefined(string id)
    {
        if (!_schemas.ContainsKey(id))
        {
            throw new ArgumentException($"Annotation '{id}' has no registered schema in this graph.");
        }
    }

    public JsonObject Validate(string id, string json)
    {
        RequireDefined(id);
        if (Encoding.UTF8.GetByteCount(json) > 65536)
        {
            throw new ArgumentException("Annotation payloads must not exceed 64 KiB.");
        }

        // Portable payloads are scalar JSON objects, for example:
        // {"intervalMs":60000,"keysChangedThreshold":100,"enabled":true}.
        // Reject duplicate properties instead of accepting the parser's last value.
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Annotation payloads must be JSON objects.");
        }
        var schema = _schemas[id];
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new ArgumentException($"Duplicate annotation property '{property.Name}'.");
            }
            if (schema[property.Name] is not JsonObject field)
            {
                throw new ArgumentException($"Unknown annotation property '{property.Name}'.");
            }
            var valid = field["type"]!.GetValue<string>() switch
            {
                "string" => property.Value.ValueKind == JsonValueKind.String,
                "number" => property.Value.ValueKind == JsonValueKind.Number,
                "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => false
            };
            if (!valid)
            {
                throw new ArgumentException($"Annotation property '{property.Name}' must have type '{field["type"]!.GetValue<string>()}'.");
            }
            if (property.Value.ValueKind == JsonValueKind.Number &&
                (!property.Value.TryGetDouble(out var number) || !double.IsFinite(number)))
            {
                throw new ArgumentException($"Annotation property '{property.Name}' must be a finite number.");
            }
        }
        foreach (var field in schema)
        {
            if (field.Value!["required"]!.GetValue<bool>() && !names.Contains(field.Key))
            {
                throw new ArgumentException($"Missing required annotation property '{field.Key}'.");
            }
        }

        return JsonNode.Parse(json)!.AsObject();
    }

    public JsonObject Describe() => new(_schemas.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry =>
        new KeyValuePair<string, JsonNode?>(entry.Key, entry.Value.DeepClone())));
}
