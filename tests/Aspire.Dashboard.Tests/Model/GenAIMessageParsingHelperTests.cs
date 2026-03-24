// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Dashboard.Model.GenAI;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class GenAIMessageParsingHelperTests
{
    [Theory]
    [InlineData("""[{"type":"text","content":"Hello"},{"type":"text","content":"World"}]""", false, 2)]
    [InlineData("""[]""", false, 0)]
    [InlineData("""[{"type":"text","content":"Hello"},{"type":"text","con""", true, 1)]
    [InlineData("""[{"type":"text","con""", true, 0)]
    [InlineData("""[""", true, 0)]
    [InlineData("""[{"type":"text","content":"Hello"},""", true, 1)]
    [InlineData("""[{"type":"text","content":"Hello Wor""", true, 0)]
    [InlineData("""[{"type":"text","content":"A"},{"type":"text","content":"B"},{"type":"text","content":"C is ver""", true, 2)]
    public void DeserializeArrayIncrementally_ReturnsExpectedTruncationAndCount(string json, bool expectedTruncated, int expectedCount)
    {
        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.Equal(expectedTruncated, truncated);
        Assert.Equal(expectedCount, items.Count);
    }

    [Fact]
    public void DeserializeArrayIncrementally_InvalidJsonNotArray_ThrowsJsonException()
    {
        var json = """{"type":"text"}""";

        Assert.Throws<JsonException>(() =>
            GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart));
    }

    [Fact]
    public void ReadChatMessage_TruncatedParts_ReturnsPartsTruncatedTrue()
    {
        var json = """[{"role":"system","parts":[{"type":"text","content":"OK"}]},{"role":"user","parts":[{"type":"text","content":"Hello"},{"type":"text","con""";

        var (chatMessages, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadChatMessage);

        Assert.True(truncated);
        Assert.Single(chatMessages);
        var (role, parts, partsTruncated) = chatMessages[0];
        Assert.Equal("system", role);
        Assert.False(partsTruncated);
        Assert.Single(parts);
    }

    [Fact]
    public void ReadChatMessage_CompleteParts_ReturnsPartsTruncatedFalse()
    {
        var json = """[{"role":"assistant","parts":[{"type":"text","content":"Hi there"}]}]""";

        var (chatMessages, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadChatMessage);

        Assert.False(truncated);
        Assert.Single(chatMessages);
        var (role, parts, partsTruncated) = chatMessages[0];
        Assert.Equal("assistant", role);
        Assert.False(partsTruncated);
        var textPart = Assert.IsType<TextPart>(Assert.Single(parts));
        Assert.Equal("Hi there", textPart.Content);
    }

    [Theory]
    [InlineData("""[{"role""", 0)]
    [InlineData("""[{"role":"user","parts":[{"type":"text","content":"OK"}]},{"role":"assistant","finish_reason":"sto""", 1)]
    public void ReadChatMessage_TruncatedJson_ReturnsTruncated(string json, int expectedMessageCount)
    {
        var (chatMessages, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadChatMessage);

        Assert.True(truncated);
        Assert.Equal(expectedMessageCount, chatMessages.Count);
    }

    [Theory]
    [InlineData("""[{"role":"assistant","finish_reason":"stop","model":"gpt-4","parts":[{"type":"text","content":"Done"}]}]""")]
    [InlineData("""[{"role":"user","metadata":{"key":"value","nested":{"a":1}},"parts":[{"type":"text","content":"Hi"}]}]""")]
    public void ReadChatMessage_WithUnknownProperties_SkipsThemSuccessfully(string json)
    {
        var (chatMessages, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadChatMessage);

        Assert.False(truncated);
        Assert.Single(chatMessages);
        Assert.Single(chatMessages[0].parts);
    }

    [Fact]
    public void ReadChatMessage_MultipleChatMessages_ParsesAll()
    {
        var json = """
            [
                {"role":"system","parts":[{"type":"text","content":"You are helpful."}]},
                {"role":"user","parts":[{"type":"text","content":"Hello"}]},
                {"role":"assistant","parts":[{"type":"text","content":"Hi!"}]}
            ]
            """;

        var (chatMessages, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadChatMessage);

        Assert.False(truncated);
        Assert.Equal(3, chatMessages.Count);
        Assert.Equal("system", chatMessages[0].role);
        Assert.Equal("user", chatMessages[1].role);
        Assert.Equal("assistant", chatMessages[2].role);
    }

    [Fact]
    public void ReadMessagePart_TextPart_ParsesStringAndNullContent()
    {
        var json = """[{"type":"text","content":"simple string"},{"type":"text","content":null}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        Assert.Equal(2, items.Count);
        Assert.Equal("simple string", Assert.IsType<TextPart>(items[0]).Content);
        Assert.Null(Assert.IsType<TextPart>(items[1]).Content);
    }

    [Fact]
    public void ReadMessagePart_ToolCallRequestPart_ParsesAllProperties()
    {
        var json = """[{"type":"tool_call","id":"call_abc","name":"get_weather","arguments":{"location":"Seattle","unit":"celsius"}}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        var toolCallPart = Assert.IsType<ToolCallRequestPart>(Assert.Single(items));
        Assert.Equal("tool_call", toolCallPart.Type);
        Assert.Equal("call_abc", toolCallPart.Id);
        Assert.Equal("get_weather", toolCallPart.Name);
        Assert.NotNull(toolCallPart.Arguments);
        Assert.Equal("Seattle", toolCallPart.Arguments["location"]!.GetValue<string>());
    }

    [Fact]
    public void ReadMessagePart_ToolCallRequestPart_ParsesStringArgumentsAsJson()
    {
        var json = """[{"type":"tool_call","id":"call_abc","name":"get_weather","arguments":"{\"location\":\"Seattle\",\"unit\":\"celsius\"}"}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        var toolCallPart = Assert.IsType<ToolCallRequestPart>(Assert.Single(items));
        Assert.Equal("get_weather", toolCallPart.Name);
        Assert.NotNull(toolCallPart.Arguments);
        Assert.Equal(JsonValueKind.Object, toolCallPart.Arguments.GetValueKind());
        Assert.Equal("Seattle", toolCallPart.Arguments["location"]!.GetValue<string>());
    }

    [Fact]
    public void ReadMessagePart_ToolCallResponsePart_ParsesObjectAndStringResponses()
    {
        var json = """[{"type":"tool_call_response","id":"call_abc","response":{"temperature":72}},{"type":"tool_call_response","id":"call_xyz","response":"plain text result"}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        Assert.Equal(2, items.Count);

        var objectResponse = Assert.IsType<ToolCallResponsePart>(items[0]);
        Assert.Equal("call_abc", objectResponse.Id);
        Assert.Equal(72, objectResponse.Response!["temperature"]!.GetValue<int>());

        var stringResponse = Assert.IsType<ToolCallResponsePart>(items[1]);
        Assert.Equal("call_xyz", stringResponse.Id);
        Assert.Equal("plain text result", stringResponse.Response!.GetValue<string>());
    }

    [Fact]
    public void ReadMessagePart_BlobPart_ParsesAllProperties()
    {
        var json = """[{"type":"blob","mime_type":"image/png","modality":"image","content":"iVBORw0KGgo="}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        var blobPart = Assert.IsType<BlobPart>(Assert.Single(items));
        Assert.Equal("blob", blobPart.Type);
        Assert.Equal("image/png", blobPart.MimeType);
        Assert.Equal("image", blobPart.Modality);
        Assert.Equal("iVBORw0KGgo=", blobPart.Content);
    }

    [Fact]
    public void ReadMessagePart_FilePart_ParsesAllProperties()
    {
        var json = """[{"type":"file","mime_type":"application/pdf","modality":"image","file_id":"file-abc123"}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        var filePart = Assert.IsType<FilePart>(Assert.Single(items));
        Assert.Equal("file", filePart.Type);
        Assert.Equal("application/pdf", filePart.MimeType);
        Assert.Equal("image", filePart.Modality);
        Assert.Equal("file-abc123", filePart.FileId);
    }

    [Fact]
    public void ReadMessagePart_UriPart_ParsesAllProperties()
    {
        var json = """[{"type":"uri","mime_type":"image/jpeg","modality":"image","uri":"https://example.com/photo.jpg"}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        var uriPart = Assert.IsType<UriPart>(Assert.Single(items));
        Assert.Equal("uri", uriPart.Type);
        Assert.Equal("image/jpeg", uriPart.MimeType);
        Assert.Equal("image", uriPart.Modality);
        Assert.Equal("https://example.com/photo.jpg", uriPart.Uri);
    }

    [Fact]
    public void ReadMessagePart_ReasoningPart_ParsesContent()
    {
        var json = """[{"type":"reasoning","content":"Let me think about this step by step..."}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        var reasoningPart = Assert.IsType<ReasoningPart>(Assert.Single(items));
        Assert.Equal("reasoning", reasoningPart.Type);
        Assert.Equal("Let me think about this step by step...", reasoningPart.Content);
    }

    [Fact]
    public void ReadMessagePart_ServerToolCallPart_ParsesAllProperties()
    {
        var json = """[{"type":"server_tool_call","id":"stc_1","name":"web_search","server_tool_call":{"type":"web_search","query":"latest news"}}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        var serverToolCallPart = Assert.IsType<ServerToolCallPart>(Assert.Single(items));
        Assert.Equal("server_tool_call", serverToolCallPart.Type);
        Assert.Equal("stc_1", serverToolCallPart.Id);
        Assert.Equal("web_search", serverToolCallPart.Name);
        Assert.NotNull(serverToolCallPart.ServerToolCall);
        Assert.Equal("latest news", serverToolCallPart.ServerToolCall["query"]!.GetValue<string>());
    }

    [Fact]
    public void ReadMessagePart_ServerToolCallResponsePart_ParsesAllProperties()
    {
        var json = """[{"type":"server_tool_call_response","id":"stc_1","server_tool_call_response":{"type":"web_search","results":["result1","result2"]}}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        var serverToolCallResponsePart = Assert.IsType<ServerToolCallResponsePart>(Assert.Single(items));
        Assert.Equal("server_tool_call_response", serverToolCallResponsePart.Type);
        Assert.Equal("stc_1", serverToolCallResponsePart.Id);
        Assert.NotNull(serverToolCallResponsePart.ServerToolCallResponse);
        Assert.Equal("web_search", serverToolCallResponsePart.ServerToolCallResponse["type"]!.GetValue<string>());
    }

    [Fact]
    public void ReadMessagePart_GenericPart_ParsesUnknownTypes()
    {
        var json = """[{"type":"custom_widget","widget_id":"w1","color":"blue","count":42},{"type":"unknown"}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        Assert.Equal(2, items.Count);

        var richGeneric = Assert.IsType<GenericPart>(items[0]);
        Assert.Equal("custom_widget", richGeneric.Type);
        Assert.NotNull(richGeneric.AdditionalProperties);
        Assert.Equal("w1", richGeneric.AdditionalProperties["widget_id"].GetString());
        Assert.Equal("blue", richGeneric.AdditionalProperties["color"].GetString());
        Assert.Equal(42, richGeneric.AdditionalProperties["count"].GetInt32());

        var bareGeneric = Assert.IsType<GenericPart>(items[1]);
        Assert.Equal("unknown", bareGeneric.Type);
    }

    [Fact]
    public void ReadMessagePart_MixedPartTypes_ParsesCorrectly()
    {
        var json = """
            [
                {"type":"text","content":"Hello"},
                {"type":"reasoning","content":"Thinking..."},
                {"type":"blob","mime_type":"image/png","modality":"image","content":"base64data"},
                {"type":"uri","mime_type":"image/jpeg","modality":"image","uri":"https://example.com/img.jpg"},
                {"type":"file","mime_type":"application/pdf","modality":"image","file_id":"f1"},
                {"type":"server_tool_call","id":"s1","name":"code_interpreter","server_tool_call":{"type":"code_interpreter"}},
                {"type":"server_tool_call_response","id":"s1","server_tool_call_response":{"type":"code_interpreter","output":"42"}}
            ]
            """;

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        Assert.Equal(7, items.Count);

        Assert.Collection(items,
            part => Assert.IsType<TextPart>(part),
            part => Assert.IsType<ReasoningPart>(part),
            part => Assert.IsType<BlobPart>(part),
            part => Assert.IsType<UriPart>(part),
            part => Assert.IsType<FilePart>(part),
            part => Assert.IsType<ServerToolCallPart>(part),
            part => Assert.IsType<ServerToolCallResponsePart>(part));
    }

    [Fact]
    public void ReadMessagePart_MissingTypeProperty_FallsBackToUnexpectedErrorPart()
    {
        var json = """[{"content":"no type here"}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        Assert.Collection(items,
            part =>
            {
                var errorPart = Assert.IsType<UnexpectedErrorPart>(part);
                Assert.NotNull(errorPart.Error);
                Assert.Contains("Missing 'type' property", errorPart.Error.Message);
                Assert.NotNull(errorPart.AdditionalProperties);
                Assert.True(errorPart.AdditionalProperties.ContainsKey("content"));
            });
    }

    [Fact]
    public void ReadMessagePart_KnownTypeDeserializationFails_MultipleParts_FallsBackToUnexpectedErrorPart()
    {
        // Mix of a valid text part and an invalid one that should fall back to UnexpectedErrorPart.
        var json = """[{"type":"text","content":"hello"},{"type":"text","content":["array","of","values"]},{"type":"text","content":"hello"}]""";

        var (items, truncated) = GenAIMessageParsingHelper.DeserializeArrayIncrementally(json, GenAIMessageParsingHelper.ReadMessagePart);

        Assert.False(truncated);
        Assert.Collection(items,
            part =>
            {
                var textPart = Assert.IsType<TextPart>(part);
                Assert.Equal("hello", textPart.Content);
            },
            part =>
            {
                var errorPart = Assert.IsType<UnexpectedErrorPart>(part);
                Assert.Equal("text", errorPart.Type);
                Assert.NotNull(errorPart.Error);
                Assert.NotNull(errorPart.AdditionalProperties);
                Assert.Equal(JsonValueKind.Array, errorPart.AdditionalProperties["content"].ValueKind);
            },
            part =>
            {
                var textPart = Assert.IsType<TextPart>(part);
                Assert.Equal("hello", textPart.Content);
            });
    }
}
