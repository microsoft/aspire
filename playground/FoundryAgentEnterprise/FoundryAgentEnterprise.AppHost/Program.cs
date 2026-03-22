// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Foundry;

var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddFoundry("my-foundry");
var deployment = foundry.AddDeployment("my-gpt-5", FoundryModel.OpenAI.Gpt5)
    .WithProperties(d => d.SkuCapacity = 10);
var project = foundry.AddProject("my-foundry-proj");

project.WithKeyVault(builder.AddAzureKeyVault("foundry-kv"));

project.AddCapabilityHost("capability-host")
    .WithCosmosDB(builder.AddAzureCosmosDB("foundry-db"))
    .WithStorage(builder.AddAzureStorage("storage-account"))
    .WithSearch(builder.AddAzureSearch("foundry-search"))
    .WithAzureOpenAI(foundry);

var app = builder.AddUvicornApp("app", "./app", "main:app")
    .WithUv()
    .WithHttpEndpoint()
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithReference(project)
    .WithReference(deployment)
    .WaitFor(deployment)
    .PublishAsHostedAgent(project, (opts) =>
    {
        opts.Description = "Foundry Agent Basic Example";
        opts.Metadata["managed-by"] = "aspire-foundry";
        opts.Cpu = 2;
        opts.Memory = 8;
    });

// var frontend = builder.AddViteApp("frontend", "./frontend")
//     .WithReference(app)
//     .WaitFor(app);

// app.PublishWithContainerFiles(frontend, "./static");

builder.Build().Run();
