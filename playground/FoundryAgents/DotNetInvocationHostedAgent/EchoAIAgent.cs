// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace DotNetInvocationHostedAgent;

internal sealed class EchoAIAgent : AIAgent
{
    public override string Name => "echo-agent";

    public override string Description => "An agent that echoes back the input message.";

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var response = new AgentResponse(new ChatMessage(ChatRole.Assistant, $"Echo: {GetInputText(messages)}"));
        return Task.FromResult(response);
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new AgentResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = [new TextContent($"Echo: {GetInputText(messages)}")]
        };

        await Task.CompletedTask;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        return new(new EchoAgentSession());
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return new(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));
    }

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
    {
        return new(new EchoAgentSession());
    }

    private static string GetInputText(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User)
            {
                return message.Text ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private sealed class EchoAgentSession : AgentSession;
}
