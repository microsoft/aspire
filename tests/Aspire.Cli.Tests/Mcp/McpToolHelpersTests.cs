// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Mcp.Tools;

namespace Aspire.Cli.Tests.Mcp;

public class McpToolHelpersTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("http://localhost:18888", "http://localhost:18888")]
    [InlineData("http://localhost:18888/", "http://localhost:18888")]
    [InlineData("http://localhost:18888/login", "http://localhost:18888")]
    [InlineData("http://localhost:18888/login?t=authtoken123", "http://localhost:18888")]
    [InlineData("https://localhost:16319/login?t=d8d8255df4c79aebcb5b7325828ccb20", "https://localhost:16319")]
    [InlineData("https://example.com:8080/path/to/resource?param=value", "https://example.com:8080/path/to/resource")]
    [InlineData("https://example.com:8080/dashboard", "https://example.com:8080/dashboard")]
    [InlineData("http://localhost/base/login", "http://localhost/base")]
    [InlineData("http://localhost/base/login?t=token123", "http://localhost/base")]
    [InlineData("https://example.com:8080/app/deep/login?t=abc", "https://example.com:8080/app/deep")]
    [InlineData("invalid-url", "invalid-url")] // Falls back to returning the original string
    public void StripLoginPath_RemovesOnlyLoginSegment(string? input, string? expected)
    {
        var result = McpToolHelpers.StripLoginPath(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("http://localhost:18888", null)]
    [InlineData("http://localhost:18888/login", null)] // No query string
    [InlineData("http://localhost:18888/login?t=authtoken123", "authtoken123")]
    [InlineData("https://localhost:16319/login?t=d8d8255df4c79aebcb5b7325828ccb20", "d8d8255df4c79aebcb5b7325828ccb20")]
    [InlineData("http://localhost/base/login?t=token123", "token123")]
    [InlineData("https://example.com:8080/app/deep/login?t=abc", "abc")]
    [InlineData("http://localhost:18888/login?other=value", null)] // No 't' parameter
    [InlineData("http://localhost:18888/login?t=", null)] // Empty token
    [InlineData("http://localhost:18888/notlogin?t=abc", null)] // Path doesn't end with /login
    [InlineData("invalid-url", null)]
    public void ExtractLoginToken_ExtractsTokenFromLoginUrl(string? input, string? expected)
    {
        var result = McpToolHelpers.ExtractLoginToken(input);
        Assert.Equal(expected, result);
    }
}
