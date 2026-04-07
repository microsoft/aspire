// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Foundry;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class FoundryDeploymentConnectionPropertiesTests
{
    [Fact]
    public void FoundryDeploymentResourceGetConnectionPropertiesReturnsExpectedValues_Local()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var deployment = builder.AddFoundry("aifoundry")
            .RunAsFoundryLocal()
            .AddDeployment("chat", FoundryModel.Local.Phi4);

        // These would be set when the resource starts
        deployment.Resource.Parent.EmulatorServiceUri = new Uri("http://localhost:8080");
        deployment.Resource.Parent.ApiKey = "OPENAI_KEY";

        var properties = ((IResourceWithConnectionString)deployment.Resource).GetConnectionProperties().ToArray();

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
            },
            property =>
            {
                Assert.Equal("ModelName", property.Key);
                Assert.Equal(FoundryModel.Local.Phi4.Name, property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Format", property.Key);
                Assert.Equal(FoundryModel.Local.Phi4.Format, property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Version", property.Key);
                Assert.Equal(FoundryModel.Local.Phi4.Version, property.Value.ValueExpression);
            });
    }

    [Fact]
    public void FoundryDeploymentResourceGetConnectionPropertiesReturnsExpectedValues_Azure()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var deployment = builder.AddFoundry("aifoundry")
            .AddDeployment("chat", FoundryModel.Microsoft.Phi4);

        var properties = ((IResourceWithConnectionString)deployment.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{aifoundry.outputs.aiFoundryApiEndpoint}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ModelName", property.Key);
                // Should be FoundryModel.Microsoft.Phi4.Format but assigned dynamically
                Assert.Equal("chat", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Format", property.Key);
                Assert.Equal(FoundryModel.Microsoft.Phi4.Format, property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Version", property.Key);
                Assert.Equal(FoundryModel.Microsoft.Phi4.Version, property.Value.ValueExpression);
            });
    }
}
