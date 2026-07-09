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
        using var reader = new StreamReader(request.Body);
        var input = await reader.ReadToEndAsync(cancellationToken);
        var agentResponse = await agent.RunAsync(input, cancellationToken: cancellationToken);

        response.ContentType = "text/plain";
        await response.WriteAsync(agentResponse.Text, cancellationToken);
    }
}
