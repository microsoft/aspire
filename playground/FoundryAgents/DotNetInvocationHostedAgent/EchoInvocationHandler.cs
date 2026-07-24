// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.AgentServer.Invocations;

namespace DotNetInvocationHostedAgent;

internal sealed class EchoInvocationHandler(EchoAIAgent agent) : InvocationHandler
{
    public override async Task HandleAsync(
        HttpRequest request,
        HttpResponse response,
        InvocationContext context,
        CancellationToken cancellationToken)
    {
        var input = await ReadInputAsync(request, cancellationToken);
        var agentResponse = await agent.RunAsync(input, cancellationToken: cancellationToken);

        response.ContentType = "text/plain";
        await response.WriteAsync(agentResponse.Text, cancellationToken);
    }

    private static async Task<string> ReadInputAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.HasJsonContentType())
        {
            var payload = await request.ReadFromJsonAsync<InvocationMessage>(cancellationToken).ConfigureAwait(false);
            return payload?.Message ?? string.Empty;
        }

        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record InvocationMessage(string Message);
}
