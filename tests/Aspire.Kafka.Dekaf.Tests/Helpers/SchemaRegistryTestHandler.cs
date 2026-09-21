// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Aspire.Kafka.Dekaf.Tests;

internal sealed class SchemaRegistryTestHandler : HttpMessageHandler
{
    public const int SchemaId = 42;

    public ConcurrentQueue<(HttpMethod Method, Uri Uri, string? Authorization)> Requests { get; } = new();

    public JsonElement RegisteredSchema { get; private set; }

    public string? Subject { get; private set; }

    public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;

    public string? UnavailableHost { get; set; }

    public bool WaitForCancellation { get; set; }

    public bool CancellationObserved { get; private set; }

    public bool Disposed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));
        if (WaitForCancellation)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                CancellationObserved = cancellationToken.IsCancellationRequested;
            }
        }

        var statusCode = request.RequestUri!.Host == UnavailableHost ? HttpStatusCode.ServiceUnavailable : StatusCode;
        if (statusCode != HttpStatusCode.OK)
        {
            return new(statusCode) { Content = JsonContent.Create(new { error_code = (int)statusCode, message = "Registry unavailable" }) };
        }

        // Implement only the Confluent REST endpoints exercised by these integration tests:
        // POST /subjects/{escaped subject}[/versions], GET /schemas/ids/42, and GET /subjects.
        // Fail unexpected requests so changes in the client protocol do not silently pass.
        // https://docs.confluent.io/platform/current/schema-registry/develop/api.html
        var path = request.RequestUri!.AbsolutePath;
        if (request.Method == HttpMethod.Post && path.StartsWith("/subjects/", StringComparison.Ordinal) && path.EndsWith("/versions", StringComparison.Ordinal))
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            RegisteredSchema = body.RootElement.Clone();
            Subject = Uri.UnescapeDataString(path["/subjects/".Length..^"/versions".Length]);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = SchemaId }) };
        }
        if (request.Method == HttpMethod.Post && path.StartsWith("/subjects/", StringComparison.Ordinal))
        {
            return new(HttpStatusCode.NotFound) { Content = JsonContent.Create(new { error_code = 40403, message = "Schema not found" }) };
        }
        if (request.Method == HttpMethod.Get && path == $"/schemas/ids/{SchemaId}")
        {
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(RegisteredSchema) };
        }
        if (request.Method == HttpMethod.Get && path == "/subjects")
        {
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Subject is null ? Array.Empty<string>() : [Subject]) };
        }

        throw new InvalidOperationException($"Unexpected Schema Registry request: {request.Method} {request.RequestUri}");
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}
