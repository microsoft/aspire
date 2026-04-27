// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ChromiumDevToolsActivePortParserTests
{
    [Fact]
    public void TryParseBrowserDebugEndpoint_ReturnsBrowserWebSocketUri()
    {
        var endpoint = ChromiumDevToolsActivePortParser.TryParseBrowserDebugEndpoint("""
            51943
            /devtools/browser/4c8404fb-06f8-45f0-9d89-112233445566
            """);

        Assert.NotNull(endpoint);
        Assert.Equal("ws://127.0.0.1:51943/devtools/browser/4c8404fb-06f8-45f0-9d89-112233445566", endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-port")]
    [InlineData("51943")]
    public void TryParseBrowserDebugEndpoint_ReturnsNullForInvalidMetadata(string metadata)
    {
        var endpoint = ChromiumDevToolsActivePortParser.TryParseBrowserDebugEndpoint(metadata);

        Assert.Null(endpoint);
    }
}
