// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureAIFoundryConnectionPropertiesTests
{
    [Fact]
    public void AzureAIFoundryResourceGetConnectionPropertiesReturnsExpectedValues_Azure()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var aiFoundry = builder.AddAzureAIFoundry("aifoundry");

        var properties = ((IResourceWithConnectionString)aiFoundry.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{aifoundry.outputs.aiFoundryApiEndpoint}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureAIFoundryResourceGetConnectionPropertiesReturnsExpectedValues_Local()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var aiFoundry = builder.AddAzureAIFoundry("aifoundry").RunAsFoundryLocal();

        // These would be set when the resource starts
        aiFoundry.Resource.EmulatorServiceUri = new Uri("http://localhost:8080");
        aiFoundry.Resource.ApiKey = "OPENAI_KEY";

        var properties = ((IResourceWithConnectionString)aiFoundry.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("http://localhost:8080/", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Key", property.Key);
                Assert.Equal("OPENAI_KEY", property.Value.ValueExpression);
            });
    }
}
