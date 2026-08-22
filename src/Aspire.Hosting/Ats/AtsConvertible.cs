// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting;

/// <summary>
/// Defines custom JSON conversion for a type that crosses an Aspire Type System boundary.
/// </summary>
/// <typeparam name="TSelf">The type that implements the conversion contract.</typeparam>
public interface IAtsConvertible<TSelf>
    where TSelf : IAtsConvertible<TSelf>
{
    /// <summary>
    /// Deserializes a JSON object into <typeparamref name="TSelf"/>.
    /// </summary>
    /// <param name="jsonObject">The JSON object to convert.</param>
    /// <returns>The converted instance.</returns>
    static abstract TSelf Deserialize(JsonObject jsonObject);

    /// <summary>
    /// Serializes an instance of <typeparamref name="TSelf"/> to JSON.
    /// </summary>
    /// <param name="value">The value to convert.</param>
    /// <returns>The converted JSON value.</returns>
    static abstract JsonNode? Serialize(TSelf value);
}

/// <summary>
/// Represents an arbitrary JSON object received from or returned to a polyglot AppHost.
/// </summary>
/// <remarks>
/// <example>
/// <code>
/// // User-defined TypeScript object passed to an exported Aspire API.
/// {
///     route: "aspire.dev",
///     match: "http",
///     users: ["chris", "dave", "maddy"]
/// }
/// </code>
/// </example>
/// The object is converted to a dictionary and exposed through <see cref="Value"/>.
/// </remarks>
public sealed class CustomAtsObjectDto : IAtsConvertible<CustomAtsObjectDto>
{
    /// <summary>
    /// Gets the JSON-compatible object values.
    /// </summary>
    [AspireExportIgnore(Reason = "Custom ATS objects are represented as the language's native object type.")]
    public IReadOnlyDictionary<string, object?> Value { get; }

    private CustomAtsObjectDto(Dictionary<string, object?> value)
    {
        Value = value;
    }

    /// <summary>
    /// Deserializes a <see cref="JsonObject"/> into a <see cref="CustomAtsObjectDto"/>.
    /// </summary>
    /// <param name="jsonObject">The JSON object to deserialize.</param>
    /// <returns>A new <see cref="CustomAtsObjectDto"/> containing the deserialized <paramref name="jsonObject"/>.</returns>
    /// <exception cref="NotSupportedException">Thrown if <paramref name="jsonObject"/> contains an unsupported value.</exception>
    public static CustomAtsObjectDto Deserialize(JsonObject jsonObject)
    {
        return new CustomAtsObjectDto(
            jsonObject.ToDictionary(kvp => kvp.Key, kvp => ConvertJsonNode(kvp.Value), StringComparer.Ordinal));

        static object? ConvertJsonNode(JsonNode? node)
        {
            return node switch
            {
                null => null,

                JsonValue value => ConvertJsonValue(value),

                JsonObject obj => obj.ToDictionary(
                    kvp => kvp.Key,
                    kvp => ConvertJsonNode(kvp.Value),
                    StringComparer.Ordinal),

                JsonArray array => array
                    .Select(ConvertJsonNode)
                    .ToList(),

                _ => throw new NotSupportedException(
                    $"Unsupported JsonNode type: {node.GetType().FullName}")
            };
        }

        static object? ConvertJsonValue(JsonValue value)
        {
            // JsonValue can wrap either a JsonElement parsed from JSON or a CLR primitive created in code.
            // SerializeToElement normalizes both representations before inspecting the JSON value kind.
            var element = JsonSerializer.SerializeToElement(value);

            return element.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number when element.TryGetDecimal(out var decimalValue) => decimalValue,
                JsonValueKind.Number => element.Clone(),
                _ => throw new NotSupportedException($"Unsupported JSON value kind '{element.ValueKind}'.")
            };
        }
    }

    /// <summary>
    /// Serializes a <see cref="CustomAtsObjectDto"/> to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <returns>The serialized JSON object.</returns>
    public static JsonNode? Serialize(CustomAtsObjectDto value)
    {
        return JsonSerializer.SerializeToNode(value.Value);
    }
}
