// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var tenantId = builder.AddParameterFromConfiguration("tenant", "Azure:TenantId");

var foundry = builder.AddFoundry("aif-myfoundry");
var project = foundry.AddProject("proj-myproject");
var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41);

builder.AddPythonApp("weather-hosted-agent", "../app", "main.py")
    .WithUv()
    .WithReference(project).WaitFor(project)
    .WithReference(chat).WaitFor(chat)
    .WithEnvironment("AZURE_TENANT_ID", tenantId)
    .PublishAsHostedAgent(project);

builder.AddProject<Projects.DotNetHostedAgent>("proj-dotnet-hosted-agent")
    .WithReference(chat).WaitFor(chat)
    .PublishAsHostedAgent()
    .WithEndpoint("http", e => e.TargetPort = 9000);

builder.Build().Run();
