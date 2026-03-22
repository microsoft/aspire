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
    [InlineData("https://example.com:8080/path/to/resource?param=value", "https://example.com:8080")]
    [InlineData("invalid-url", "invalid-url")] // Falls back to returning the original string
    public void GetBaseUrl_ExtractsBaseUrl_RemovingPathAndQueryString(string? input, string? expected)
    {
        var result = McpToolHelpers.GetBaseUrl(input);
        Assert.Equal(expected, result);
    }
}
