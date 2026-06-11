// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.GitHub.Models.Tests;

public class ConnectionPropertiesTests
{
    [Fact]
    public void GitHubModelResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var key = new ParameterResource("key", _ => "p@ssw0rd1", secret: true);
        var resource = new GitHubModelResource("model", "gpt", organization: null, key);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("https://models.github.ai/inference", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Key", property.Key);
                Assert.Equal("{key.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ModelName", property.Key);
                Assert.Equal("gpt", property.Value.ValueExpression);
            });

        Assert.DoesNotContain(properties, property => property.Key == "Organization");
    }

    [Fact]
    public void GitHubModelResourceGetConnectionPropertiesIncludesOrganizationWhenProvided()
    {
        var organization = new ParameterResource("org", _ => "dotnet");
        var key = new ParameterResource("key", _ => "p@ssw0rd1", secret: true);
        var resource = new GitHubModelResource("model", "gpt", organization, key);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Contains(properties, property => property.Key == "Uri" && property.Value.ValueExpression == "https://models.github.ai/orgs/{org.value}/inference");
        Assert.Contains(properties, property => property.Key == "Key" && property.Value.ValueExpression == "{key.value}");
        Assert.Contains(properties, property => property.Key == "ModelName" && property.Value.ValueExpression == "gpt");
        Assert.Contains(properties, property => property.Key == "Organization" && property.Value.ValueExpression == "{org.value}");
    }
}
