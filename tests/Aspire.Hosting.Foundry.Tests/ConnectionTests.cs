// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;

namespace Aspire.Hosting.Foundry.Tests;

public class AppInsightsConnectionPropertiesTests
{
    [Fact]
    public void ShouldRenderToBicep()
    {
        AppInsightsConnectionProperties props = new()
        {
            Target = new BicepValue<string>("my-insights-id"),
            IsSharedToAll = true
        };
        var result = props.ToBicepExpression().ToString();
        Assert.Contains("isSharedToAll: true", result);
        Assert.Contains("target: 'my-insights-id'", result);
        Assert.Contains("authType: 'ApiKey'", result);
        Assert.Contains("category: 'AppInsights'", result);
    }
}

public class AzureKeyVaultConnectionPropertiesTests
{
    [Fact]
    public void ShouldRenderToBicep()
    {
        AzureKeyVaultConnectionProperties props = new()
        {
            Target = new BicepValue<string>("my-keyvault-id"),
            IsSharedToAll = true
        };
        var result = props.ToBicepExpression().ToString();
        Assert.Contains("isSharedToAll: true", result);
        Assert.Contains("target: 'my-keyvault-id'", result);
        Assert.Contains("authType: 'ManagedIdentity'", result);
        Assert.Contains("category: 'AzureKeyVault'", result);
    }
}

public class AzureStorageAccountConnectionPropertiesTests
{
    [Fact]
    public void ShouldRenderToBicep()
    {
        AzureStorageAccountConnectionProperties props = new()
        {
            Target = new BicepValue<string>("my-storage-endpoint"),
            IsSharedToAll = false
        };
        var result = props.ToBicepExpression().ToString();
        Assert.Contains("target: 'my-storage-endpoint'", result);
        Assert.Contains("category: 'AzureStorageAccount'", result);
    }
}
