// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class ApplicationInsightsPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AddAzureApplicationInsightsShouldThrowWhenBuilderIsNull(int overrideIndex)
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "insights";
        IResourceBuilder<AzureLogAnalyticsWorkspaceResource>? logAnalyticsWorkspace = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddAzureApplicationInsights(name),
            1 => () => builder.AddAzureApplicationInsights(name, logAnalyticsWorkspace),
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
    public void AddAzureApplicationInsightsShouldThrowWhenNameIsNullOrEmpty(int overrideIndex, bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;
        IResourceBuilder<AzureLogAnalyticsWorkspaceResource>? logAnalyticsWorkspace = null;

        Action action = overrideIndex switch
        {
            0 => () => builder.AddAzureApplicationInsights(name),
            1 => () => builder.AddAzureApplicationInsights(name, logAnalyticsWorkspace),
            _ => throw new InvalidOperationException()
        };

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureApplicationInsightsResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        Action<AzureResourceInfrastructure> configureInfrastructure = (_) => { };

        var action = () => new AzureApplicationInsightsResource(name, configureInfrastructure);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureApplicationInsightsResourceShouldThrowWhenConfigureInfrastructureIsNull()
    {
        const string name = "insights";
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzureApplicationInsightsResource(name, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }
}
