// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Kafka.Tests;

public class KafkaPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddKafkaShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "Kafka";

        var action = () => builder.AddKafka(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKafkaShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddKafka(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKafkaWithParametersShouldThrowWhenBuilderIsNull(bool includePort)
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "Kafka";
        IResourceBuilder<ParameterResource>? userName = null;
        IResourceBuilder<ParameterResource>? password = null;

        var action = () => includePort
            ? builder.AddKafka(name, 9092, userName: userName, password: password)
            : builder.AddKafka(name, userName: userName, password: password);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddKafkaWithParametersShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddKafka(name, userName: null, password: null);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void WithPasswordShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<KafkaServerResource> builder = null!;

        var action = () => builder.WithPassword(null);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithUserNameShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<KafkaServerResource> builder = null!;
        IResourceBuilder<ParameterResource> userName = null!;

        var action = () => builder.WithUserName(userName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithUserNameShouldThrowWhenUserNameIsNull()
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper)
            .AddKafka("kafka");
        IResourceBuilder<ParameterResource> userName = null!;

        var action = () => builder.WithUserName(userName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(userName), exception.ParamName);
    }

    [Fact]
    public void WithKafkaUIShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<KafkaServerResource> builder = null!;

        var action = () => builder.WithKafkaUI();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithHostPortShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<KafkaUIContainerResource> builder = null!;
        int? port = null;

        var action = () => builder.WithHostPort(port);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<KafkaServerResource> builder = null!;

        var action = () => builder.WithDataVolume();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataBindMountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<KafkaServerResource> builder = null!;
        const string source = "/Kafka/data";

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
            .AddKafka("kafka");
        var source = isNull ? null! : string.Empty;

        var action = () => builder.WithDataBindMount(source);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(source), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorKafkaServerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;

        var action = () => new KafkaServerResource(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    public void CtorKafkaServerResourceWithParametersShouldThrowWhenNameIsNullOrEmpty(bool isNull, bool isNullUserName, bool isNullPassword)
    {
        var name = isNull ? null! : string.Empty;
        var userName = isNullUserName ? null : new ParameterResource("user", _ => "usr");
        var password = isNullPassword ? null : new ParameterResource("pass", _ => "p@ssw0rd1", secret: true);

        var action = () => new KafkaServerResource(name, userName, password);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorKafkaUIContainerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;

        var action = () => new KafkaUIContainerResource(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }
}
