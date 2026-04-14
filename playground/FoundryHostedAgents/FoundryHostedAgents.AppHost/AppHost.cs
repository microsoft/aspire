// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddFoundry("aif-myfoundry");
var project = foundry.AddProject("proj-myproject");
var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41);

builder.AddPythonApp("weather-hosted-agent", "../app", "main.py")
    .WithUv()
    .WithReference(chat).WaitFor(chat)
    .PublishAsHostedAgent(project);

builder.AddProject<Projects.DotNetHostedAgent>("proj-dotnet-hosted-agent")
    .WithReference(chat).WaitFor(chat)
    .PublishAsHostedAgent(project)
    .WithEndpoint("http", e => e.TargetPort = 9000);

builder.Build().Run();
