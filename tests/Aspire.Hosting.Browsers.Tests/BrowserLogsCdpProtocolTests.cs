// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Hosting.Browsers.Tests;

[Trait("Partition", "2")]
public class BrowserLogsCdpProtocolTests
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

        var header = BrowserLogsCdpProtocol.ParseMessageHeader(payload);
        var @event = Assert.IsType<BrowserLogsConsoleApiCalledEvent>(BrowserLogsCdpProtocol.ParseEvent(header, payload));

        Assert.Equal("target-session-1", @event.SessionId);
        Assert.Equal("error", @event.Parameters.Type);

        var args = Assert.IsType<BrowserLogsCdpProtocolRemoteObject[]>(@event.Parameters.Args);
        Assert.IsType<BrowserLogsCdpProtocolStringValue>(args[0].Value);
        Assert.IsType<BrowserLogsCdpProtocolBooleanValue>(args[1].Value);
        Assert.IsType<BrowserLogsCdpProtocolNumberValue>(args[2].Value);
        Assert.IsType<BrowserLogsCdpProtocolObjectValue>(args[3].Value);
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

        var result = BrowserLogsCdpProtocol.ParseCreateTargetResponse(payload);

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

        var exception = Assert.Throws<InvalidOperationException>(() => BrowserLogsCdpProtocol.ParseCommandAckResponse(payload));

        Assert.Contains("Method not found", exception.Message);
        Assert.Contains("-32601", exception.Message);
    }

    [Fact]
    public void ParseCaptureScreenshotResponse_ReturnsBase64ImageData()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "id": 5,
              "result": {
                "data": "aW1hZ2UtZGF0YQ=="
              }
            }
            """);

        var result = BrowserLogsCdpProtocol.ParseCaptureScreenshotResponse(payload);

        Assert.Equal("aW1hZ2UtZGF0YQ==", result.Data);
    }

    [Fact]
    public void ParseRuntimeEvaluateResponse_ReturnsRemoteObject()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "id": 9,
              "result": {
                "result": {
                  "type": "string",
                  "value": "{\"action\":\"snapshot\"}"
                }
              }
            }
            """);

        var result = BrowserLogsCdpProtocol.ParseRuntimeEvaluateResponse(payload);
        var value = Assert.IsType<BrowserLogsCdpProtocolStringValue>(result.Result?.Value);

        Assert.Equal("""{"action":"snapshot"}""", value.Value);
    }

    [Fact]
    public void ParseRuntimeEvaluateResponse_ThrowsForExceptionDetails()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "id": 9,
              "result": {
                "result": {
                  "type": "object"
                },
                "exceptionDetails": {
                  "text": "Uncaught",
                  "exception": {
                    "description": "Error: boom"
                  }
                }
              }
            }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => BrowserLogsCdpProtocol.ParseRuntimeEvaluateResponse(payload));

        Assert.Equal("Error: boom", exception.Message);
    }

    [Fact]
    public void ParseEvent_TargetDetachedFromTarget_UsesParameterSessionId()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "method": "Target.detachedFromTarget",
              "sessionId": "browser-session",
              "params": {
                "sessionId": "target-session-1",
                "targetId": "target-1"
              }
            }
            """);

        var header = BrowserLogsCdpProtocol.ParseMessageHeader(payload);
        var @event = Assert.IsType<BrowserLogsDetachedFromTargetEvent>(BrowserLogsCdpProtocol.ParseEvent(header, payload));

        Assert.Equal("browser-session", @event.SessionId);
        Assert.Equal("target-session-1", @event.DetachedSessionId);
        Assert.Equal("target-1", @event.TargetId);
    }

    [Fact]
    public void ParseEvent_TargetCrashed_ReturnsTargetStatusAndErrorCode()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "method": "Target.targetCrashed",
              "params": {
                "targetId": "target-1",
                "status": "crashed",
                "errorCode": 1337
              }
            }
            """);

        var header = BrowserLogsCdpProtocol.ParseMessageHeader(payload);
        var @event = Assert.IsType<BrowserLogsTargetCrashedEvent>(BrowserLogsCdpProtocol.ParseEvent(header, payload));

        Assert.Equal("target-1", @event.TargetId);
        Assert.Equal("crashed", @event.Parameters.Status);
        Assert.Equal(1337, @event.Parameters.ErrorCode);
    }

    [Fact]
    public void ParseEvent_InspectorDetachedWithoutParams_ReturnsNull()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "method": "Inspector.detached",
              "sessionId": "target-session-1"
            }
            """);

        var header = BrowserLogsCdpProtocol.ParseMessageHeader(payload);

        Assert.Null(BrowserLogsCdpProtocol.ParseEvent(header, payload));
    }

    [Fact]
    public void CreateCommandFrame_DoesNotEscapeNonAsciiCharacters()
    {
        var payload = BrowserLogsCdpProtocol.CreateCommandFrame(
            7,
            BrowserLogsCdpProtocol.PageNavigateMethod,
            "session-1",
            writer => writer.WriteString("url", "https://example.test/über"));

        var json = Encoding.UTF8.GetString(payload);

        Assert.Contains("https://example.test/über", json);
        Assert.DoesNotContain("\\u00fc", json);
    }
}
