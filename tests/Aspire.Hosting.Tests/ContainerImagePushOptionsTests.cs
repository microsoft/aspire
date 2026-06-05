// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

namespace Aspire.Hosting.Tests;

public class ContainerImagePushOptionsTests
{
    [Fact]
    public async Task GetFullRemoteImageNameAsync_SimpleImageName_UsesRegistryEndpoint()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "myapp",
            RemoteImageTag = "v1.0.0"
        };

        var registry = new FakeContainerRegistry("myregistry", "myregistry.azurecr.io");

        // Act
        var result = await options.GetFullRemoteImageNameAsync(registry);

        // Assert
        Assert.Equal("myregistry.azurecr.io/myapp:v1.0.0", result);
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_SimpleImageName_WithRepository_UsesRegistryEndpointAndRepository()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "myapp",
            RemoteImageTag = "v1.0.0"
        };

        var registry = new FakeContainerRegistry("myregistry", "docker.io", "captainsafia");

        // Act
        var result = await options.GetFullRemoteImageNameAsync(registry);

        // Assert
        Assert.Equal("docker.io/captainsafia/myapp:v1.0.0", result);
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_ImageNameWithRepository_OverridesRegistryRepository()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "myorg/myapp",
            RemoteImageTag = "latest"
        };

        var registry = new FakeContainerRegistry("myregistry", "docker.io", "captainsafia");

        // Act
        var result = await options.GetFullRemoteImageNameAsync(registry);

        // Assert
        // The repository in RemoteImageName is used as-is, registry's repository is still prepended
        Assert.Equal("docker.io/captainsafia/myorg/myapp:latest", result);
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_FullPathWithHost_OverridesRegistryEndpoint()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "ghcr.io/myuser/myrepo",
            RemoteImageTag = "v2.0.0"
        };

        var registry = new FakeContainerRegistry("myregistry", "docker.io", "captainsafia");

        // Act
        var result = await options.GetFullRemoteImageNameAsync(registry);

        // Assert
        // The host in RemoteImageName overrides the registry endpoint entirely
        Assert.Equal("ghcr.io/myuser/myrepo:v2.0.0", result);
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_LocalhostWithPort_OverridesRegistryEndpoint()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "localhost:5000/myapp",
            RemoteImageTag = "dev"
        };

        var registry = new FakeContainerRegistry("myregistry", "myregistry.azurecr.io");

        // Act
        var result = await options.GetFullRemoteImageNameAsync(registry);

        // Assert
        Assert.Equal("localhost:5000/myapp:dev", result);
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_Localhost_OverridesRegistryEndpoint()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "localhost/myapp",
            RemoteImageTag = "dev"
        };

        var registry = new FakeContainerRegistry("myregistry", "myregistry.azurecr.io");

        // Act
        var result = await options.GetFullRemoteImageNameAsync(registry);

        // Assert
        Assert.Equal("localhost/myapp:dev", result);
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_DefaultsToLatestTag()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "myapp",
            RemoteImageTag = null
        };

        var registry = new FakeContainerRegistry("myregistry", "myregistry.azurecr.io");

        // Act
        var result = await options.GetFullRemoteImageNameAsync(registry);

        // Assert
        Assert.Equal("myregistry.azurecr.io/myapp:latest", result);
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_EmptyTagDefaultsToLatest()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "myapp",
            RemoteImageTag = ""
        };

        var registry = new FakeContainerRegistry("myregistry", "myregistry.azurecr.io");

        // Act
        var result = await options.GetFullRemoteImageNameAsync(registry);

        // Assert
        Assert.Equal("myregistry.azurecr.io/myapp:latest", result);
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_ThrowsWhenRemoteImageNameIsNull()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = null,
            RemoteImageTag = "v1.0.0"
        };

        var registry = new FakeContainerRegistry("myregistry", "myregistry.azurecr.io");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => options.GetFullRemoteImageNameAsync(registry));
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_ThrowsWhenRemoteImageNameIsEmpty()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "",
            RemoteImageTag = "v1.0.0"
        };

        var registry = new FakeContainerRegistry("myregistry", "myregistry.azurecr.io");

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => options.GetFullRemoteImageNameAsync(registry));
    }

    [Fact]
    public async Task GetFullRemoteImageNameAsync_ThrowsWhenRegistryIsNull()
    {
        // Arrange
        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "myapp",
            RemoteImageTag = "v1.0.0"
        };

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(() => options.GetFullRemoteImageNameAsync(null!));
    }

    [Theory]
    [InlineData("docker.io/library/nginx", "docker.io", "library/nginx")]
    [InlineData("ghcr.io/user/repo", "ghcr.io", "user/repo")]
    [InlineData("myregistry.azurecr.io/myapp", "myregistry.azurecr.io", "myapp")]
    [InlineData("localhost:5000/myapp", "localhost:5000", "myapp")]
    [InlineData("localhost/myapp", "localhost", "myapp")]
    [InlineData("192.168.1.1:5000/myapp", "192.168.1.1:5000", "myapp")]
    public void ParseImageReference_WithHost_ReturnsHostAndPath(string input, string expectedHost, string expectedPath)
    {
        // Act
        var (host, path) = ContainerImagePushOptions.ParseImageReference(input);

        // Assert
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPath, path);
    }

    [Theory]
    [InlineData("myapp", null, "myapp")]
    [InlineData("myorg/myapp", null, "myorg/myapp")]
    [InlineData("myorg/subgroup/myapp", null, "myorg/subgroup/myapp")]
    public void ParseImageReference_WithoutHost_ReturnsNullHostAndFullPath(string input, string? expectedHost, string expectedPath)
    {
        // Act
        var (host, path) = ContainerImagePushOptions.ParseImageReference(input);

        // Assert
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPath, path);
    }

    private sealed class FakeContainerRegistry(string name, string endpoint, string? repository = null) : IContainerRegistry
    {
        private readonly string _name = name;
        private readonly string _endpoint = endpoint;
        private readonly string? _repository = repository;

        public ReferenceExpression Name => ReferenceExpression.Create($"{_name}");
        public ReferenceExpression Endpoint => ReferenceExpression.Create($"{_endpoint}");
        public ReferenceExpression? Repository => _repository is not null ? ReferenceExpression.Create($"{_repository}") : null;
    }
}
