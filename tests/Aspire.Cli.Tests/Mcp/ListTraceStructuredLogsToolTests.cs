// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Mcp;

public class ListTraceStructuredLogsToolTests
{
    [Fact]
    public async Task ListTraceStructuredLogsTool_WithRunId_PassesRunIdToAllRequests()
    {
        var requestedUrls = new List<string>();
        using var handler = new MockHttpMessageHandler(request =>
        {
            requestedUrls.Add(request.RequestUri!.ToString());
            var json = request.RequestUri.AbsolutePath.EndsWith("/resources", StringComparison.Ordinal)
                ? "[]"
                : "{\"data\":{\"resourceLogs\":[]},\"totalCount\":0,\"returnedCount\":0}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });
        var tool = new ListTraceStructuredLogsTool(
            new StaticDashboardInfoProvider("http://localhost:18888", "test-token"),
            auxiliaryBackchannelMonitor: null,
            new MockHttpClientFactory(handler),
            NullLogger<ListTraceStructuredLogsTool>.Instance);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["traceId"] = JsonDocument.Parse("\"trace-123\"").RootElement,
            ["runId"] = JsonDocument.Parse("\"incident-42\"").RootElement
        };

        await tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).DefaultTimeout();

        Assert.Equal(2, requestedUrls.Count);
        Assert.All(requestedUrls, url => Assert.Contains("runId=incident-42", url, StringComparison.Ordinal));
        Assert.Contains("traceId=trace-123", requestedUrls[1], StringComparison.Ordinal);
    }
}