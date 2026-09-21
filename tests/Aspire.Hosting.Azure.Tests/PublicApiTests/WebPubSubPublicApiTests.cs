// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using static Aspire.Hosting.ApplicationModel.ReferenceExpression;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class WebPubSubPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddAzureWebPubSubShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "web-pub-sub";

        var action = () => builder.AddAzureWebPubSub(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureWebPubSubShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureWebPubSub(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddHubShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureWebPubSubResource> builder = null!;
        const string hubName = "hub";

        var action = () => builder.AddHub(hubName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddHubShouldThrowWhenHubNameIsNullOrEmpty(bool isNull)
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureWebPubSub("web-pub-sub");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddHub(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AddEventHandlerShouldThrowWhenBuilderIsNull(int overrideIndex)
    {
        IResourceBuilder<AzureWebPubSubHubResource> builder = null!;
        var urlExpression = ReferenceExpression.Create($"host");
        const string userEventPattern = "*";

        Action action = overrideIndex switch
        {
            0 => () => builder.AddEventHandler(new ExpressionInterpolatedStringHandler(1, 1), userEventPattern),
            1 => () => builder.AddEventHandler(urlExpression, userEventPattern),
            _ => throw new InvalidOperationException()
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public void AddEventHandlerShouldThrowWhenUserEventPatternIsNull(int overrideIndex, bool isNull)
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureWebPubSub("web-pub-sub").AddHub("hub");
        var urlExpression = ReferenceExpression.Create($"host");
        var userEventPattern = isNull ? null! : string.Empty;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddEventHandler(new ExpressionInterpolatedStringHandler(1, 1), userEventPattern),
            1 => () => builder.AddEventHandler(urlExpression, userEventPattern),
            _ => throw new InvalidOperationException()
        };

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(userEventPattern), exception.ParamName);
    }

    [Fact]
    public void AddEventHandlerShouldThrowWhenUrlExpressionIsNull()
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureWebPubSub("web-pub-sub").AddHub("hub");
        ReferenceExpression urlExpression = null!;

        var action = () => builder.AddEventHandler(urlExpression);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(urlExpression), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureWebPubSubHubResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        var webpubsub = new AzureWebPubSubResource("web-pub-sub", (_) => { });

        var action = () => new AzureWebPubSubHubResource(name, name, webpubsub);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureWebPubSubHubResourceShouldThrowWhenWebPubSubIsNull()
    {
        const string name = "web-pub-sub";
        AzureWebPubSubResource webpubsub = null!;

        var action = () => new AzureWebPubSubHubResource(name, name, webpubsub);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(webpubsub), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureWebPubSubResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        Action<AzureResourceInfrastructure> configureInfrastructure = (_) => { };

        var action = () => new AzureWebPubSubResource(name, configureInfrastructure);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureWebPubSubResourceShouldThrowWhenConfigureInfrastructureIsNull()
    {
        const string name = "web-pub-sub";
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzureWebPubSubResource(name, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }
}
