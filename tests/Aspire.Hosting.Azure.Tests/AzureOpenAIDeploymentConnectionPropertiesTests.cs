// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureOpenAIDeploymentConnectionPropertiesTests
{
    [Fact]
    public void AzureOpenAIDeploymentResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var openai = builder.AddAzureOpenAI("openai");
        var deployment = openai.AddDeployment("mydeployment", "gpt-4", "1.0");

        var resource = Assert.Single(builder.Resources.OfType<AzureOpenAIDeploymentResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{openai.outputs.connectionString}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ModelName", property.Key);
                Assert.Equal("mydeployment", property.Value.ValueExpression);
            });
    }
}
