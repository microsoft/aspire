// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class OperationalInsightsPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureLogAnalyticsWorkspaceResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        Action<AzureResourceInfrastructure> configureInfrastructure = (_) => { };

        var action = () => new AzureLogAnalyticsWorkspaceResource(name, configureInfrastructure);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureLogAnalyticsWorkspaceResourceShouldThrowWhenConfigureInfrastructureIsNull()
    {
        const string name = "log-analytics";
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzureLogAnalyticsWorkspaceResource(name, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }

    [Fact]
    public void AddAzureLogAnalyticsWorkspaceShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "log-analytics";

        var action = () => builder.AddAzureLogAnalyticsWorkspace(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureLogAnalyticsWorkspaceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureLogAnalyticsWorkspace(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }
}
