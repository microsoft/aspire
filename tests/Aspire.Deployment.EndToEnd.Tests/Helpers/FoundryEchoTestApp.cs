// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Deployment.EndToEnd.Tests.Helpers;

internal static class FoundryEchoTestApp
{
    internal static void Write(string directory, string marker)
    {
        Directory.CreateDirectory(directory);
        // This model-free worker only needs the Responses hosting package. Let it select compatible
        // transitive SDK versions instead of copying model/MCP client pins from a different scenario.
        File.WriteAllText(Path.Combine(directory, "EchoAgent.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <NoWarn>$(NoWarn);OPENAI001;MAIF001;MAAI001</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.Agents.AI.Foundry.Hosting" Version="1.12.0-preview.260629.1" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Program.cs"), """
            using Microsoft.Agents.AI.Foundry.Hosting;

            var builder = WebApplication.CreateBuilder(args);
            var port = Environment.GetEnvironmentVariable("DEFAULT_AD_PORT") ?? "8088";
            builder.WebHost.UseUrls($"http://+:{port}");
            builder.Services.AddFoundryResponses(new EchoAIAgent());
            var app = builder.Build();
            app.MapFoundryResponses();
            app.MapGet("/liveness", () => Results.Ok("Healthy"));
            app.MapGet("/readiness", () => Results.Ok("Ready"));
            app.Run();
            """);
        File.WriteAllText(Path.Combine(directory, "EchoAIAgent.cs"), $$"""
            using System.Runtime.CompilerServices;
            using System.Text.Json;
            using Microsoft.Agents.AI;
            using Microsoft.Extensions.AI;

            internal sealed class EchoAIAgent : AIAgent
            {
                public override string Name => "echo-agent";
                public override string Description => "A deterministic model-free deployment probe.";

                protected override Task<AgentResponse> RunCoreAsync(
                    IEnumerable<ChatMessage> messages, AgentSession? session = null,
                    AgentRunOptions? options = null, CancellationToken cancellationToken = default)
                {
                    return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, GetResponse(messages))));
                }

                protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
                    IEnumerable<ChatMessage> messages, AgentSession? session = null,
                    AgentRunOptions? options = null,
                    [EnumeratorCancellation] CancellationToken cancellationToken = default)
                {
                    yield return new AgentResponseUpdate
                    {
                        Role = ChatRole.Assistant,
                        Contents = [new TextContent(GetResponse(messages))]
                    };
                    await Task.CompletedTask;
                }

                protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
                    => new(new EchoAgentSession());

                protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
                    AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
                    CancellationToken cancellationToken = default)
                    => new(JsonSerializer.SerializeToElement(new { }, jsonSerializerOptions));

                protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
                    JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
                    CancellationToken cancellationToken = default)
                    => new(new EchoAgentSession());

                private static string GetResponse(IEnumerable<ChatMessage> messages)
                {
                    var input = messages.Last(message => message.Role == ChatRole.User).Text ?? string.Empty;
                    // This source-only marker prevents a stale image from satisfying a new deployment probe.
                    if (!input.StartsWith("{{marker}}:", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("The request targeted a different deployment's image.");
                    }

                    return $"Echo: {input}";
                }

                private sealed class EchoAgentSession : AgentSession;
            }
            """);
    }
}
