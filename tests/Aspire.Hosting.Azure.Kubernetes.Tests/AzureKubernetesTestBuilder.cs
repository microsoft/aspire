// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES002
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

internal static class AzureKubernetesTestBuilder
{
    public static IDistributedApplicationTestingBuilder Create(
        ITestOutputHelper outputHelper,
        TemporaryWorkspace workspace,
        DistributedApplicationOperation operation = DistributedApplicationOperation.Publish,
        string? step = WellKnownPipelineSteps.Publish,
        IDeploymentStateManager? deploymentStateManager = null,
        IResourceContainerImageManager? imageManager = null,
        IPipelineActivityReporter? activityReporter = null,
        IHelmRunner? helmRunner = null)
    {
        var builder = TestDistributedApplicationBuilder
            .Create(operation, workspace.Path, step: step)
            .WithTestAndResourceLogging(outputHelper);

        builder.Services.AddSingleton<IDeploymentStateManager>(deploymentStateManager ?? new InMemoryDeploymentStateManager());
        builder.Services.AddSingleton<IResourceContainerImageManager>(imageManager ?? new MockImageBuilder());
        builder.Services.AddSingleton<IPipelineActivityReporter>(activityReporter ?? new TestPipelineActivityReporter(outputHelper));
        builder.Services.AddSingleton<IHelmRunner>(helmRunner ?? new FakeHelmRunner());

        return builder;
    }
}