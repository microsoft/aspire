// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Aspire.Hosting.Foundry;

internal sealed class FoundryToolboxReadinessProbe(
    HttpClient client,
    TimeSpan? timeout = null,
    TimeSpan? retryDelay = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);
    private readonly TimeSpan _retryDelay = retryDelay ?? TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<string>> WaitForToolsAsync(
        Uri endpoint,
        string accessToken,
        IReadOnlyCollection<string> requiredToolNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentNullException.ThrowIfNull(requiredToolNames);

        using var discoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        discoveryCancellation.CancelAfter(_timeout);
        try
        {
            var initialize = await SendRequestAsync(
                endpoint,
                accessToken,
                sessionId: null,
                protocolVersion: null,
                """
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"Aspire.Hosting.Foundry","version":"1.0"}}}
                """,
                discoveryCancellation.Token).ConfigureAwait(false);
            var negotiatedProtocol = initialize.Result
                .GetProperty("protocolVersion")
                .GetString();
            if (string.IsNullOrEmpty(negotiatedProtocol))
            {
                throw new InvalidOperationException("Foundry Toolbox MCP initialization did not negotiate a protocol version.");
            }

            await SendRequestAsync(
                endpoint,
                accessToken,
                initialize.SessionId,
                negotiatedProtocol,
                """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""",
                discoveryCancellation.Token).ConfigureAwait(false);

            var requestId = 2;
            while (true)
            {
                var discoveredToolNames = new HashSet<string>(StringComparer.Ordinal);
                string? cursor = null;
                do
                {
                    var response = await SendRequestAsync(
                        endpoint,
                        accessToken,
                        initialize.SessionId,
                        negotiatedProtocol,
                        CreateToolsListPayload(requestId++, cursor),
                        discoveryCancellation.Token).ConfigureAwait(false);
                    foreach (var tool in response.Result.GetProperty("tools").EnumerateArray())
                    {
                        discoveredToolNames.Add(tool.GetProperty("name").GetString()
                            ?? throw new InvalidOperationException("A discovered Toolbox tool did not have a name."));
                    }

                    // MCP paginates tools/list as:
                    //   {"result":{"tools":[...],"nextCursor":"opaque continuation token"}}
                    cursor = response.Result.TryGetProperty("nextCursor", out var nextCursor)
                        ? nextCursor.GetString()
                        : null;
                }
                while (!string.IsNullOrEmpty(cursor));

                if (requiredToolNames.Count == 0
                    ? discoveredToolNames.Count > 0
                    : requiredToolNames.All(discoveredToolNames.Contains))
                {
                    return discoveredToolNames.ToArray();
                }

                // Toolbox tool discovery is eventually consistent immediately after reconciliation.
                await Task.Delay(_retryDelay, discoveryCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var expected = requiredToolNames.Count == 0
                ? "any tool"
                : string.Join(", ", requiredToolNames.Order(StringComparer.Ordinal));
            throw new TimeoutException(
                $"Foundry Toolbox did not discover the required tools within {_timeout}: {expected}.");
        }
    }

    private static string CreateToolsListPayload(int requestId, string? cursor)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", requestId);
            writer.WriteString("method", "tools/list");
            writer.WriteStartObject("params");
            if (cursor is not null)
            {
                writer.WriteString("cursor", cursor);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private async Task<McpResponse> SendRequestAsync(
        Uri endpoint,
        string accessToken,
        string? sessionId,
        string? protocolVersion,
        string payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Foundry-Features", FoundryToolboxResource.PreviewFeatureHeaderValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(sessionId))
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }
        if (!string.IsNullOrEmpty(protocolVersion))
        {
            request.Headers.Add("MCP-Protocol-Version", protocolVersion);
        }
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var requestDocument = JsonDocument.Parse(payload);
        var expectedId = requestDocument.RootElement.TryGetProperty("id", out var requestId)
            ? requestId.GetInt32()
            : (int?)null;

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var responsePayload = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var responseSessionId = response.Headers.TryGetValues("Mcp-Session-Id", out var values)
            ? values.Single()
            : sessionId;
        if (string.IsNullOrWhiteSpace(responsePayload) || expectedId is null)
        {
            return new(default, responseSessionId);
        }

        // Streamable HTTP may return either one JSON document or SSE frames such as:
        //   event: message
        //   data: {"jsonrpc":"2.0","id":1,"result":{...}}
        var responseMessages = responsePayload.TrimStart().StartsWith('{')
            ? [responsePayload]
            : responsePayload.Split('\n', StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
                .Select(line => line["data:".Length..].Trim());
        JsonElement? matchingResponse = null;
        foreach (var responseMessage in responseMessages)
        {
            using var candidate = JsonDocument.Parse(responseMessage);
            if (candidate.RootElement.TryGetProperty("id", out var responseId) &&
                responseId.ValueKind == JsonValueKind.Number &&
                responseId.GetInt32() == expectedId)
            {
                matchingResponse = candidate.RootElement.Clone();
                break;
            }
        }

        if (matchingResponse is null)
        {
            throw new InvalidOperationException(
                $"The Toolbox MCP response did not contain JSON-RPC response ID {expectedId}.");
        }

        if (matchingResponse.Value.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"Toolbox MCP request failed: {error.GetRawText()}");
        }

        var result = matchingResponse.Value.TryGetProperty("result", out var resultElement)
            ? resultElement.Clone()
            : default;
        return new(result, responseSessionId);
    }

    private sealed record McpResponse(JsonElement Result, string? SessionId);
}
