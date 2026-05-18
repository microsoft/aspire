// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Aspire.Hosting.Browsers.Tests;

[Trait("Partition", "2")]
public class BrowserCdpProtocolTests
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

        var header = BrowserCdpProtocol.ParseMessageHeader(payload);
        var @event = Assert.IsType<BrowserConsoleApiCalledEvent>(BrowserCdpProtocol.ParseEvent(header, payload));

        Assert.Equal("target-session-1", @event.SessionId);
        Assert.Equal("error", @event.Parameters.Type);

        var args = Assert.IsType<BrowserCdpProtocolRemoteObject[]>(@event.Parameters.Args);
        Assert.IsType<BrowserCdpProtocolStringValue>(args[0].Value);
        Assert.IsType<BrowserCdpProtocolBooleanValue>(args[1].Value);
        Assert.IsType<BrowserCdpProtocolNumberValue>(args[2].Value);
        Assert.IsType<BrowserCdpProtocolObjectValue>(args[3].Value);
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

        var result = BrowserCdpProtocol.ParseCreateTargetResponse(payload);

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

        var exception = Assert.Throws<InvalidOperationException>(() => BrowserCdpProtocol.ParseCommandAckResponse(payload));

        Assert.Contains("Method not found", exception.Message);
        Assert.Contains("-32601", exception.Message);
    }

    [Fact]
    public void ParseNavigateResponse_ReturnsTypedResult()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "id": 4,
              "result": {
                "frameId": "frame-1",
                "loaderId": "loader-1",
                "isDownload": false
              }
            }
            """);

        var result = BrowserCdpProtocol.ParseNavigateResponse(payload);

        Assert.Equal("frame-1", result.FrameId);
        Assert.Equal("loader-1", result.LoaderId);
        Assert.Equal(false, result.IsDownload);
    }

    [Fact]
    public void ParseNavigateResponse_ThrowsForErrorText()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "id": 4,
              "result": {
                "frameId": "frame-1",
                "errorText": "net::ERR_CONNECTION_REFUSED"
              }
            }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => BrowserCdpProtocol.ParseNavigateResponse(payload));

        Assert.Contains("net::ERR_CONNECTION_REFUSED", exception.Message);
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

        var result = BrowserCdpProtocol.ParseCaptureScreenshotResponse(payload);

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

        var result = BrowserCdpProtocol.ParseRuntimeEvaluateResponse(payload);
        var value = Assert.IsType<BrowserCdpProtocolStringValue>(result.Result?.Value);

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

        var exception = Assert.Throws<InvalidOperationException>(() => BrowserCdpProtocol.ParseRuntimeEvaluateResponse(payload));

        Assert.Equal("Error: boom", exception.Message);
    }

    [Fact]
    public void ParseRawCommandResponse_ReturnsResultJson()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "id": 9,
              "result": {
                "value": 42,
                "nested": {
                  "ok": true
                }
              }
            }
            """);

        var result = BrowserCdpProtocol.ParseRawCommandResponse(payload);

        Assert.Equal("""{"value":42,"nested":{"ok":true}}""", result);
    }

    [Fact]
    public void ParseRawCommandResponse_ThrowsForProtocolError()
    {
        var payload = Encoding.UTF8.GetBytes("""
            {
              "id": 9,
              "error": {
                "code": -32601,
                "message": "Method not found"
              }
            }
            """);

        var exception = Assert.Throws<InvalidOperationException>(() => BrowserCdpProtocol.ParseRawCommandResponse(payload));

        Assert.Contains("Method not found", exception.Message);
        Assert.Contains("-32601", exception.Message);
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

        var header = BrowserCdpProtocol.ParseMessageHeader(payload);
        var @event = Assert.IsType<BrowserDetachedFromTargetEvent>(BrowserCdpProtocol.ParseEvent(header, payload));

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

        var header = BrowserCdpProtocol.ParseMessageHeader(payload);
        var @event = Assert.IsType<BrowserTargetCrashedEvent>(BrowserCdpProtocol.ParseEvent(header, payload));

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

        var header = BrowserCdpProtocol.ParseMessageHeader(payload);

        Assert.Null(BrowserCdpProtocol.ParseEvent(header, payload));
    }

    [Fact]
    public void CreateCommandFrame_DoesNotEscapeNonAsciiCharacters()
    {
        var payload = BrowserCdpProtocol.CreateCommandFrame(
            7,
            BrowserCdpProtocol.PageNavigateMethod,
            "session-1",
            writer => writer.WriteString("url", "https://example.test/über"));

        var json = Encoding.UTF8.GetString(payload);

        Assert.Contains("https://example.test/über", json);
        Assert.DoesNotContain("\\u00fc", json);
    }
}
