// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class ResourceWithAzureFunctionsConfigTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AzureStorageResource_ImplementsIResourceWithAzureFunctionsConfig()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var storageResource = builder.AddAzureStorage("storage").Resource;

        // Act & Assert
        Assert.IsAssignableFrom<IResourceWithAzureFunctionsConfig>(storageResource);
    }

    [Fact]
    public void AzureBlobStorageResource_ImplementsIResourceWithAzureFunctionsConfig()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var storageResource = builder.AddAzureStorage("storage");
        var blobResource = storageResource.AddBlobs("blobs").Resource;

        // Act & Assert
        Assert.IsAssignableFrom<IResourceWithAzureFunctionsConfig>(blobResource);
    }

    [Fact]
    public void AzureQueueStorageResource_ImplementsIResourceWithAzureFunctionsConfig()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var storageResource = builder.AddAzureStorage("storage");
        var queueResource = storageResource.AddQueues("queues").Resource;

        // Act & Assert
        Assert.IsAssignableFrom<IResourceWithAzureFunctionsConfig>(queueResource);
    }

    [Fact]
    public void AzureCosmosDBResource_ImplementsIResourceWithAzureFunctionsConfig()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos").Resource;

        // Act & Assert
        Assert.IsAssignableFrom<IResourceWithAzureFunctionsConfig>(cosmosResource);
    }

    [Fact]
    public void AzureCosmosDBDatabaseResource_ImplementsIResourceWithAzureFunctionsConfig()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos");
        var dbResource = cosmosResource.AddCosmosDatabase("database").Resource;

        // Act & Assert
        Assert.IsAssignableFrom<IResourceWithAzureFunctionsConfig>(dbResource);
    }

    [Fact]
    public void AzureCosmosDBContainerResource_ImplementsIResourceWithAzureFunctionsConfig()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos");
        var dbResource = cosmosResource.AddCosmosDatabase("database");
        var containerResource = dbResource.AddContainer("container", "/id").Resource;

        // Act & Assert
        Assert.IsAssignableFrom<IResourceWithAzureFunctionsConfig>(containerResource);
    }

    [Fact]
    public void AzureEventHubsResource_ImplementsIResourceWithAzureFunctionsConfig()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var eventHubsResource = builder.AddAzureEventHubs("eventhubs").Resource;

        // Act & Assert
        Assert.IsAssignableFrom<IResourceWithAzureFunctionsConfig>(eventHubsResource);
    }

    [Fact]
    public void AzureServiceBusResource_ImplementsIResourceWithAzureFunctionsConfig()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var serviceBusResource = builder.AddAzureServiceBus("servicebus").Resource;

        // Act & Assert
        Assert.IsAssignableFrom<IResourceWithAzureFunctionsConfig>(serviceBusResource);
    }

    [Fact]
    public void AzureStorageEmulator_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator().Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)storage).ApplyAzureFunctionsConfiguration(target, "myconnection");

        // Assert
        Assert.True(target.ContainsKey("myconnection"));
        Assert.True(target.ContainsKey("Aspire__Azure__Storage__Blobs__myconnection__ConnectionString"));
        Assert.True(target.ContainsKey("Aspire__Azure__Storage__Queues__myconnection__ConnectionString"));
        Assert.True(target.ContainsKey("Aspire__Azure__Data__Tables__myconnection__ConnectionString"));
    }

    [Fact]
    public void AzureStorage_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var storage = builder.AddAzureStorage("storage").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)storage).ApplyAzureFunctionsConfiguration(target, "myconnection");

        // Assert
        Assert.True(target.ContainsKey("myconnection__blobServiceUri"));
        Assert.True(target.ContainsKey("myconnection__queueServiceUri"));
        Assert.True(target.ContainsKey("myconnection__tableServiceUri"));
        Assert.True(target.ContainsKey("Aspire__Azure__Storage__Blobs__myconnection__ServiceUri"));
        Assert.True(target.ContainsKey("Aspire__Azure__Storage__Queues__myconnection__ServiceUri"));
        Assert.True(target.ContainsKey("Aspire__Azure__Data__Tables__myconnection__ServiceUri"));
    }

    [Fact]
    public void AzureBlobStorage_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        var blobResource = storage.AddBlobs("blobs").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)blobResource).ApplyAzureFunctionsConfiguration(target, "myblobs");

        // Assert
        Assert.True(target.ContainsKey("myblobs"));
        Assert.True(target.ContainsKey("Aspire__Azure__Storage__Blobs__myblobs__ConnectionString"));
    }

    [Fact]
    public void AzureTableStorage_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        var tableResource = storage.AddTables("tables").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)tableResource).ApplyAzureFunctionsConfiguration(target, "mytables");

        // Assert
        Assert.True(target.ContainsKey("mytables"));
        Assert.True(target.ContainsKey("Aspire__Azure__Data__Tables__mytables__ConnectionString"));
    }

    [Fact]
    public void AzureQueueStorage_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var storage = builder.AddAzureStorage("storage").RunAsEmulator();
        var queueResource = storage.AddQueues("queues").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)queueResource).ApplyAzureFunctionsConfiguration(target, "myqueues");

        // Assert
        Assert.True(target.ContainsKey("myqueues"));
        Assert.True(target.ContainsKey("Aspire__Azure__Storage__Queues__myqueues__ConnectionString"));
    }

    [Fact]
    public void AzureCosmosDBEmulator_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos").RunAsEmulator().Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)cosmosResource).ApplyAzureFunctionsConfiguration(target, "mycosmosdb");

        // Assert
        Assert.True(target.ContainsKey("mycosmosdb"));
        Assert.True(target.ContainsKey("Aspire__Microsoft__Azure__Cosmos__mycosmosdb__ConnectionString"));
    }

    [Fact]
    public void AzureCosmosDB_WithAccessKey_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos").WithAccessKeyAuthentication().Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)cosmosResource).ApplyAzureFunctionsConfiguration(target, "mycosmosdb");

        // Assert
        Assert.True(target.ContainsKey("mycosmosdb"));
        Assert.True(target.ContainsKey("Aspire__Microsoft__Azure__Cosmos__mycosmosdb__ConnectionString"));
    }

    [Fact]
    public void AzureCosmosDB_WithEntraID_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)cosmosResource).ApplyAzureFunctionsConfiguration(target, "mycosmosdb");

        // Assert
        Assert.True(target.ContainsKey("mycosmosdb__accountEndpoint"));
        Assert.True(target.ContainsKey("Aspire__Microsoft__Azure__Cosmos__mycosmosdb__AccountEndpoint"));
    }

    [Fact]
    public void AzureCosmosDBDatabase_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos");
        var dbResource = cosmosResource.AddCosmosDatabase("database").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)dbResource).ApplyAzureFunctionsConfiguration(target, "cosmosdb");

        // Assert
        Assert.True(target.ContainsKey("cosmosdb__accountEndpoint"));
        var targetReferenceExpression = Assert.IsType<ReferenceExpression>(target["cosmosdb__accountEndpoint"]);
        Assert.Equal(cosmosResource.Resource.ConnectionStringExpression.ValueExpression, targetReferenceExpression.ValueExpression);
        Assert.True(target.ContainsKey("Aspire__Microsoft__Azure__Cosmos__cosmosdb__AccountEndpoint"));
        targetReferenceExpression = Assert.IsType<ReferenceExpression>(target["Aspire__Microsoft__Azure__Cosmos__cosmosdb__AccountEndpoint"]);
        Assert.Equal(cosmosResource.Resource.ConnectionStringExpression.ValueExpression, targetReferenceExpression.ValueExpression);
        // Validate DatabaseName for non-EF settings
        Assert.Equal(dbResource.DatabaseName, target["Aspire__Microsoft__Azure__Cosmos__cosmosdb__DatabaseName"]);
        // Validate DatabaseName for EF settings
        Assert.Equal(dbResource.DatabaseName, target["Aspire__Microsoft__EntityFrameworkCore__Cosmos__cosmosdb__DatabaseName"]);
    }

    [Fact]
    public void AzureCosmosDBContainer_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos");
        var containerResource = cosmosResource.AddCosmosDatabase("database").AddContainer("container", "/partitionKey").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)containerResource).ApplyAzureFunctionsConfiguration(target, "cosmosdb");

        // Assert
        Assert.True(target.ContainsKey("cosmosdb__accountEndpoint"));
        var targetReferenceExpression = Assert.IsType<ReferenceExpression>(target["cosmosdb__accountEndpoint"]);
        Assert.Equal(targetReferenceExpression.ValueExpression, cosmosResource.Resource.ConnectionStringExpression.ValueExpression);
        Assert.True(target.ContainsKey("Aspire__Microsoft__Azure__Cosmos__cosmosdb__AccountEndpoint"));
        targetReferenceExpression = Assert.IsType<ReferenceExpression>(target["Aspire__Microsoft__Azure__Cosmos__cosmosdb__AccountEndpoint"]);
        Assert.Equal(targetReferenceExpression.ValueExpression, cosmosResource.Resource.ConnectionStringExpression.ValueExpression);
        // Validate DatabaseName and ContainerName for non-EF settings
        Assert.Equal(target["Aspire__Microsoft__Azure__Cosmos__cosmosdb__DatabaseName"], containerResource.Parent.DatabaseName);
        Assert.Equal(target["Aspire__Microsoft__Azure__Cosmos__cosmosdb__ContainerName"], containerResource.ContainerName);
        // Validate DatabaseName and ContainerName for EF settings
        Assert.Equal(target["Aspire__Microsoft__EntityFrameworkCore__Cosmos__cosmosdb__DatabaseName"], containerResource.Parent.DatabaseName);
        Assert.Equal(target["Aspire__Microsoft__EntityFrameworkCore__Cosmos__cosmosdb__ContainerName"], containerResource.ContainerName);
    }

    [Fact]
    public void AzureCosmosDBDatabaseEmulator_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos")
            .RunAsEmulator();
        var dbResource = cosmosResource.AddCosmosDatabase("database").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)dbResource).ApplyAzureFunctionsConfiguration(target, "cosmosdb");

        // Assert
        Assert.True(target.ContainsKey("cosmosdb"));
        var targetReferenceExpression = Assert.IsType<ReferenceExpression>(target["cosmosdb"]);
        Assert.Equal(targetReferenceExpression.ValueExpression, cosmosResource.Resource.ConnectionStringExpression.ValueExpression);
        Assert.True(target.ContainsKey("Aspire__Microsoft__Azure__Cosmos__cosmosdb__ConnectionString"));
        targetReferenceExpression = Assert.IsType<ReferenceExpression>(target["Aspire__Microsoft__Azure__Cosmos__cosmosdb__ConnectionString"]);
        Assert.Equal(targetReferenceExpression.ValueExpression, dbResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AzureCosmosDBContainerEmulator_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var cosmosResource = builder.AddAzureCosmosDB("cosmos")
            .RunAsEmulator();
        var containerResource = cosmosResource.AddCosmosDatabase("database").AddContainer("container", "/partitionKey").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)containerResource).ApplyAzureFunctionsConfiguration(target, "cosmosdb");

        // Assert
        Assert.True(target.ContainsKey("cosmosdb"));
        var targetReferenceExpression = Assert.IsType<ReferenceExpression>(target["cosmosdb"]);
        Assert.Equal(targetReferenceExpression.ValueExpression, cosmosResource.Resource.ConnectionStringExpression.ValueExpression);
        Assert.True(target.ContainsKey("Aspire__Microsoft__Azure__Cosmos__cosmosdb__ConnectionString"));
        targetReferenceExpression = Assert.IsType<ReferenceExpression>(target["Aspire__Microsoft__Azure__Cosmos__cosmosdb__ConnectionString"]);
        Assert.Equal(targetReferenceExpression.ValueExpression, containerResource.ConnectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AzureEventHubsEmulator_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var eventHubsResource = builder.AddAzureEventHubs("eventhubs").RunAsEmulator().Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)eventHubsResource).ApplyAzureFunctionsConfiguration(target, "myeventhubs");

        // Assert
        Assert.True(target.ContainsKey("myeventhubs"));
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__myeventhubs__ConnectionString", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__myeventhubs__ConnectionString", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__EventProcessorClient__myeventhubs__ConnectionString", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__PartitionReceiver__myeventhubs__ConnectionString", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__myeventhubs__ConnectionString", target.Keys);
    }

    [Fact]
    public void AzureEventHubs_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var eventHubsResource = builder.AddAzureEventHubs("eventhubs").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)eventHubsResource).ApplyAzureFunctionsConfiguration(target, "myeventhubs");

        // Assert
        Assert.True(target.ContainsKey("myeventhubs__fullyQualifiedNamespace"));
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__EventHubProducerClient__myeventhubs__FullyQualifiedNamespace", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__EventHubConsumerClient__myeventhubs__FullyQualifiedNamespace", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__EventProcessorClient__myeventhubs__FullyQualifiedNamespace", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__PartitionReceiver__myeventhubs__FullyQualifiedNamespace", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__EventHubs__EventHubBufferedProducerClient__myeventhubs__FullyQualifiedNamespace", target.Keys);
    }

    [Fact]
    public void AzureServiceBus_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var serviceBusResource = builder.AddAzureServiceBus("servicebus").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)serviceBusResource).ApplyAzureFunctionsConfiguration(target, "myservicebus");

        // Assert
        Assert.True(target.ContainsKey("myservicebus__fullyQualifiedNamespace"));
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__myservicebus__FullyQualifiedNamespace", target.Keys);
    }

    [Fact]
    public void AzureServiceBusQueue_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var serviceBusResource = builder.AddAzureServiceBus("servicebus");
        var queueResource = serviceBusResource.AddServiceBusQueue("ordersqueue").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)queueResource).ApplyAzureFunctionsConfiguration(target, "myqueue");

        // Assert
        Assert.True(target.ContainsKey("myqueue__fullyQualifiedNamespace"));
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__myqueue__FullyQualifiedNamespace", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__myqueue__QueueOrTopicName", target.Keys);
        Assert.Equal("ordersqueue", target["Aspire__Azure__Messaging__ServiceBus__myqueue__QueueOrTopicName"]);
    }

    [Fact]
    public void AzureServiceBusQueueEmulator_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var serviceBusResource = builder.AddAzureServiceBus("servicebus").RunAsEmulator();
        var queueResource = serviceBusResource.AddServiceBusQueue("ordersqueue").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)queueResource).ApplyAzureFunctionsConfiguration(target, "myqueue");

        // Assert
        Assert.True(target.ContainsKey("myqueue"));
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__myqueue__ConnectionString", target.Keys);

        var connectionStringExpression = Assert.IsType<ReferenceExpression>(target["Aspire__Azure__Messaging__ServiceBus__myqueue__ConnectionString"]);
        Assert.Equal(queueResource.ConnectionStringExpression.ValueExpression, connectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AzureServiceBusTopic_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var serviceBusResource = builder.AddAzureServiceBus("servicebus");
        var topicResource = serviceBusResource.AddServiceBusTopic("notificationstopic").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)topicResource).ApplyAzureFunctionsConfiguration(target, "mytopic");

        // Assert
        Assert.True(target.ContainsKey("mytopic__fullyQualifiedNamespace"));
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__mytopic__FullyQualifiedNamespace", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__mytopic__QueueOrTopicName", target.Keys);
        Assert.Equal("notificationstopic", target["Aspire__Azure__Messaging__ServiceBus__mytopic__QueueOrTopicName"]);
    }

    [Fact]
    public void AzureServiceBusTopicEmulator_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var serviceBusResource = builder.AddAzureServiceBus("servicebus").RunAsEmulator();
        var topicResource = serviceBusResource.AddServiceBusTopic("notificationstopic").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)topicResource).ApplyAzureFunctionsConfiguration(target, "mytopic");

        // Assert
        Assert.True(target.ContainsKey("mytopic"));
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__mytopic__ConnectionString", target.Keys);

        var connectionStringExpression = Assert.IsType<ReferenceExpression>(target["Aspire__Azure__Messaging__ServiceBus__mytopic__ConnectionString"]);
        Assert.Equal(topicResource.ConnectionStringExpression.ValueExpression, connectionStringExpression.ValueExpression);
    }

    [Fact]
    public void AzureServiceBusSubscription_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, testOutputHelper);
        var serviceBusResource = builder.AddAzureServiceBus("servicebus");
        var topicResource = serviceBusResource.AddServiceBusTopic("notificationstopic");
        var subscriptionResource = topicResource.AddServiceBusSubscription("usersubscription").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)subscriptionResource).ApplyAzureFunctionsConfiguration(target, "mysub");

        // Assert
        Assert.True(target.ContainsKey("mysub__fullyQualifiedNamespace"));
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__mysub__FullyQualifiedNamespace", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__mysub__QueueOrTopicName", target.Keys);
        Assert.Contains("Aspire__Azure__Messaging__ServiceBus__mysub__SubscriptionName", target.Keys);
        Assert.Equal("notificationstopic", target["Aspire__Azure__Messaging__ServiceBus__mysub__QueueOrTopicName"]);
        Assert.Equal("usersubscription", target["Aspire__Azure__Messaging__ServiceBus__mysub__SubscriptionName"]);
    }

    [Fact]
    public void AzureServiceBusSubscriptionEmulator_AppliesCorrectConfigurationFormat()
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var serviceBusResource = builder.AddAzureServiceBus("servicebus").RunAsEmulator();
        var topicResource = serviceBusResource.AddServiceBusTopic("notificationstopic");
        var subscriptionResource = topicResource.AddServiceBusSubscription("usersubscription").Resource;
        var target = new Dictionary<string, object>();

        // Act
        ((IResourceWithAzureFunctionsConfig)subscriptionResource).ApplyAzureFunctionsConfiguration(target, "mysub");

        // Insert settings for both Functions host and Aspire client integrations
        Assert.True(target.ContainsKey("mysub"));
        Assert.True(target.ContainsKey("Aspire__Azure__Messaging__ServiceBus__mysub__ConnectionString"));

        // Aspire client integration should get the connection string of the child resource
        var connectionStringExpression = Assert.IsType<ReferenceExpression>(target["Aspire__Azure__Messaging__ServiceBus__mysub__ConnectionString"]);
        Assert.Equal(subscriptionResource.ConnectionStringExpression.ValueExpression, connectionStringExpression.ValueExpression);

        connectionStringExpression = Assert.IsType<ReferenceExpression>(target["mysub"]);
        Assert.Equal(serviceBusResource.Resource.ConnectionStringExpression.ValueExpression, connectionStringExpression.ValueExpression);
    }
}
