// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Otlp.Serialization;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Mcp;

public class ListTraceStructuredLogsToolTests
{
    [Fact]
    public async Task ListTraceStructuredLogsTool_AuthenticatedRequestDoesNotLeakInLogsOrErrors()
    {
        var resourcesResponse = JsonSerializer.Serialize(
            Array.Empty<ResourceInfoJson>(),
            OtlpJsonSerializerContext.Default.ResourceInfoJsonArray);
        Uri? logsRequestUri = null;
        using var handler = new MockHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/resources", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        resourcesResponse,
                        System.Text.Encoding.UTF8,
                        "application/json")
                };
            }

            logsRequestUri = request.RequestUri;
            throw new HttpRequestException(
                "Request failed at https://" + "exception-user" + ":" + "exception-password" +
                "@exception.example?token=exception-secret#exception-fragment");
        });
        var sink = new TestSink();
        var logger = new TestLogger<ListTraceStructuredLogsTool>(
            new TestLoggerFactory(sink, enabled: true));
        var tool = new ListTraceStructuredLogsTool(
            new StaticDashboardInfoProvider(
                "https://" + "request-user" + ":" + "request-password" + "@example.com:5000/login" +
                "?t=actual-login-token&accessKey=request-secret&view=resources#request-fragment",
                apiKey: "api-key"),
            auxiliaryBackchannelMonitor: null,
            new MockHttpClientFactory(handler),
            logger);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["traceId"] = JsonDocument.Parse("\"trace-id\"").RootElement
        };

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(
                CallToolContextTestHelper.Create(arguments),
                CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.True(logsRequestUri is not null, exception.ToString());
        Assert.Equal("request-user:request-password", logsRequestUri.UserInfo);
        Assert.Equal(
            $"?accessKey=request-secret&view=resources&traceId=trace-id&limit={TelemetryCommandHelpers.MaxTelemetryLimit}",
            logsRequestUri.Query);
        foreach (var diagnostic in new[] { exception.Message }.Concat(
            sink.Writes.Select(write => $"{write.Message} {write.Exception}")))
        {
            Assert.DoesNotContain("request-user", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("request-password", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("actual-login-token", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("request-secret", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("request-fragment", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-secret", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-fragment", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-user", diagnostic, StringComparison.Ordinal);
            Assert.DoesNotContain("exception-password", diagnostic, StringComparison.Ordinal);
        }
    }
}
