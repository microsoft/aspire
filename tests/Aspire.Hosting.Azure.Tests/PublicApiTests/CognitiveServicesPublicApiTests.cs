// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0618 // AzureOpenAIDeployment is obsolete

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class CognitiveServicesPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureOpenAIDeploymentShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string modelName = "ai";
        const string modelVersion = "1.0";

        var action = () => new AzureOpenAIDeployment(name, modelName, modelVersion);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureOpenAIDeploymentShouldThrowWhenModelNameIsNullOrEmpty(bool isNull)
    {
        const string name = "open-ai";
        var modelName = isNull ? null! : string.Empty;
        const string modelVersion = "1.0";

        var action = () => new AzureOpenAIDeployment(name, modelName, modelVersion);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(modelName), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureOpenAIDeploymentShouldThrowWhenModelVersionIsNullOrEmpty(bool isNull)
    {
        const string name = "open-ai";
        const string modelName = "ai";
        var modelVersion = isNull ? null! : string.Empty;

        var action = () => new AzureOpenAIDeployment(name, modelName, modelVersion);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(modelVersion), exception.ParamName);
    }

    [Fact]
    public void AddAzureOpenAIShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "open-ai";

        var action = () => builder.AddAzureOpenAI(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureOpenAIShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureOpenAI(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddDeploymentShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureOpenAIResource> builder = null!;
        var deployment = new AzureOpenAIDeployment("open-ai", "ai", "1.0");

        var action = () => builder.AddDeployment(deployment);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void AddDeploymentShouldThrowWhenDeploymentIsNull()
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzureOpenAI("open-ai");
        AzureOpenAIDeployment deployment = null!;

        var action = () => builder.AddDeployment(deployment);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(deployment), exception.ParamName);
    }
}
