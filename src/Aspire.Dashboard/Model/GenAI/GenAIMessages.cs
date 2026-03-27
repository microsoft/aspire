// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.OpenApi;

namespace Aspire.Dashboard.Model.GenAI;

/// <summary>
/// Represents text, tool calls, data parts, and generic parts.
/// </summary>
[JsonConverter(typeof(MessagePartConverter))]
public abstract class MessagePart
{
    public const string TextType = "text";
    public const string ToolCallType = "tool_call";
    public const string ToolCallResponseType = "tool_call_response";
    public const string BlobType = "blob";
    public const string FileType = "file";
    public const string UriType = "uri";
    public const string ReasoningType = "reasoning";
    public const string ServerToolCallType = "server_tool_call";
    public const string ServerToolCallResponseType = "server_tool_call_response";

    public string Type { get; set; } = default!;
}

/// <summary>
/// Represents text content sent to or received from the model.
/// </summary>
public class TextPart : MessagePart
{
    public TextPart()
    {
        Type = TextType;
    }

    public string? Content { get; set; } = default!;
}

/// <summary>
/// Represents a tool call requested by the model.
/// </summary>
public class ToolCallRequestPart : MessagePart
{
    public ToolCallRequestPart()
    {
        Type = ToolCallType;
    }

    public string? Id { get; set; }
    public string? Name { get; set; } = default!;
    public JsonNode? Arguments { get; set; }
}

/// <summary>
/// Represents a tool call result sent to the model.
/// </summary>
public class ToolCallResponsePart : MessagePart
{
    public ToolCallResponsePart()
    {
        Type = ToolCallResponseType;
    }

    public string? Id { get; set; }
    public JsonNode? Response { get; set; } = default!;
}

/// <summary>
/// Represents blob binary data sent inline to the model.
/// </summary>
public class BlobPart : MessagePart
{
    public BlobPart()
    {
        Type = BlobType;
    }

    public string? MimeType { get; set; }
    public string? Modality { get; set; }
    public string? Content { get; set; }
}

/// <summary>
/// Represents an external referenced file sent to the model by file ID.
/// </summary>
public class FilePart : MessagePart
{
    public FilePart()
    {
        Type = FileType;
    }

    public string? MimeType { get; set; }
    public string? Modality { get; set; }
    public string? FileId { get; set; }
}

/// <summary>
/// Represents an external referenced file sent to the model by URI.
/// </summary>
public class UriPart : MessagePart
{
    public UriPart()
    {
        Type = UriType;
    }

    public string? MimeType { get; set; }
    public string? Modality { get; set; }
    public string? Uri { get; set; }
}

/// <summary>
/// Represents reasoning/thinking content received from the model.
/// </summary>
public class ReasoningPart : MessagePart
{
    public ReasoningPart()
    {
        Type = ReasoningType;
    }

    public string? Content { get; set; }
}

/// <summary>
/// Represents a server-side tool call invocation.
/// </summary>
public class ServerToolCallPart : MessagePart
{
    public ServerToolCallPart()
    {
        Type = ServerToolCallType;
    }

    public string? Id { get; set; }
    public string? Name { get; set; }
    public JsonNode? ServerToolCall { get; set; }
}

/// <summary>
/// Represents a server-side tool call response.
/// </summary>
public class ServerToolCallResponsePart : MessagePart
{
    public ServerToolCallResponsePart()
    {
        Type = ServerToolCallResponseType;
    }

    public string? Id { get; set; }
    public JsonNode? ServerToolCallResponse { get; set; }
}

