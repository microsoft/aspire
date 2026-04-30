#pragma warning disable ASPIREAZURE001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureEnvironmentResourceExtensionsTests
{
    [Fact]
    public void AddAzureEnvironment_ShouldAddResourceToBuilder_InPublishMode()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var resourceBuilder = builder.AddAzureEnvironment();

        // Assert
        Assert.NotNull(resourceBuilder);
        var environmentResource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        // Assert that default Location and ResourceGroup parameters are set
        Assert.NotNull(environmentResource.Location);
        Assert.NotNull(environmentResource.ResourceGroupName);
        // Assert that the parameters are not added to the resource model
        Assert.Empty(builder.Resources.OfType<ParameterResource>());
    }

    [Fact]
    public void AddAzureEnvironment_CalledMultipleTimes_ReturnsSameResource()
    {
        // Arrange
        var builder = CreateBuilder();

        // Act
        var firstBuilder = builder.AddAzureEnvironment();
        var secondBuilder = builder.AddAzureEnvironment();

        // Assert
        Assert.Same(firstBuilder.Resource, secondBuilder.Resource);
        Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
    }

    [Fact]
    public void AddAzureEnvironment_InRunMode_AddsHiddenResourceToModel()
    {
        var builder = CreateBuilder(isRunMode: true);

        var resourceBuilder = builder.AddAzureEnvironment();

        Assert.NotNull(resourceBuilder);
        var resource = Assert.Single(builder.Resources.OfType<AzureEnvironmentResource>());
        Assert.True(resource.TryGetLastAnnotation<ResourceSnapshotAnnotation>(out var snapshot));
        Assert.True(snapshot.InitialSnapshot.IsHidden);
    }

    [Fact]
    public void AddAzureEnvironment_CreatesDefaultName()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);

        // Act
        builder.AddAzureEnvironment();

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.StartsWith("azure", resource.Name);
    }

    [Fact]
    public void WithLocation_ShouldSetLocationProperty()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);
        var resourceBuilder = builder.AddAzureEnvironment();
        var expectedLocation = builder.AddParameter("location", "eastus2");

        // Act
        resourceBuilder.WithLocation(expectedLocation);

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal(expectedLocation.Resource, resource.Location);
    }

    [Fact]
    public void WithResourceGroup_ShouldSetResourceGroupNameProperty()
    {
        // Arrange
        var builder = CreateBuilder(isRunMode: false);
        var resourceBuilder = builder.AddAzureEnvironment();
        var expectedResourceGroup = builder.AddParameter("resourceGroupName", "my-resource-group");

        // Act
        resourceBuilder.WithResourceGroup(expectedResourceGroup);

        // Assert
        var resource = builder.Resources.OfType<AzureEnvironmentResource>().Single();
        Assert.Equal(expectedResourceGroup.Resource, resource.ResourceGroupName);
    }

    private static IDistributedApplicationBuilder CreateBuilder(bool isRunMode = false)
    {
        var operation = isRunMode ? DistributedApplicationOperation.Run : DistributedApplicationOperation.Publish;
        return TestDistributedApplicationBuilder.Create(operation);
    }
}
