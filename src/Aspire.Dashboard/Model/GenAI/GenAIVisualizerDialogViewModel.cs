// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;

namespace Aspire.Dashboard.Model.GenAI;

[DebuggerDisplay("Span = {Span.SpanId}, Title = {Title}, Items = {Items.Count}")]
public sealed class GenAIVisualizerDialogViewModel
{
    // The exact name doesn't matter. A value is required when resolving color for peer.
    private const string UnknownPeerName = "unknown-peer";

    public required OtlpSpan Span { get; init; }
    public required string Title { get; init; }
    public required SpanDetailsViewModel SpanDetailsViewModel { get; init; }
    public required long? SelectedLogEntryId { get; init; }
    public required Func<List<OtlpSpan>> GetContextGenAISpans { get; init; }
    public required string PeerName { get; init; }
    public required string SourceName { get; init; }

    public FluentTreeItem? SelectedTreeItem { get; set; }
    public List<GenAIItemViewModel> Items { get; } = new List<GenAIItemViewModel>();
    public List<GenAIItemViewModel> InputMessages { get; private set; } = default!;
    public List<GenAIItemViewModel> OutputMessages { get; private set; } = default!;
    public GenAIItemViewModel? ErrorItem { get; private set; }
    public List<ToolDefinitionViewModel> ToolDefinitions { get; private set; } = new();
    public List<EvaluationResultViewModel> Evaluations { get; private set; } = new();

    // Used for error message from the dashboard when displaying GenAI telemetry.
    public string? DisplayErrorMessage { get; set; }

