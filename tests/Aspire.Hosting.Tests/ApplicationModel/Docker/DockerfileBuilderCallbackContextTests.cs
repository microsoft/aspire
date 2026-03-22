// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001 // Type is for evaluation purposes only and is subject to change or removal in future updates

using Aspire.Hosting.ApplicationModel.Docker;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests.ApplicationModel.Docker;

[Trait("Partition", "4")]
public class DockerfileBuilderCallbackContextTests
{
    [Fact]
    public void DockerfileBuilderCallbackContext_Constructor_SetsProperties()
    {
        // Arrange
        var resource = new ContainerResource("test");
        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();

        // Act
        var context = new DockerfileBuilderCallbackContext(resource, builder, services, CancellationToken.None);

        // Assert
        Assert.Same(resource, context.Resource);
        Assert.Same(builder, context.Builder);
        Assert.Same(services, context.Services);
    }

    [Fact]
    public void DockerfileBuilderCallbackContext_Constructor_ThrowsOnNullResource()
    {
        // Arrange
        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DockerfileBuilderCallbackContext(null!, builder, services, CancellationToken.None));
    }

    [Fact]
    public void DockerfileBuilderCallbackContext_Constructor_ThrowsOnNullBuilder()
    {
        // Arrange
        var resource = new ContainerResource("test");
        var services = new ServiceCollection().BuildServiceProvider();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DockerfileBuilderCallbackContext(resource, null!, services, CancellationToken.None));
    }

    [Fact]
    public void DockerfileBuilderCallbackContext_Constructor_ThrowsOnNullServices()
    {
        // Arrange
        var resource = new ContainerResource("test");
        var builder = new DockerfileBuilder();

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new DockerfileBuilderCallbackContext(resource, builder, null!, CancellationToken.None));
    }

    [Fact]
    public void DockerfileBuilderCallbackContext_Properties_AreReadOnly()
    {
        // Arrange
        var resource = new ContainerResource("test");
        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = new DockerfileBuilderCallbackContext(resource, builder, services, CancellationToken.None);

        // Act & Assert - Properties should have getters only
        Assert.Same(resource, context.Resource);
        Assert.Same(builder, context.Builder);
        Assert.Same(services, context.Services);

        // Verify properties are read-only by checking type info
        var properties = typeof(DockerfileBuilderCallbackContext).GetProperties();
        foreach (var property in properties)
        {
            Assert.True(property.CanRead, $"Property {property.Name} should be readable");
            Assert.False(property.CanWrite, $"Property {property.Name} should be read-only");
        }
    }

    [Fact]
    public void DockerfileBuilderCallbackContext_Builder_CanBeUsedToModifyDockerfile()
    {
        // Arrange
        var resource = new ContainerResource("test");
        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = new DockerfileBuilderCallbackContext(resource, builder, services, CancellationToken.None);

        // Act
        context.Builder.From("node:18")
            .WorkDir("/app")
            .Run("npm install");

        // Assert
        Assert.Single(context.Builder.Stages);
        Assert.Equal(3, context.Builder.Stages[0].Statements.Count); // FROM + WORKDIR + RUN
    }

    [Fact]
    public void DockerfileBuilderCallbackContext_Services_CanBeUsedForDependencyInjection()
    {
        // Arrange
        var resource = new ContainerResource("test");
        var services = new ServiceCollection()
            .AddSingleton<string>("test-service")
            .BuildServiceProvider();
        var builder = new DockerfileBuilder();
        var context = new DockerfileBuilderCallbackContext(resource, builder, services, CancellationToken.None);

        // Act
        var retrievedService = context.Services.GetService<string>();

        // Assert
        Assert.Equal("test-service", retrievedService);
    }

    [Fact]
    public void DockerfileBuilderCallbackContext_Resource_CanBeAccessedInCallback()
    {
        // Arrange
        var resource = new ContainerResource("mycontainer");
        var builder = new DockerfileBuilder();
        var services = new ServiceCollection().BuildServiceProvider();
        var context = new DockerfileBuilderCallbackContext(resource, builder, services, CancellationToken.None);

        // Act & Assert
        Assert.Equal("mycontainer", context.Resource.Name);
        Assert.IsAssignableFrom<IResource>(context.Resource);
    }
}