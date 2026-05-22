// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Ats;

/// <summary>
/// Represents an object that has custom conversion logic for crossing ATS boundaries.
/// </summary>
public interface IAtsConvertable 
{
    /// <summary>
    /// Deserializes the given JSON object into the implementing class's type.
    /// </summary>
    /// <param name="jsonObj">The JSON document to convert.</param>
    /// <returns>The deserialized object.</returns>
    static abstract object? Deserialize(JsonObject jsonObj);

    /// <summary>
    /// Serializes the given object to JSON.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <returns>A JSON object matching the given value.</returns>
    static abstract JsonNode? Serialize(object value);
}

/// <summary>
/// Represents an object that is convertable across language boundaries through the ATS system.
/// </summary>
/// <remarks>
/// This implementation of <see cref="IAtsConvertable"/> allows for completely generic objects to be sent
/// across language boundaries.
/// <example>
/// 
/// <code>
/// // User-defined custom TypeScript object
/// {
///     route: "aspire.dev",
///     match: "http",
///     users: ["chris", "dave", "maddy"]
/// }
/// </code>
/// </example>
/// The above object will get serialized into the <see cref="Object"/> property as a <see cref="Dictionary{TKey, TValue}"/>.
/// </remarks>
/// <ats-summary>An object that supports serialization of custom properties.</ats-summary>
[AspireDto]
[AspireExport]
public class CustomAtsObjectDto : IAtsConvertable
{
    /// <summary>
    /// Contains the result of deserialization.
    /// </summary>
    [AspireExportIgnore]
    internal Dictionary<string, object?>? Object { get; set; }

    /// <summary>
    /// Deserializes a <see cref="JsonObject"/> into a Dictionary. A new <see cref="CustomAtsObjectDto"/> will be returned 
    /// with the results of the deserialization set to the object's <see cref="Object"/> property.
    /// </summary>
    /// <param name="jsonObj">The JSON value to deserialize.</param>
    /// <returns>A new <see cref="CustomAtsObjectDto"/> containing the deserialized <paramref name="jsonObj"/>.</returns>
    /// <exception cref="NotSupportedException">Thrown if the <paramref name="jsonObj"/> contains an unsupported type.</exception>
    public static object? Deserialize(JsonObject jsonObj)
    {
        if (jsonObj is null)
        {
            return null;
        }

        return new CustomAtsObjectDto()
        {
            Object = jsonObj.ToDictionary(kvp => kvp.Key, kvp => ConvertJsonNode(kvp.Value))
        };

        static object? ConvertJsonNode(JsonNode? node)
        {
            return node switch
            {
                null => null,

                JsonValue value => ConvertJsonValue(value),

                JsonObject obj => obj.ToDictionary(
                    kvp => kvp.Key,
                    kvp => ConvertJsonNode(kvp.Value)),

                JsonArray array => array
                    .Select(ConvertJsonNode)
                    .ToList(),

                _ => throw new NotSupportedException(
                    $"Unsupported JsonNode type: {node.GetType().FullName}")
            };
        }

        static object? ConvertJsonValue(JsonValue value)
        {
            var element = value.GetValue<JsonElement>();

            return value.GetValueKind() switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number when element.TryGetInt64(out var longValue) => longValue,
                JsonValueKind.Number => element.GetDouble(),
                _ => throw new NotSupportedException($"Unsupported JSON value kind '{value.GetValueKind()}'.")
            };
        }
    }

    /// <summary>
    /// Forwards serialization to the application's default <see cref="JsonSerializer"/>.
    /// </summary>
    /// <param name="value">The value to serialize.</param>
    /// <returns>The serialized JSON.</returns>
    public static JsonNode? Serialize(object value)
    {
        return JsonSerializer.Serialize(value);
    }
}