// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOSMOSDB001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureRunAsEmulatorModeTests
{
    public static TheoryData<string, Func<IDistributedApplicationBuilder, Action, IResource>> RunAsEmulatorResources => new()
    {
        { "Azure App Configuration", (builder, configure) => builder.AddAzureAppConfiguration("appconfig").RunAsEmulator(_ => configure()).Resource },
        { "Azure Cosmos DB", (builder, configure) => builder.AddAzureCosmosDB("cosmos").RunAsEmulator(_ => configure()).Resource },
        { "Azure Cosmos DB preview", (builder, configure) => builder.AddAzureCosmosDB("cosmos").RunAsPreviewEmulator(_ => configure()).Resource },
        { "Azure Event Hubs", (builder, configure) => builder.AddAzureEventHubs("eventhubs").RunAsEmulator(_ => configure()).Resource },
        { "Azure Kusto", (builder, configure) => builder.AddAzureKustoCluster("kusto").RunAsEmulator(_ => configure()).Resource },
        { "Azure Service Bus", (builder, configure) => builder.AddAzureServiceBus("servicebus").RunAsEmulator(_ => configure()).Resource },
        { "Azure SignalR", (builder, configure) => builder.AddAzureSignalR("signalr").RunAsEmulator(_ => configure()).Resource },
        { "Azure Storage", (builder, configure) => builder.AddAzureStorage("storage").RunAsEmulator(_ => configure()).Resource },
    };

    [Theory]
    [MemberData(nameof(RunAsEmulatorResources))]
    public void RunAsEmulator_InRunMode_ConfiguresLocalContainer(string resourceType, Func<IDistributedApplicationBuilder, Action, IResource> addResource)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var callbackInvoked = false;

        var resource = addResource(builder, () => callbackInvoked = true);

        Assert.True(callbackInvoked);
        Assert.True(resource.IsEmulator() || resource.IsContainer(), $"{resourceType} should be configured as an emulator or local container in run mode.");
        Assert.Contains(builder.Resources, resource => resource.Annotations.OfType<ContainerImageAnnotation>().Any());
    }

    [Theory]
    [MemberData(nameof(RunAsEmulatorResources))]
    public void RunAsEmulator_InPublishMode_DoesNotConfigureLocalContainer(string resourceType, Func<IDistributedApplicationBuilder, Action, IResource> addResource)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var callbackInvoked = false;

        var resource = addResource(builder, () => callbackInvoked = true);

        Assert.False(callbackInvoked);
        Assert.False(resource.IsEmulator(), $"{resourceType} should not be configured as an emulator in publish mode.");
        Assert.False(resource.IsContainer(), $"{resourceType} should not be configured as a local container in publish mode.");
        Assert.DoesNotContain(builder.Resources, resource => resource.Annotations.OfType<ContainerImageAnnotation>().Any());
    }
}
