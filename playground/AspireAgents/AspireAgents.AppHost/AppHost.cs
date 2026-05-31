// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Agents;
using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddFoundry("agents-foundry");
var project = foundry.AddProject("agentsproject");
var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41Mini);

var a2aAgent = builder.AddUvicornApp("a2a-jsonrpc-agent", "../weather-agent-python", "weather_agent_python.main:app")
    .WithUv()
    .WithReference(project)
    .WithReference(chat)
    .WaitFor(chat)
    .AsAgent(AgentProtocol.A2AJsonRpc);

builder.AddProject<Projects.ResponsesAgent>("responses-agent")
    .WithHttpEndpoint(env: "PORT")
    .WithReference(chat)
    .WithReference(a2aAgent)
    .WaitFor(chat)
    .AsAgent(AgentProtocol.Responses, AgentProtocol.Mcp);

builder.AddProject<Projects.McpAgent>("mcp-agent")
    .WithHttpEndpoint(env: "PORT")
    .WithReference(chat)
    .WaitFor(chat)
    .AsAgent(AgentProtocol.Mcp);

builder.AddUvicornApp("ag-ui-agent", "../ag-ui-agent-python", "ag_ui_agent.main:app")
    .WithUv()
    .WithReference(project)
    .WithReference(chat)
    .WaitFor(chat)
    .AsAgent(AgentProtocol.AgUi);

builder.AddUvicornApp("acp-agent", "../acp-agent-python", "acp_agent.main:app")
    .WithUv()
    .WithReference(project)
    .WithReference(chat)
    .WaitFor(chat)
    .AsAgent(AgentProtocol.Acp);

builder.AddExecutable(
        "agent-env-dump",
        "sh",
        ".",
        "-c",
        "echo A2A_JSONRPC_AGENT_AGENTCARD_URL=$A2A_JSONRPC_AGENT_AGENTCARD_URL && sleep 3600")
    .WithReference(a2aAgent).WaitFor(a2aAgent);

#if !SKIP_DASHBOARD_REFERENCE
builder.AddProject<Projects.Aspire_Dashboard>(KnownResourceNames.AspireDashboard);
#endif

builder.Build().Run();
