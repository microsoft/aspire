// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Azure.Core;
using OpenTelemetry;

namespace Aspire.Components.TestUtilities;

internal sealed class FoundryTestTokenCredential(string token = "test-token") : TokenCredential
{
    public ConcurrentQueue<string[]> RequestedScopes { get; } = new();

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        RequestedScopes.Enqueue(requestContext.Scopes);
        return new(token, DateTimeOffset.MaxValue);
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

internal sealed class FoundryTestHttpMessageHandler(string response) : HttpMessageHandler
{
    public ConcurrentQueue<FoundryTestRequest> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(new(request.RequestUri!, request.Headers.Authorization?.ToString(), request.Headers.UserAgent.ToString()));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json")
        });
    }
}

internal sealed record FoundryTestRequest(Uri Uri, string? Authorization, string UserAgent);

internal sealed class FoundryTestActivityExporter : BaseExporter<Activity>
{
    public ConcurrentQueue<Activity> Activities { get; } = new();

    public override ExportResult Export(in Batch<Activity> batch)
    {
        foreach (var activity in batch)
        {
            Activities.Enqueue(activity);
        }

        return ExportResult.Success;
    }
}
