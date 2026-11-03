// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class SearchPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddAzureSearchShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "search";

        var action = () => builder.AddAzureSearch(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureSearchShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureSearch(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureSearchResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        Action<AzureResourceInfrastructure> configureInfrastructure = (_) => { };

        var action = () => new AzureSearchResource(name, configureInfrastructure);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureSearchResourceShouldThrowWhenConfigureInfrastructureIsNull()
    {
        const string name = "search";
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzureSearchResource(name, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }
}