    public bool NoMessageContent { get; set; }
    public string? ModelName { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    public static GenAIVisualizerDialogViewModel Create(
        SpanDetailsViewModel spanDetailsViewModel,
        long? selectedLogEntryId,
        ITelemetryErrorRecorder errorRecorder,
        TelemetryRepository telemetryRepository,
        Func<List<OtlpSpan>> getContextGenAISpans)
    {
        var resources = telemetryRepository.GetResources();

        var viewModel = new GenAIVisualizerDialogViewModel
        {
            Span = spanDetailsViewModel.Span,
            Title = SpanWaterfallViewModel.GetTitle(spanDetailsViewModel.Span, spanDetailsViewModel.Resources),
            SpanDetailsViewModel = spanDetailsViewModel,
            SelectedLogEntryId = selectedLogEntryId,
            GetContextGenAISpans = getContextGenAISpans,
            SourceName = OtlpResource.GetResourceName(spanDetailsViewModel.Span.Source, resources),
            PeerName = telemetryRepository.GetPeerResource(spanDetailsViewModel.Span) is { } peerResource
                ? OtlpResource.GetResourceName(peerResource, resources)
                : OtlpHelpers.GetPeerAddress(spanDetailsViewModel.Span.Attributes) ?? UnknownPeerName
        };

        viewModel.ModelName = viewModel.Span.Attributes.GetValue(GenAIHelpers.GenAIResponseModel);
        viewModel.InputTokens = viewModel.Span.Attributes.GetValueAsInteger(GenAIHelpers.GenAIUsageInputTokens);
        viewModel.OutputTokens = viewModel.Span.Attributes.GetValueAsInteger(GenAIHelpers.GenAIUsageOutputTokens);

        // Parse tool definitions if present
        var toolDefinitionsJson = viewModel.Span.Attributes.GetValue(GenAIHelpers.GenAIToolDefinitions);
        if (!string.IsNullOrEmpty(toolDefinitionsJson))
        {
            try
            {
                // Deserialize to intermediate format since OpenApiSchema doesn't work well with System.Text.Json
                var jsonNode = JsonNode.Parse(toolDefinitionsJson);
                if (jsonNode is JsonArray array)
                {
                    viewModel.ToolDefinitions = new List<ToolDefinitionViewModel>();
                    foreach (var item in array)
                    {
                        if (item is not JsonObject obj)
                        {
                            continue;
                        }

                        var toolDef = new ToolDefinition
                        {
                            Type = obj["type"]?.GetValue<string>() ?? "function",
                            Name = obj["name"]?.GetValue<string>(),
                            Description = obj["description"]?.GetValue<string>()
                        };

                        // Parse parameters if present
                        if (obj["parameters"] is JsonObject paramsObj)
                        {
                            toolDef.Parameters = ParseOpenApiSchema(paramsObj);
                        }

                        viewModel.ToolDefinitions.Add(new ToolDefinitionViewModel { ToolDefinition = toolDef });
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't fail the entire view model creation
                errorRecorder.RecordError($"Error parsing tool definitions for span {viewModel.Span.SpanId}", ex, writeToLogging: true);
                viewModel.ToolDefinitions = new List<ToolDefinitionViewModel>();
            }
        }

        try
        {
            CreateMessages(viewModel, telemetryRepository);
        }
        catch (Exception ex)
        {
            // We're catching errors here to avoid it going to Blazor global error handling. But we still want to record errors from reading messages to telemetry.
            // This can be changed to just using logging once we have confidence that we're handling popular content well.
            errorRecorder.RecordError($"Error reading GenAI telemetry messages for span {viewModel.Span.SpanId}", ex, writeToLogging: true);

            // There could be invalid or unexpected message JSON that causes deserialization to fail. Display an error message.
            var sb = new StringBuilder();
            var current = ex;
            while (current != null)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.AppendLine(CultureInfo.InvariantCulture, $"{current.GetType().FullName}: {current.Message}");
                current = current.InnerException;
            }

            viewModel.DisplayErrorMessage = sb.ToString();
            viewModel.Items.Clear();

            return viewModel;
        }

        try
        {
            ParseEvaluations(viewModel, telemetryRepository);
        }
        catch (Exception ex)
        {
            // Log error but don't fail the entire view model creation
            errorRecorder.RecordError($"Error parsing GenAI evaluation results for span {viewModel.Span.SpanId}", ex, writeToLogging: true);
            viewModel.Evaluations = new List<EvaluationResultViewModel>();
        }

        if (viewModel.Span.Status == OtlpSpanStatusCode.Error)
        {
            var errorMessage = FormatHelpers.CombineWithSeparator(
                Environment.NewLine + Environment.NewLine,
                viewModel.Span.Attributes.GetValue("error.type"),
                viewModel.Span.StatusMessage);

            viewModel.ErrorItem = new GenAIItemViewModel
            {
                Index = viewModel.Items.Count,
                InternalId = null,
                ItemParts = [GenAIItemPartViewModel.CreateErrorMessage(errorMessage)],
                Parent = viewModel.Span,
                ResourceName = viewModel.PeerName,
                Type = GenAIItemType.Error
            };
            viewModel.Items.Add(viewModel.ErrorItem);
        }

        viewModel.InputMessages = viewModel.Items.Where(e => e.Type is GenAIItemType.SystemMessage or GenAIItemType.UserMessage or GenAIItemType.AssistantMessage or GenAIItemType.ToolMessage).ToList();
        viewModel.OutputMessages = viewModel.Items.Where(e => e.Type == GenAIItemType.OutputMessage).ToList();

        viewModel.NoMessageContent = AllMessagesHaveNoContent(viewModel.InputMessages) && AllMessagesHaveNoContent(viewModel.OutputMessages) && viewModel.ErrorItem == null;

        return viewModel;
    }

    private static bool AllMessagesHaveNoContent(List<GenAIItemViewModel> messageViewModels)
    {
        if (messageViewModels.Count == 0)
        {
            // Microsoft.Extensions.AI doesn't output any message telemetry when sensitive data isn't enabled.
            return true;
        }

        foreach (var messageViewModel in messageViewModels)
        {
            foreach (var partViewModel in messageViewModel.ItemParts)
            {
                if (partViewModel.MessagePart is TextPart textPart)
                {
                    if (!string.IsNullOrEmpty(textPart.Content))
                    {
                        return false;
                    }
                }
                else if (partViewModel.MessagePart is ToolCallRequestPart toolCallRequestPart)
                {
                    if (toolCallRequestPart.Arguments != null)
                    {
                        return false;
                    }
                }
                else if (partViewModel.MessagePart is ToolCallResponsePart toolCallResponsePart)
                {
                    if (toolCallResponsePart.Response != null)
                    {
                        return false;
                    }
                }
                else if (partViewModel.MessagePart is GenericPart)
                {
                    return false;
                }
            }
        }

        return true;
    }

    // The standard for recording messages has changed a number of times. Try to get messages in the following order:
    // - Span attributes.
    // - Log entry bodies.
    // - Span event attributes.
    private static void CreateMessages(GenAIVisualizerDialogViewModel viewModel, TelemetryRepository telemetryRepository)
    {
        var currentIndex = 0;

        var systemInstructions = viewModel.Span.Attributes.GetValue(GenAIHelpers.GenAISystemInstructions);
        var inputMessages = viewModel.Span.Attributes.GetValue(GenAIHelpers.GenAIInputMessages);
        var outputMessages = viewModel.Span.Attributes.GetValue(GenAIHelpers.GenAIOutputInstructions);

        if (!string.IsNullOrEmpty(systemInstructions) || !string.IsNullOrEmpty(inputMessages) || !string.IsNullOrEmpty(outputMessages))
        {
            if (!string.IsNullOrEmpty(systemInstructions))
            {
                var instructionParts = DeserializeWithErrorHandling(GenAIHelpers.GenAISystemInstructions, systemInstructions, GenAIMessagesContext.Default.ListMessagePart)!;
                viewModel.Items.Add(CreateMessage(viewModel, currentIndex, GenAIItemType.SystemMessage, instructionParts.Select(GenAIItemPartViewModel.CreateMessagePart).ToList(), internalId: null));
                currentIndex++;
            }
            if (!string.IsNullOrEmpty(inputMessages))
            {
                ParseMessages(viewModel, inputMessages, GenAIHelpers.GenAIInputMessages, isOutput: false, ref currentIndex);
            }
            if (!string.IsNullOrEmpty(outputMessages))
            {
                ParseMessages(viewModel, outputMessages, GenAIHelpers.GenAIOutputInstructions, isOutput: true, ref currentIndex);
            }

            return;
        }

        // Attempt to get messages from log entries.
        var logEntries = GetSpanLogEntries(telemetryRepository, viewModel.Span);
        foreach (var (item, index) in logEntries.OrderBy(i => i.TimeStamp).Select((l, i) => (l, i)))
        {
            if (!string.IsNullOrEmpty(item.Message) && OtlpHelpers.GetEventName(item) is { } name && TryMapEventName(name, out var type))
            {
                var parts = DeserializeEventContent(index, type.Value, item.Message);
                viewModel.Items.Add(CreateMessage(viewModel, currentIndex, type.Value, parts, internalId: item.InternalId));
                currentIndex++;
            }
        }

        if (viewModel.Items.Count > 0)
        {
            return;
        }

        // Attempt get get messages from span events.
        foreach (var (item, index) in viewModel.Span.Events.OrderBy(i => i.Time).Select((l, i) => (l, i)))
        {
            // Detect GenAI messages by event name. Don't check for the gen_ai.system attribute because it's optional on events.
            if (TryMapEventName(item.Name, out var type))
            {
                var content = item.Attributes.GetValue(GenAIHelpers.GenAIEventContent);
                if (!string.IsNullOrEmpty(content))
                {
                    var parts = DeserializeEventContent(index, type.Value, content);
                    viewModel.Items.Add(CreateMessage(viewModel, currentIndex, type.Value, parts, internalId: null));
                    currentIndex++;
                }
            }
        }

        if (viewModel.Items.Count > 0)
        {
            return;
        }

        // Final fallback: attempt to parse LangSmith OpenTelemetry genai standard attributes.
        // LangSmith uses a flattened format with indexed attributes like gen_ai.prompt.0.role, gen_ai.prompt.0.content, etc.
        ParseLangSmithFormat(viewModel, ref currentIndex);
    }

    private static int ParseMessages(GenAIVisualizerDialogViewModel viewModel, string messages, string description, bool isOutput, ref int currentIndex)
    {
        var inputParts = DeserializeWithErrorHandling(description, messages, GenAIMessagesContext.Default.ListChatMessage)!;
        foreach (var msg in inputParts)
        {
            var parts = msg.Parts.Select(GenAIItemPartViewModel.CreateMessagePart).ToList();
            var type = msg.Role switch
            {
                "system" => GenAIItemType.SystemMessage,
                "user" => msg.Parts.All(p => p is ToolCallResponsePart) ? GenAIItemType.ToolMessage : GenAIItemType.UserMessage,
                "assistant" => isOutput ? GenAIItemType.OutputMessage : GenAIItemType.AssistantMessage,
                _ => GenAIItemType.UserMessage
            };
            viewModel.Items.Add(CreateMessage(viewModel, currentIndex, type, parts, internalId: null));
            currentIndex++;
        }

        return currentIndex;
    }

    // Parse LangSmith OpenTelemetry genai standard attributes format.
    // LangSmith uses a flattened format with indexed attributes:
    // gen_ai.prompt.0.role, gen_ai.prompt.0.content, gen_ai.prompt.1.role, etc.
    // gen_ai.completion.0.role, gen_ai.completion.0.content, etc.
    private static void ParseLangSmithFormat(GenAIVisualizerDialogViewModel viewModel, ref int currentIndex)
    {
        var attributes = viewModel.Span.Attributes;

        // Group attributes by prefix (prompt or completion) and index
        var promptMessages = ExtractIndexedMessages(attributes, GenAIHelpers.GenAIPromptPrefix);
        var completionMessages = ExtractIndexedMessages(attributes, GenAIHelpers.GenAICompletionPrefix);

        // Parse prompt messages (inputs)
        foreach (var (index, message) in promptMessages.OrderBy(kvp => kvp.Key))
        {
            var role = GetMessageRole(message, defaultRole: "user");
            var content = GetMessageContent(message);

            if (!string.IsNullOrEmpty(content))
            {
                var parts = new List<GenAIItemPartViewModel>
                {
                    GenAIItemPartViewModel.CreateMessagePart(new TextPart { Content = content })
                };

                var type = role switch
                {
                    "system" => GenAIItemType.SystemMessage,
                    "user" => GenAIItemType.UserMessage,
                    "assistant" => GenAIItemType.AssistantMessage,
                    "tool" => GenAIItemType.ToolMessage,
                    _ => GenAIItemType.UserMessage
                };

                viewModel.Items.Add(CreateMessage(viewModel, currentIndex, type, parts, internalId: null));
                currentIndex++;
            }
        }

        // Parse completion messages (outputs)
        foreach (var (index, message) in completionMessages.OrderBy(kvp => kvp.Key))
        {
            var role = GetMessageRole(message, defaultRole: "assistant");
            var content = GetMessageContent(message);

            if (!string.IsNullOrEmpty(content))
            {
                var parts = new List<GenAIItemPartViewModel>
                {
                    GenAIItemPartViewModel.CreateMessagePart(new TextPart { Content = content })
                };

                viewModel.Items.Add(CreateMessage(viewModel, currentIndex, GenAIItemType.OutputMessage, parts, internalId: null));
                currentIndex++;
            }
        }

        // Extract role from message dictionary with fallback to message.role and default
        static string GetMessageRole(Dictionary<string, string> message, string defaultRole)
        {
            return message.TryGetValue("role", out var r) ? r : message.GetValueOrDefault("message.role", defaultRole);
        }

        // Extract content from message dictionary with fallback to message.content
        static string? GetMessageContent(Dictionary<string, string> message)
        {
            return message.TryGetValue("content", out var c) ? c : message.GetValueOrDefault("message.content");
        }
    }

    // Extract messages from indexed span attributes like gen_ai.prompt.0.role, gen_ai.prompt.0.content
    private static Dictionary<int, Dictionary<string, string>> ExtractIndexedMessages(KeyValuePair<string, string>[] attributes, string prefix)
    {
        var messages = new Dictionary<int, Dictionary<string, string>>();

        foreach (var attr in attributes)
        {
            if (attr.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                // Extract index and field name from attribute key
                // Format: gen_ai.prompt.{index}.{field}
                var remainder = attr.Key.AsSpan(prefix.Length);
                var dotIndex = remainder.IndexOf('.');

                if (dotIndex > 0 && int.TryParse(remainder.Slice(0, dotIndex), out var messageIndex))
                {
                    var fieldName = remainder.Slice(dotIndex + 1).ToString();

                    if (!messages.TryGetValue(messageIndex, out var message))
                    {
                        message = new Dictionary<string, string>();
                        messages[messageIndex] = message;
                    }

                    message[fieldName] = attr.Value;
                }
            }
        }

        return messages;
    }

    private static GenAIItemViewModel CreateMessage(GenAIVisualizerDialogViewModel viewModel, int currentIndex, GenAIItemType type, List<GenAIItemPartViewModel> parts, long? internalId)
    {
        return new GenAIItemViewModel
        {
            Index = currentIndex,
            InternalId = internalId,
            Type = type,
            Parent = viewModel.Span,
            ResourceName = type is GenAIItemType.AssistantMessage or GenAIItemType.OutputMessage ? viewModel.PeerName! : viewModel.SourceName!,
            ItemParts = parts
        };
    }

    private static List<GenAIItemPartViewModel> DeserializeEventContent(int index, GenAIItemType type, string message)
    {
        var messagePartViewModels = new List<GenAIItemPartViewModel>();

        switch (type)
        {
            case GenAIItemType.SystemMessage:
            case GenAIItemType.UserMessage:
                var systemOrUserEvent = DeserializeEventJson(message, GenAIEventsContext.Default.SystemOrUserEvent)!;
                messagePartViewModels.Add(GenAIItemPartViewModel.CreateMessagePart(new TextPart { Content = systemOrUserEvent.Content }));
                break;
            case GenAIItemType.AssistantMessage:
                var assistantEvent = DeserializeEventJson(message, GenAIEventsContext.Default.AssistantEvent)!;
                ProcessAssistantEvent(messagePartViewModels, assistantEvent);
                break;
            case GenAIItemType.ToolMessage:
                var toolEvent = DeserializeEventJson(message, GenAIEventsContext.Default.ToolEvent)!;
                var toolResponse = ProcessJsonPayload(toolEvent.Content);
                messagePartViewModels.Add(GenAIItemPartViewModel.CreateMessagePart(new ToolCallResponsePart { Id = toolEvent.Id, Response = toolResponse }));
                break;
            case GenAIItemType.OutputMessage:
                var choiceEvent = DeserializeEventJson(message, GenAIEventsContext.Default.ChoiceEvent)!;
                if (choiceEvent.Message is { } m)
                {
                    ProcessAssistantEvent(messagePartViewModels, m);
                }
                break;
            default:
                throw new InvalidOperationException($"Unexpected type: {type}");
        }

        return messagePartViewModels;

        TValue DeserializeEventJson<TValue>(string json, JsonTypeInfo<TValue> jsonTypeInfo)
        {
            return DeserializeWithErrorHandling($"{type} event content for message #{index}", json, jsonTypeInfo);
        }

        static void ProcessAssistantEvent(List<GenAIItemPartViewModel> messagePartViewModels, AssistantEvent assistantEvent)
        {
            if (assistantEvent.Content != null)
            {
                messagePartViewModels.Add(GenAIItemPartViewModel.CreateMessagePart(new TextPart { Content = assistantEvent.Content }));
            }
            if (assistantEvent?.ToolCalls?.Length > 0)
            {
                for (var i = 0; i < assistantEvent.ToolCalls.Length; i++)
                {
                    var function = assistantEvent.ToolCalls[i].Function;
                    if (function == null)
                    {
                        continue;
                    }

                    var args = ProcessJsonPayload(function.Arguments);
                    messagePartViewModels.Add(GenAIItemPartViewModel.CreateMessagePart(new ToolCallRequestPart { Name = function.Name, Arguments = args }));
                }
            }
        }
    }

    private static TValue DeserializeWithErrorHandling<TValue>(string description, string json, JsonTypeInfo<TValue> jsonTypeInfo)
    {
        try
        {
            return JsonSerializer.Deserialize<TValue>(json, jsonTypeInfo)!;
        }
        catch (Exception ex)
        {
            // Don't include JSON in exception message because it could contain sensitive data.
            throw new InvalidOperationException(
                $"""
                Error deserializing GenAI message content.
                Error description: {ex.GetType().FullName}: {ex.Message}
                Content description: {description}
                """, ex);
        }
    }

    // Args might be a serialized object string instead of a raw object.
    // To avoid extra escaping in displaying serialized object string, attempt to convert to object.
    private static JsonNode? ProcessJsonPayload(JsonNode? args)
    {
        if (args?.GetValueKind() == JsonValueKind.String && args.GetValue<string>() is { } argsJson)
        {
            try
            {
                var node = JsonNode.Parse(argsJson);
                if (node?.GetValueKind() is JsonValueKind.Object or JsonValueKind.Array)
                {
                    args = node;
                }
            }
            catch (Exception)
            {
                // Not a JSON string. Ignore.
            }
        }

        return args;
    }

    private static OpenApiSchema? ParseOpenApiSchema(JsonObject schemaObj)
    {
        var schema = new OpenApiSchema
        {
            Type = schemaObj["type"]?.GetValue<string>(),
            Description = schemaObj["description"]?.GetValue<string>()
        };

        // Parse properties
        if (schemaObj["properties"] is JsonObject propsObj)
        {
            schema.Properties = new Dictionary<string, OpenApiSchema>();
            foreach (var prop in propsObj)
            {
                if (prop.Value is JsonObject propSchemaObj)
                {
                    schema.Properties[prop.Key] = ParseOpenApiSchema(propSchemaObj);
                }
            }
        }

        // Parse required
        if (schemaObj["required"] is JsonArray requiredArray)
        {
            schema.Required = new HashSet<string>();
            foreach (var item in requiredArray)
            {
                if (item != null)
                {
                    schema.Required.Add(item.GetValue<string>());
                }
            }
        }

        // Parse enum
        if (schemaObj["enum"] is JsonArray enumArray)
        {
            schema.Enum = new List<IOpenApiAny>();
            foreach (var item in enumArray)
            {
                if (item != null)
                {
                    schema.Enum.Add(new OpenApiString(item.GetValue<string>()));
                }
            }
        }

        return schema;
    }

    private static bool TryMapEventName(string name, [NotNullWhen(true)] out GenAIItemType? type)
    {
        type = name switch
        {
            "gen_ai.system.message" => GenAIItemType.SystemMessage,
            "gen_ai.user.message" => GenAIItemType.UserMessage,
            "gen_ai.assistant.message" => GenAIItemType.AssistantMessage,
            "gen_ai.tool.message" => GenAIItemType.ToolMessage,
            "gen_ai.choice" => GenAIItemType.OutputMessage,
            _ => null
        };

        return type != null;
    }

    private static List<OtlpLogEntry> GetSpanLogEntries(TelemetryRepository telemetryRepository, OtlpSpan span)
    {
        var logsContext = new GetLogsContext
        {
            ResourceKey = null,
            Count = int.MaxValue,
            StartIndex = 0,
            Filters = [
                new FieldTelemetryFilter
                {
                    Field = KnownStructuredLogFields.SpanIdField,
                    Condition = FilterCondition.Equals,
                    Value = span.SpanId
                }
            ]
        };
        var logsResult = telemetryRepository.GetLogs(logsContext);
        return logsResult.Items;
    }

    private static void ParseEvaluations(GenAIVisualizerDialogViewModel viewModel, TelemetryRepository telemetryRepository)
    {
        var evaluations = new List<EvaluationResultViewModel>();

        // Parse evaluation results from log entries
        var logEntries = GetSpanLogEntries(telemetryRepository, viewModel.Span);
        foreach (var logEntry in logEntries)
        {
            if (OtlpHelpers.GetEventName(logEntry) == GenAIHelpers.GenAIEvaluationResultEventName)
            {
                var evaluation = ParseEvaluationFromAttributes(logEntry.Attributes);
                if (evaluation != null)
                {
                    evaluations.Add(evaluation);
                }
            }
        }

        // Parse evaluation results from span events
        foreach (var spanEvent in viewModel.Span.Events)
        {
            if (spanEvent.Name == GenAIHelpers.GenAIEvaluationResultEventName)
            {
                var evaluation = ParseEvaluationFromAttributes(spanEvent.Attributes);
                if (evaluation != null)
                {
                    evaluations.Add(evaluation);
                }
            }
        }

        viewModel.Evaluations = evaluations;
    }

    private static EvaluationResultViewModel? ParseEvaluationFromAttributes(KeyValuePair<string, string>[] eventAttributes)
    {
        // Parse evaluation fields from attributes per OpenTelemetry specification
        var name = eventAttributes.GetValue(GenAIHelpers.GenAIEvaluationName);
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        return new EvaluationResultViewModel
        {
            Name = name,
            ScoreLabel = eventAttributes.GetValue(GenAIHelpers.GenAIEvaluationScoreLabel),
            ScoreValue = ParseDouble(eventAttributes.GetValue(GenAIHelpers.GenAIEvaluationScoreValue)),
            Explanation = eventAttributes.GetValue(GenAIHelpers.GenAIEvaluationExplanation),
            ResponseId = eventAttributes.GetValue(GenAIHelpers.GenAIResponseId),
            ErrorType = eventAttributes.GetValue(GenAIHelpers.ErrorType)
        };
    }

    private static double? ParseDouble(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (double.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        return null;
    }
}

public enum OverviewViewKind
{
    InputOutput,
    Details,
    Tools,
    Evaluations
}

public enum ItemViewKind
{
    Preview,
    Raw,
    Toolcalls
}

public record BadgeDetail(string Text, string Class, Icon Icon);
