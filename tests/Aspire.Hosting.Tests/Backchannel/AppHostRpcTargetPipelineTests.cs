// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE004
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests.Backchannel;

public class AppHostRpcTargetPipelineTests
{
    [Fact]
    public async Task GetPipelineStepsAsync_DoesNotRepresentConcurrencyGroupsAsDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        AddGroupedDeploymentTargets(builder);

        using var app = builder.Build();
        var response = await app.Services.GetRequiredService<AppHostRpcTarget>().GetPipelineStepsAsync();

        var firstStep = Assert.Single(response.Steps, step => step.Name == "provision-target1");
        var secondStep = Assert.Single(response.Steps, step => step.Name == "provision-target2");

        Assert.Empty(firstStep.DependsOn);
        Assert.Empty(secondStep.DependsOn);
        Assert.True(Array.IndexOf(response.Steps, firstStep) < Array.IndexOf(response.Steps, secondStep));
    }

    [Fact]
    public async Task GetPipelineStepsAsync_TargetedConcurrencyGroupMemberDoesNotAddDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        AddGroupedDeploymentTargets(builder);

        using var app = builder.Build();
        var response = await app.Services.GetRequiredService<AppHostRpcTarget>().GetPipelineStepsAsync(
            new GetPipelineStepsRequest { Step = "provision-target2" });

        var step = Assert.Single(response.Steps);
        Assert.Equal("provision-target2", step.Name);
        Assert.Empty(step.DependsOn);
    }

    private static void AddGroupedDeploymentTargets(IDistributedApplicationBuilder builder)
    {
        var group = new DeploymentConcurrencyGroup();
        AddDeploymentTarget("app1", "target1");
        AddDeploymentTarget("app2", "target2");

        void AddDeploymentTarget(string resourceName, string targetName)
        {
            var target = new ContainerResource(targetName);
            var deploymentTarget = new DeploymentTargetAnnotation(target);
            target.Annotations.Add(new DeploymentConcurrencyGroupAnnotation(group));

            var resource = builder.AddContainer(resourceName, "myimage").Resource;
            resource.Annotations.Add(deploymentTarget);
            resource.Annotations.Add(new PipelineStepAnnotation(_ => new PipelineStep
            {
                Name = $"provision-{targetName}",
                Resource = target,
                Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
                Action = _ => Task.CompletedTask
            }));
        }
    }
}
