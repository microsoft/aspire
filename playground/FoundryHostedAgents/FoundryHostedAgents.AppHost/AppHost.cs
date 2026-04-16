// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Foundry;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.CognitiveServices;
using Azure.Provisioning.Expressions;

var builder = DistributedApplication.CreateBuilder(args);

var foundry = builder.AddFoundry("aif-myfoundry");
var project = foundry.AddProject("proj-myproject")
    // workaround for https://github.com/microsoft/aspire/issues/15971
    .ConfigureInfrastructure(infra =>
    {
        var project = infra.GetProvisionableResources().OfType<CognitiveServicesProject>().Single();

        var foundryAccount = foundry.Resource.AddAsExistingResource(infra);

        var cogUserRa = foundryAccount.CreateRoleAssignment(CognitiveServicesBuiltInRole.CognitiveServicesUser, RoleManagementPrincipalType.ServicePrincipal, project.Identity.PrincipalId);
        // There's a bug in the CDK, see https://github.com/Azure/azure-sdk-for-net/issues/47265
        cogUserRa.Name = BicepFunction.CreateGuid(foundryAccount.Id, project.Id, cogUserRa.RoleDefinitionId);
        infra.Add(cogUserRa);
    });
var chat = project.AddModelDeployment("chat", FoundryModel.OpenAI.Gpt41);

builder.AddPythonApp("weather-hosted-agent", "../app", "main.py")
    .WithUv()
    .WithReference(chat).WaitFor(chat)
    .PublishAsHostedAgent(project);

builder.AddProject<Projects.DotNetHostedAgent>("proj-dotnet-hosted-agent")
    .WithHttpEndpoint(targetPort: 9000)
    .WithReference(chat).WaitFor(chat)
    .PublishAsHostedAgent(project);

builder.Build().Run();