/// <summary>
/// Represents an arbitrary message part with any type and properties.
/// </summary>
public class GenericPart : MessagePart
{
    // Extensible dynamic properties
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

/// <summary>
/// Represents a message part that failed to deserialize as its expected type.
/// This is not part of the GenAI standard. It exists to handle unexpected errors when reading JSON,
/// allowing the remaining data to still be displayed.
/// </summary>
public class UnexpectedErrorPart : GenericPart
{
    [JsonIgnore]
    public Exception? Error { get; set; }
}

/// <summary>
/// A chat message containing a role and parts.
/// </summary>
public class ChatMessage
{
    public string Role { get; set; } = default!;
    public List<MessagePart> Parts { get; set; } = new();
    // Only set on output message.
    public string? FinishReason { get; set; }
}

/// <summary>
/// Represents a tool definition that can be used by the model.
/// </summary>
[DebuggerDisplay("Type = {Type}, Name = {Name}")]
public class ToolDefinition
{
    public string Type { get; set; } = "function";
    public string? Name { get; set; }
    public string? Description { get; set; }
    public OpenApiSchema? Parameters { get; set; }
}

/// <summary>
/// Handles polymorphic serialization and deserialization of <see cref="MessagePart"/> types.
/// </summary>
internal sealed class MessagePartConverter : JsonConverter<MessagePart>
{
    public override MessagePart? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        string? type = null;
        try
        {
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
            {
                throw new JsonException("Missing 'type' property.");
            }

            type = typeProp.GetString();

            return type switch
            {
                MessagePart.TextType => doc.RootElement.Deserialize<TextPart>(options),
                MessagePart.ToolCallType => TryParseStringArguments(doc.RootElement.Deserialize<ToolCallRequestPart>(options)),
                MessagePart.ToolCallResponseType => doc.RootElement.Deserialize<ToolCallResponsePart>(options),
                MessagePart.BlobType => doc.RootElement.Deserialize<BlobPart>(options),
                MessagePart.FileType => doc.RootElement.Deserialize<FilePart>(options),
                MessagePart.UriType => doc.RootElement.Deserialize<UriPart>(options),
                MessagePart.ReasoningType => doc.RootElement.Deserialize<ReasoningPart>(options),
                MessagePart.ServerToolCallType => TryParseServerToolCallArguments(doc.RootElement.Deserialize<ServerToolCallPart>(options)),
                MessagePart.ServerToolCallResponseType => doc.RootElement.Deserialize<ServerToolCallResponsePart>(options),
                _ => doc.RootElement.Deserialize<GenericPart>(options),
            };
        }
        catch (JsonException ex)
        {
            var wrappedEx = new InvalidOperationException($"Error reading message part '{type ?? "(missing)"}': {ex.Message}", ex);

            UnexpectedErrorPart? errorPart = null;
            try
            {
                errorPart = doc.RootElement.Deserialize<UnexpectedErrorPart>(options);
            }
            catch
            {
                // Ignore. We rethrow the original exception.
            }

            if (errorPart is null)
            {
                throw wrappedEx;
            }

            errorPart.Error = wrappedEx;
            return errorPart;
        }
    }

    public override void Write(Utf8JsonWriter writer, MessagePart value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
    }

    /// <summary>
    /// If the tool call arguments are a string, try parsing them as JSON and replace with the parsed object.
    /// </summary>
    private static ToolCallRequestPart? TryParseStringArguments(ToolCallRequestPart? part)
    {
        if (part is { Arguments: not null })
        {
            part.Arguments = GenAIMessageParsingHelper.TryParseStringJsonNode(part.Arguments);
        }

        return part;
    }

    private static ServerToolCallPart? TryParseServerToolCallArguments(ServerToolCallPart? part)
    {
        if (part is { ServerToolCall: not null })
        {
            part.ServerToolCall = GenAIMessageParsingHelper.TryParseStringJsonNode(part.ServerToolCall);
        }

        return part;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(MessagePart))]
[JsonSerializable(typeof(TextPart))]
[JsonSerializable(typeof(ToolCallRequestPart))]
[JsonSerializable(typeof(ToolCallResponsePart))]
[JsonSerializable(typeof(BlobPart))]
[JsonSerializable(typeof(FilePart))]
[JsonSerializable(typeof(UriPart))]
[JsonSerializable(typeof(ReasoningPart))]
[JsonSerializable(typeof(ServerToolCallPart))]
[JsonSerializable(typeof(ServerToolCallResponsePart))]
[JsonSerializable(typeof(GenericPart))]
[JsonSerializable(typeof(UnexpectedErrorPart))]
[JsonSerializable(typeof(ChatMessage))]
[JsonSerializable(typeof(List<ChatMessage>))]
public sealed partial class GenAIMessagesContext : JsonSerializerContext;
