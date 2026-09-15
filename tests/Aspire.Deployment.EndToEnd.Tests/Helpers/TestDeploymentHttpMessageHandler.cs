// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;

namespace Aspire.Deployment.EndToEnd.Tests.Helpers;

internal sealed class TestDeploymentHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    internal ConcurrentQueue<RequestSnapshot> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Enqueue(new(
            request.Method.Method,
            request.RequestUri!.AbsoluteUri,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
            request.Headers.Authorization?.Scheme));
        return await respond(request, cancellationToken);
    }

    internal sealed record RequestSnapshot(string Method, string Uri, string? Body, string? AuthorizationScheme);
}
