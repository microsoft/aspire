// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class KeyVaultPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureKeyVaultResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        Action<AzureResourceInfrastructure> configureInfrastructure = (_) => { };

        var action = () => new AzureKeyVaultResource(name, configureInfrastructure);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureKeyVaultResourceShouldThrowWhenConfigureInfrastructureIsNull()
    {
        const string name = "key-vault";
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzureKeyVaultResource(name, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }

    [Fact]
    public void AddAzureKeyVaultShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "key-vault";

        var action = () => builder.AddAzureKeyVault(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureKeyVaultShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureKeyVault(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }
}
