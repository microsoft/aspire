// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Agents;
using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddFoundry("agents-foundry");
var project = foundry.AddProject("agentsproject");
var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41Mini);

var a2aAgent = builder.AddUvicornApp("weather-a2a-agent", "../weather-agent-python", "weather_agent_python.main:app")
    .WithUv()
    .WithReference(project)
    .WithReference(chat)
    .WaitFor(chat)
    .AsAgent(AgentProtocol.A2AJsonRpc);

builder.AddProject<Projects.ResponsesAgent>("weather-responses-agent")
    .WithHttpEndpoint(env: "PORT", targetPort: 8080)
    .WithReference(chat)
    .WithReference(a2aAgent)
    .WaitFor(chat)
    .AsAgent(AgentProtocol.Responses);

builder.AddExecutable(
        "agent-env-dump",
        "sh",
        ".",
        "-c",
        "echo WEATHER_A2A_AGENT_AGENTCARD_URL=$WEATHER_A2A_AGENT_AGENTCARD_URL && sleep 3600")
    .WithReference(a2aAgent).WaitFor(a2aAgent);

#if !SKIP_DASHBOARD_REFERENCE
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
