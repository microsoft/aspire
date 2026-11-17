// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

internal sealed class DcpCleanupTestHandler : HttpMessageHandler
{
    public ConcurrentDictionary<string, bool> Resources { get; } = new();
    public ConcurrentQueue<string> Deletes { get; } = new();
    public Func<string, CancellationToken, Task>? BeforeDelete { get; set; }
    public bool Exited { get; set; }
    public ConcurrentQueue<string> AdministrativeRequests { get; } = new();
    public ConcurrentQueue<(string ContentType, string Body)> RequestContents { get; } = new();
    public Func<CancellationToken, Task>? BeforeCleanup { get; set; }
    public Func<CancellationToken, Task>? BeforeResponse { get; set; }
    public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

    public DcpApiClient CreateClient(NativeServerOptions options)
    {
        return new DcpApiClient(options, new HttpClient(this) { BaseAddress = new Uri("http://localhost") });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (BeforeResponse is { } beforeResponse)
        {
            await beforeResponse(cancellationToken);
        }
        if (ResponseFactory is { } responseFactory)
        {
            return responseFactory(request);
        }
        var path = request.RequestUri!.AbsolutePath;
        var key = string.Join('/', path.Split('/')[^2..]);
        if (request.Content is { } content)
        {
            RequestContents.Enqueue((content.Headers.ContentType!.ToString(),
                await content.ReadAsStringAsync(cancellationToken)));
        }
        if (key == "admin/execution")
        {
            if (request.Method == HttpMethod.Patch)
            {
                AdministrativeRequests.Enqueue(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (BeforeCleanup is { } before)
                {
                    await before(cancellationToken);
                }
                Resources.Clear();
            }

            return Json("""{"status":"CleanupComplete"}""");
        }
        if (request.Method == HttpMethod.Post)
        {
            using var body = System.Text.Json.JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Resources[path.Split('/')[^1] + "/" + body.RootElement.GetProperty("metadata").GetProperty("name").GetString()] = true;
            return Json("{}");
        }
        if (request.Method == HttpMethod.Delete)
        {
            Deletes.Enqueue(key);
            if (BeforeDelete is { } before)
            {
                await before(key, cancellationToken);
            }
            return Resources.TryRemove(key, out _) ? Json("{}") : new(HttpStatusCode.NotFound);
        }
        if (key.EndsWith("/log", StringComparison.Ordinal))
        {
            return new(HttpStatusCode.OK) { Content = new StringContent("") };
        }
        if (!Resources.ContainsKey(key))
        {
            return new(HttpStatusCode.NotFound);
        }

        return key.StartsWith("services/", StringComparison.Ordinal)
            ? Json("""{"status":{"effectiveAddress":"127.0.0.1","effectivePort":5050}}""")
            : Json(Exited ? """{"status":{"state":"Exited","exitCode":0}}""" :
                """{"status":{"state":"Running","containerId":"test-instance"}}""");
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };
}
