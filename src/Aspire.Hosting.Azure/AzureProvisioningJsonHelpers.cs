// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Azure;

/// <summary>
/// JSON helpers for Azure provisioning command payloads and persisted deployment state.
/// </summary>
internal static class AzureProvisioningJsonHelpers
{
    private static readonly JsonDocumentOptions s_deploymentStateJsonDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly JsonSerializerOptions s_commandResultJsonSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    internal static JsonNode? ParseDeploymentStateJson(string json)
    {
        // Deployment state stores JSON payloads as strings, for example:
        //   Outputs = { "id": { "type": "String", "value": "/subscriptions/..." } }
        // These values can be hand-edited while recovering local state, so tolerate
        // JSONC-style comments and trailing commas when reading cached state.
        return JsonNode.Parse(json, documentOptions: s_deploymentStateJsonDocumentOptions);
    }

    internal static string ToCommandResultJsonString(JsonObject json)
        => json.ToJsonString(s_commandResultJsonSerializerOptions);
}
