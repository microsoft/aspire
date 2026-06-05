// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKeyVaultConnectionPropertiesTests
{
    [Fact]
    public void AzureKeyVaultResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var keyVault = builder.AddAzureKeyVault("keyvault");

        var properties = ((IResourceWithConnectionString)keyVault.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{keyvault.outputs.vaultUri}", property.Value.ValueExpression);
            });
    }
}
