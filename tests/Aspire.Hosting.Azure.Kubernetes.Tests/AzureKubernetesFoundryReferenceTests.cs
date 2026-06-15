// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesFoundryReferenceTests
{
    [Fact]
    public async Task EndpointReferenceToFoundryHostedAgentIsResolvedAcrossComputeEnvironments()
    {
        using var tempDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(
            DistributedApplicationOperation.Publish,
            tempDir.Path);

        builder.Services.AddSingleton<IResourceContainerImageManager, MockImageBuilder>();

        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var project = builder.AddFoundry("foundry")
            .AddProject("project");

        // The agent app is deployed to the Foundry project compute environment via AsHostedAgent.
        var agent = builder.AddProject<Project>("agent", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints();
        agent.AsHostedAgent(project);

        // The web app is deployed to Azure Kubernetes and references the Foundry hosted agent.
        // The Kubernetes publisher must delegate endpoint resolution to the Foundry compute
        // environment rather than looking the agent up in its own (local) endpoint map, which
        // does not contain the cross-environment agent. See issue #17749.
        // WithReference(agent) exercises the bare EndpointReference branch; the explicit
        // Property(Url) environment variable exercises the EndpointReferenceExpression branch.
        builder.AddProject<Project>("web", launchProfileName: null)
            .WithHttpEndpoint()
            .WithExternalHttpEndpoints()
            .WithComputeEnvironment(aks)
            .WithReference(agent)
            .WithEnvironment("AGENT_URL", agent.GetEndpoint("http").Property(EndpointProperty.Url));

        await using var app = builder.Build();
        await app.RunAsync();

        // The resolved environment variable values are emitted into the Helm chart's values.yaml.
        // The agent endpoint must resolve to the Foundry project endpoint composed with the deployed
        // hosted agent path because hosted-agent deployment creates the Foundry agent version with the
        // wrapper resource name.
        var valuesPath = Directory.EnumerateFiles(tempDir.Path, "values.yaml", SearchOption.AllDirectories).Single();
        var values = await File.ReadAllTextAsync(valuesPath);

        Assert.Contains("AGENT_HTTP: \"{project.outputs.endpoint}/agents/agent-ha\"", values);
        Assert.Contains("AGENT_URL: \"{project.outputs.endpoint}/agents/agent-ha\"", values);
        Assert.Contains("services__agent__http__0: \"{project.outputs.endpoint}/agents/agent-ha\"", values);
    }

    private sealed class Project : IProjectMetadata
    {
        public string ProjectPath => "project";
    }
}
