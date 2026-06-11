// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Qdrant.Tests;

public class QdrantPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddQdrantShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "Qdrant";

        var action = () => builder.AddQdrant(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddQdrantShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddQdrant(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<QdrantServerResource> builder = null!;

        var action = () => builder.WithDataVolume();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<QdrantServerResource> builder = null!;
        const string source = "/qdrant/storage";

        var action = () => builder.WithDataBindMount(source);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataBindMountShouldThrowWhenSourceIsNullOrEmpty(bool isNull)
    {
        var builderResource = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var qdrant = builderResource.AddQdrant("Qdrant");
        var source = isNull ? null! : string.Empty;

        var action = () => qdrant.WithDataBindMount(source);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Fact]
    public void WithReferenceShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<IResourceWithEnvironment> builder = null!;
        var builderResource = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var qdrantResource = builderResource.AddQdrant("Qdrant");

        var action = () => builder.WithReference(qdrantResource);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithReferenceShouldThrowWhenQdrantResourceIsNull()
    {
        var builder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var qdrant = builder.AddQdrant("Qdrant");
        IResourceBuilder<QdrantServerResource> qdrantResource = null!;

        var action = () => qdrant.WithReference(qdrantResource);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(qdrantResource), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorQdrantServerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var distributedApplicationBuilder = TestDistributedApplicationBuilder.CreateWithTestContainerRegistry(testOutputHelper);
        var name = isNull ? null! : string.Empty;
        const string key = nameof(key);
        var apiKey = new ParameterResource(key, (ParameterDefault? parameterDefault) => key);

        var action = () => new QdrantServerResource(name, apiKey);

        var exception = isNull
             ? Assert.Throws<ArgumentNullException>(action)
             : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorQdrantServerResourceShouldThrowWhenApiKeyIsNull()
    {
        const string name = "Qdrant";
        ParameterResource apiKey = null!;

        var action = () => new QdrantServerResource(name, apiKey);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(apiKey), exception.ParamName);
    }
}
