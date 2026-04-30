// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.TypeSystem;

/// <summary>
/// Provides JSON literal formatting helpers for generated ATS source code.
/// </summary>
public static class AtsJsonCodeWriter
{
    private static readonly JsonSerializerOptions s_relaxedJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Formats a JSON node using relaxed escaping so non-ASCII content remains readable in generated source.
    /// </summary>
    public static string ToRelaxedJsonString(this JsonNode value)
    {
        return value.ToJsonString(s_relaxedJsonOptions);
    }

    /// <summary>
    /// Formats a string literal as JSON using relaxed escaping.
    /// </summary>
    public static string ToRelaxedJsonString(string value)
    {
        return JsonValue.Create(value)!.ToJsonString(s_relaxedJsonOptions);
    }
}
