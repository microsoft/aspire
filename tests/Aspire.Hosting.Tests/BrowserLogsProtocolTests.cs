// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class BrowserLogsProtocolTests
{
    [Fact]
    public void ParseEvent_ConsoleApiCalled_ReturnsStronglyTypedParameters()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "method": "Runtime.consoleAPICalled",
              "sessionId": "target-session-1",
              "params": {
                "type": "error",
                "args": [
                  { "value": "boom" },
                  { "value": true },
                  { "value": 42 },
                  { "value": { "nested": "value" } },
                  { "unserializableValue": "Infinity" }
                ]
              }
            }
            """);

        var header = BrowserLogsProtocol.ParseMessageHeader(payload);
        var @event = Assert.IsType<BrowserLogsConsoleApiCalledEvent>(BrowserLogsProtocol.ParseEvent(header, payload));

        Assert.Equal("target-session-1", @event.SessionId);
        Assert.Equal("error", @event.Parameters.Type);

        var args = Assert.IsType<BrowserLogsProtocolRemoteObject[]>(@event.Parameters.Args);
        Assert.IsType<BrowserLogsProtocolStringValue>(args[0].Value);
        Assert.IsType<BrowserLogsProtocolBooleanValue>(args[1].Value);
        Assert.IsType<BrowserLogsProtocolNumberValue>(args[2].Value);
        Assert.IsType<BrowserLogsProtocolObjectValue>(args[3].Value);
        Assert.Equal("Infinity", args[4].UnserializableValue);
    }

    [Fact]
    public void ParseCreateTargetResponse_ReturnsTypedResult()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "id": 7,
              "result": {
                "targetId": "target-123"
              }
            }
            """);

        var result = BrowserLogsProtocol.ParseCreateTargetResponse(payload);

        Assert.Equal("target-123", result.TargetId);
    }

    [Fact]
    public void ParseCommandAckResponse_IncludesProtocolErrorDetails()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "id": 3,
              "error": {
                "code": -32601,
                "message": "Method not found"
              }
            }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => BrowserLogsProtocol.ParseCommandAckResponse(payload));

        Assert.Contains("Method not found", exception.Message);
        Assert.Contains("-32601", exception.Message);
    }

    [Fact]
    public void CreateCommandFrame_DoesNotEscapeNonAsciiCharacters()
    {
        var payload = BrowserLogsProtocol.CreateCommandFrame(
            7,
            BrowserLogsProtocol.PageNavigateMethod,
            "session-1",
            writer => writer.WriteString("url", "https://example.test/über"));

        var json = Encoding.UTF8.GetString(payload);

        Assert.Contains("https://example.test/über", json);
        Assert.DoesNotContain("\\u00fc", json);
    }
}
