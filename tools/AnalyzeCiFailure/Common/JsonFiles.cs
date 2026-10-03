// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AnalyzeCiFailure.Common;

internal static class JsonFiles
{
    /// <summary>
    /// Binds snake_case JSON to the document records. Non-nullable members must be present and
    /// non-null, so a trusted document that binds successfully has the shape the validator relies on.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <summary>Like <see cref="Options"/>, but a JSON property with no matching member is an error.</summary>
    public static readonly JsonSerializerOptions StrictOptions = new(Options)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions s_writeOptions = new()
    {
        WriteIndented = true,
        // Keep non-ASCII text readable, as jq does for the other files these helpers write.
        // Control characters are still escaped, and the output is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <exception cref="JsonException">The file is not valid JSON or does not match <typeparamref name="T"/>.</exception>
    public static T Read<T>(string path, JsonSerializerOptions? options = null)
    {
        using var stream = File.OpenRead(path);

        // A literal `null` document binds to null; treat it as a shape mismatch like any other.
        return JsonSerializer.Deserialize<T>(stream, options ?? Options)
            ?? throw new JsonException($"{Path.GetFileName(path)} is null", path: "$", lineNumber: null, bytePositionInLine: null);
    }

    public static bool IsValidJson(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Edits a document in place. Used for agent-written files whose full schema the validator
    /// does not own, so fields it does not model survive the rewrite unchanged.
    /// </summary>
    public static void Update(string path, Action<JsonNode> update)
    {
        var root = JsonNode.Parse(File.ReadAllText(path))!;
        update(root);
        File.WriteAllText(path, root.ToJsonString(s_writeOptions) + "\n");
    }
}
