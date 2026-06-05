// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.OpenAI.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void OpenAiResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var key = new ParameterResource("key", _ => "p@ssw0rd1", secret: true);
        var resource = new OpenAIResource("openai", key)
        {
            Endpoint = "https://contoso.ai/v1"
        };

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Endpoint", property.Key);
                Assert.Equal("https://contoso.ai/v1", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("https://contoso.ai/v1", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Key", property.Key);
                Assert.Equal("{key.value}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void OpenAiModelResourceGetConnectionPropertiesIncludesModel()
    {
        var key = new ParameterResource("key", _ => "p@ssw0rd1", secret: true);
        var account = new OpenAIResource("openai", key)
        {
            Endpoint = "https://contoso.ai/v1"
        };
        var resource = new OpenAIModelResource("assistant", "gpt-4o-mini", account);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Contains(properties, property => property.Key == "Endpoint" && property.Value.ValueExpression == "https://contoso.ai/v1");
        Assert.Contains(properties, property => property.Key == "Uri" && property.Value.ValueExpression == "https://contoso.ai/v1");
        Assert.Contains(properties, property => property.Key == "Key" && property.Value.ValueExpression == "{key.value}");
        Assert.Contains(properties, property => property.Key == "ModelName" && property.Value.ValueExpression == "gpt-4o-mini");
    }
}
