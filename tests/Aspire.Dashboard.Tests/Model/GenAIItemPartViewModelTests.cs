// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Dashboard.Model.GenAI;
using Aspire.Dashboard.Utils;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public sealed class GenAIItemPartViewModelTests
{
    [Fact]
    public void CreateMessagePart_ToolCallResponse_StringResponseDecoded()
    {
        // Arrange
        var responsePart = new ToolCallResponsePart
        {
            Response = JsonValue.Create("**Markdown**")
        };

        // Act
        var itemPart = GenAIItemPartViewModel.CreateMessagePart(responsePart);

        // Assert
        Assert.Equal("**Markdown**", itemPart.TextVisualizerViewModel.Text);
        Assert.Equal(DashboardUIHelpers.MarkdownFormat, itemPart.TextVisualizerViewModel.FormatKind);
        Assert.Equal("**Markdown**", itemPart.TextVisualizerViewModel.FormattedText);
    }

    [Fact]
    public void CreateMessagePart_ToolCallResponse_StringResponseContainingJsonFormatted()
    {
        // Arrange
        const string expectedText = """{"answer":42,"unit":"C"}""";
        var responsePart = new ToolCallResponsePart
        {
            Response = JsonValue.Create(expectedText)
        };

        // Act
        var itemPart = GenAIItemPartViewModel.CreateMessagePart(responsePart);

        // Assert
        Assert.Equal(expectedText, itemPart.TextVisualizerViewModel.Text);
        Assert.Equal(DashboardUIHelpers.JsonFormat, itemPart.TextVisualizerViewModel.FormatKind);
        Assert.Equal(
            """
            {
              "answer": 42,
              "unit": "C"
            }
            """,
            itemPart.TextVisualizerViewModel.FormattedText);
    }

    [Fact]
    public void CreateMessagePart_ToolCallResponse_ArrayResponse_SerializedAsJson()
    {
        // Arrange
        var responsePart = new ToolCallResponsePart
        {
            Response = JsonNode.Parse("""["Jack","Jane"]""")
        };

        // Act
        var itemPart = GenAIItemPartViewModel.CreateMessagePart(responsePart);

        // Assert
        Assert.Equal(
            """
            [
              "Jack",
              "Jane"
            ]
            """,
            itemPart.TextVisualizerViewModel.Text);
        Assert.Equal(DashboardUIHelpers.JsonFormat, itemPart.TextVisualizerViewModel.FormatKind);
        Assert.Equal(
            """
            [
              "Jack",
              "Jane"
            ]
            """,
            itemPart.TextVisualizerViewModel.FormattedText);
    }

    [Fact]
    public void CreateMessagePart_ToolCallResponse_ObjectResponse_SerializedAsJson()
    {
        // Arrange
        var responsePart = new ToolCallResponsePart
        {
            Response = JsonNode.Parse("""{"name":"Jack","age":30}""")
        };

        // Act
        var itemPart = GenAIItemPartViewModel.CreateMessagePart(responsePart);

        // Assert
        Assert.Equal(
            """
            {
              "name": "Jack",
              "age": 30
            }
            """,
            itemPart.TextVisualizerViewModel.Text);
        Assert.Equal(DashboardUIHelpers.JsonFormat, itemPart.TextVisualizerViewModel.FormatKind);
    }

    [Fact]
    public void CreateMessagePart_ToolCallRequest_NonAsciiArguments_PreservesCharacters()
    {
        // Arrange
        var requestPart = new ToolCallRequestPart
        {
            Name = "get_weather",
            Arguments = JsonNode.Parse("""{"location":"東京","unit":"celsius"}""")
        };

        // Act
        var itemPart = GenAIItemPartViewModel.CreateMessagePart(requestPart);

        // Assert
        Assert.Contains("東京", itemPart.TextVisualizerViewModel.Text);
        Assert.Equal(DashboardUIHelpers.JavascriptFormat, itemPart.TextVisualizerViewModel.FormatKind);
    }

    [Fact]
    public void CreateMessagePart_ToolCallResponse_NonAsciiObjectResponse_PreservesCharacters()
    {
        // Arrange
        var responsePart = new ToolCallResponsePart
        {
            Response = JsonNode.Parse("""{"weather":"晴れ","city":"東京"}""")
        };

        // Act
        var itemPart = GenAIItemPartViewModel.CreateMessagePart(responsePart);

        // Assert
        Assert.Contains("晴れ", itemPart.TextVisualizerViewModel.Text);
        Assert.Contains("東京", itemPart.TextVisualizerViewModel.Text);
        Assert.Equal(DashboardUIHelpers.JsonFormat, itemPart.TextVisualizerViewModel.FormatKind);
    }

    [Fact]
    public void CreateMessagePart_GenericPart_NonAsciiProperties_PreservesCharacters()
    {
        // Arrange
        var genericPart = new GenericPart
        {
            Type = "custom",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["message"] = JsonDocument.Parse("""{"text":"こんにちは"}""").RootElement.GetProperty("text")
            }
        };

        // Act
        var itemPart = GenAIItemPartViewModel.CreateMessagePart(genericPart);

        // Assert
        Assert.Contains("こんにちは", itemPart.TextVisualizerViewModel.Text);
        Assert.DoesNotContain("\\u", itemPart.TextVisualizerViewModel.Text);
    }

    [Fact]
    public void CreateMessagePart_UnexpectedErrorPart_SetsErrorMessage()
    {
        var errorPart = new UnexpectedErrorPart
        {
            Type = "text",
            Error = new JsonException("Test deserialization error"),
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["content"] = JsonDocument.Parse("""{"nested":"value"}""").RootElement
            }
        };

        var itemPart = GenAIItemPartViewModel.CreateMessagePart(errorPart);

        Assert.Equal("Test deserialization error", itemPart.ErrorMessage);
        Assert.NotNull(itemPart.AdditionalProperties);
        Assert.Single(itemPart.AdditionalProperties);
    }
}
