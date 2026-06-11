// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Milvus.Tests;

public class MilvusPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorAttuResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;

        var action = () => new AttuResource(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddMilvusShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "Milvus";

        var action = () => builder.AddMilvus(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddMilvusShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddMilvus(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddDatabaseShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MilvusServerResource> builder = null!;
        const string name = "db";

        var action = () => builder.AddDatabase(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddDatabaseShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddMilvus("Milvus");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddDatabase(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithAttuShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MilvusServerResource> builder = null!;

        var action = () => builder.WithAttu();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MilvusServerResource> builder = null!;

        var action = () => builder.WithDataVolume();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MilvusServerResource> builder = null!;
        const string source = "/milvus/data";

        var action = () => builder.WithDataBindMount(source);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithDataBindMountShouldThrowWhenSourceIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddMilvus("Milvus");
        var source = isNull ? null! : string.Empty;

        var action = () => builder.WithDataBindMount(source);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Fact]
    public void WithConfigurationBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MilvusServerResource> builder = null!;
        const string configurationFilePath = "/milvus/configs/milvus.yaml";

#pragma warning disable CS0618 // Type or member is obsolete
        var action = () => builder.WithConfigurationBindMount(configurationFilePath);
#pragma warning restore CS0618 // Type or member is obsolete

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithConfigurationBindMountShouldThrowWhenConfigurationFilePathIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddMilvus("Milvus");
        string configurationFilePath = isNull ? null! : string.Empty;

#pragma warning disable CS0618 // Type or member is obsolete
        var action = () => builder.WithConfigurationBindMount(configurationFilePath);
#pragma warning restore CS0618 // Type or member is obsolete

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(configurationFilePath), exception.ParamName);
    }

    [Fact]
    public void WithConfigurationFileShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<MilvusServerResource> builder = null!;
        const string configurationFilePath = "/milvus/configs/milvus.yaml";

        var action = () => builder.WithConfigurationFile(configurationFilePath);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithConfigurationFileShouldThrowWhenConfigurationFilePathIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddMilvus("Milvus");
        string configurationFilePath = isNull ? null! : string.Empty;

        var action = () => builder.WithConfigurationFile(configurationFilePath);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(configurationFilePath), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorMilvusDatabaseResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string databaseName = "db";
        var apiKey = new ParameterResource("ApiKey", (pd) => "root:Milvus");
        var parent = new MilvusServerResource("Milvus", apiKey);

        var action = () => new MilvusDatabaseResource(name, databaseName, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorMilvusDatabaseResourceShouldThrowWhenDatabaseNameIsNullOrEmpty(bool isNull)
    {
        const string name = "Milvus";
        var databaseName = isNull ? null! : string.Empty;
        var apiKey = new ParameterResource("ApiKey", (pd) => "root:Milvus");
        var parent = new MilvusServerResource("Milvus", apiKey);

        var action = () => new MilvusDatabaseResource(name, databaseName, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(databaseName), exception.ParamName);
    }

    [Fact]
    public void CtorMilvusDatabaseResourceShouldThrowWhenDatabaseNameIsNull()
    {
        const string name = "Milvus";
        var databaseName = "db";
        MilvusServerResource parent = null!;

        var action = () => new MilvusDatabaseResource(name, databaseName, parent);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(parent), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorMilvusServerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        var apiKey = new ParameterResource("ApiKey", (pd) => "root:Milvus");

        var action = () => new MilvusServerResource(name, apiKey);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorMilvusServerResourceShouldThrowWhenApiKeyIsNull()
    {
        const string name = "Milvus";
        ParameterResource apiKey = null!;

        var action = () => new MilvusServerResource(name, apiKey);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(apiKey), exception.ParamName);
    }
}

