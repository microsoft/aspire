// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Foundry.Tests;

internal sealed class SequenceHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new(responses);

    public List<HttpRequestSnapshot> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(new(
            await request.Content!.ReadAsStringAsync(cancellationToken),
            request.Headers.TryGetValues("Mcp-Session-Id", out var sessionIds)
                ? sessionIds.Single()
                : null,
            request.Headers.TryGetValues("MCP-Protocol-Version", out var protocolVersions)
                ? protocolVersions.Single()
                : null));

        return _responses.Dequeue();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            while (_responses.TryDequeue(out var response))
            {
                response.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    internal sealed record HttpRequestSnapshot(
        string Content,
        string? SessionId,
        string? ProtocolVersion);
}
