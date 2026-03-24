// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Utils;

namespace Aspire.Dashboard.Model.GenAI;

[DebuggerDisplay("Name = {Name}, Value = {Value}")]
public sealed class GenAIPartPropertyViewModel : IPropertyGridItem
{
    public required string Name { get; set; }
    public required string Value { get; set; }
}

public sealed class GenAIItemPartViewModel
{
    private static readonly JsonSerializerOptions s_jsonSerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    public MessagePart? MessagePart { get; init; }
    public string? ErrorMessage { get; init; }
    public required TextVisualizerViewModel TextVisualizerViewModel { get; init; }
    public List<GenAIPartPropertyViewModel>? AdditionalProperties { get; init; }

    public bool TryGetPropertyValue(string propertyName, [NotNullWhen(true)] out string? value)
    {
        value = AdditionalProperties?.SingleOrDefault(p => p.Name == propertyName)?.Value;
        return value != null;
    }

    private GenAIItemPartViewModel()
    {
    }

    public static GenAIItemPartViewModel CreateErrorMessage(string errorMessage)
    {
        return new GenAIItemPartViewModel
        {
            ErrorMessage = errorMessage,
            TextVisualizerViewModel = new TextVisualizerViewModel(errorMessage, indentText: false)
        };
    }

    public static GenAIItemPartViewModel CreateMessagePart(MessagePart part)
    {
        return new GenAIItemPartViewModel
        {
            MessagePart = part,
            ErrorMessage = part is UnexpectedErrorPart errorPart ? errorPart.Error?.Message : null,
            TextVisualizerViewModel = CreateMessagePartVisualizer(part),
            AdditionalProperties = part is GenericPart genericPart
                ? genericPart.AdditionalProperties?.Select(p => new GenAIPartPropertyViewModel { Name = p.Key, Value = p.Value.ToString() ?? string.Empty }).ToList()
                : null
        };
    }

    private static TextVisualizerViewModel CreateMessagePartVisualizer(MessagePart p)
    {
        if (p is TextPart textPart)
        {
            return new TextVisualizerViewModel(textPart.Content ?? string.Empty, indentText: true, fallbackFormat: DashboardUIHelpers.MarkdownFormat);
        }
        if (p is ToolCallRequestPart toolCallRequestPart)
        {
            var argumentsText = toolCallRequestPart.Arguments switch
            {
                null => string.Empty,
                JsonObject obj when obj.Count == 0 => string.Empty,
                JsonArray arr when arr.Count == 0 => string.Empty,
                _ => toolCallRequestPart.Arguments.ToJsonString(s_jsonSerializerOptions)
            };

            return new TextVisualizerViewModel($"{toolCallRequestPart.Name}({argumentsText})", indentText: true, knownFormat: DashboardUIHelpers.JavascriptFormat);
        }
        if (p is ToolCallResponsePart toolCallResponsePart)
        {
            // If a tool response is a string then decode it.
            // This handles situations where telemetry is reported incorrectly, i.e. a structured JSON response is encoded inside a string.
            // And it allows possible Markdown content inside the string to be formatted.
            var toolResponseContent = (toolCallResponsePart.Response?.GetValueKind() == JsonValueKind.String)
                ? toolCallResponsePart.Response.GetValue<string>()
                : toolCallResponsePart.Response?.ToJsonString(s_jsonSerializerOptions) ?? string.Empty;

            return new TextVisualizerViewModel(toolResponseContent, indentText: true, fallbackFormat: DashboardUIHelpers.MarkdownFormat);
        }
        if (p is BlobPart blobPart)
        {
            return new TextVisualizerViewModel(blobPart.Content ?? string.Empty, indentText: true);
        }
        if (p is UriPart uriPart)
        {
            return new TextVisualizerViewModel(uriPart.Uri ?? string.Empty, indentText: true);
        }
        if (p is FilePart filePart)
        {
            return new TextVisualizerViewModel(filePart.FileId ?? string.Empty, indentText: true);
        }
        if (p is ReasoningPart reasoningPart)
        {
            return new TextVisualizerViewModel(reasoningPart.Content ?? string.Empty, indentText: true, fallbackFormat: DashboardUIHelpers.MarkdownFormat);
        }
        if (p is ServerToolCallPart serverToolCallPart)
        {
            var serverToolCallText = serverToolCallPart.ServerToolCall switch
            {
                null => string.Empty,
                JsonObject obj when obj.Count == 0 => string.Empty,
                JsonArray arr when arr.Count == 0 => string.Empty,
                _ => serverToolCallPart.ServerToolCall.ToJsonString(s_jsonSerializerOptions)
            };

            return new TextVisualizerViewModel($"{serverToolCallPart.Name}({serverToolCallText})", indentText: true, knownFormat: DashboardUIHelpers.JavascriptFormat);
        }
        if (p is ServerToolCallResponsePart serverToolCallResponsePart)
        {
            var responseContent = (serverToolCallResponsePart.ServerToolCallResponse?.GetValueKind() == JsonValueKind.String)
                ? serverToolCallResponsePart.ServerToolCallResponse.GetValue<string>()
                : serverToolCallResponsePart.ServerToolCallResponse?.ToJsonString(s_jsonSerializerOptions) ?? string.Empty;

            return new TextVisualizerViewModel(responseContent, indentText: true, fallbackFormat: DashboardUIHelpers.MarkdownFormat);
        }

        var additionalProperties = p is GenericPart genericPart ? genericPart.AdditionalProperties ?? [] : [];
        var content = additionalProperties.Count > 0 ? ToJsonObject(additionalProperties).ToJsonString(s_jsonSerializerOptions) : string.Empty;

        return new TextVisualizerViewModel(content, indentText: true);
    }

    private static JsonObject ToJsonObject(Dictionary<string, JsonElement> dict)
    {
        var jsonObject = new JsonObject();

        foreach (var kvp in dict)
        {
            jsonObject[kvp.Key] = JsonNode.Parse(kvp.Value.GetRawText());
        }

        return jsonObject;
    }
}
