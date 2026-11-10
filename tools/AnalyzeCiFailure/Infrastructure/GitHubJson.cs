// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnalyzeCiFailure;

/// <summary>Reads upstream numeric IDs exactly; durable historical token comparison remains separate.</summary>
internal static class GitHubJson
{
    internal static long ReadRequiredId(JsonNode? node)
        => TryReadId(node, out var id)
            ? id : throw new ValidationException("GitHub metadata must contain a positive 64-bit integer ID.");

    internal static long ReadRequiredId(JsonElement? element)
        => element is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var id) && id > 0
            ? id : throw new ValidationException("GitHub metadata must contain a positive 64-bit integer ID.");

    internal static bool TryReadId(JsonNode? node, out long id)
    {
        id = 0;
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.Number &&
            long.TryParse(value.ToJsonString(), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}
