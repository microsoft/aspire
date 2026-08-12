// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class TokenCredentialProviderTests
{
    [Fact]
    public void AddAzureProvisioning_RegistersITokenCredentialProvider()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureProvisioning();

        using var app = builder.Build();

        var provider = app.Services.GetRequiredService<ITokenCredentialProvider>();

        Assert.NotNull(provider);
        Assert.NotNull(provider.TokenCredential);
    }

    [Fact]
    public void AddAzureProvisioning_RegistersITokenCredentialProviderAsSingleton()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureProvisioning();

        using var app = builder.Build();

        var first = app.Services.GetRequiredService<ITokenCredentialProvider>();
        var second = app.Services.GetRequiredService<ITokenCredentialProvider>();

        Assert.Same(first, second);
        Assert.Same(first.TokenCredential, second.TokenCredential);
    }

    [Fact]
    public void AddingAzureResource_RegistersITokenCredentialProvider()
    {
        // AddAzureProvisioning is invoked indirectly when an Azure resource is added.
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureInfrastructure("infra", _ => { });

        using var app = builder.Build();

        var provider = app.Services.GetRequiredService<ITokenCredentialProvider>();

        Assert.NotNull(provider.TokenCredential);
    }

    [Fact]
    public void ITokenCredentialProvider_CanBeReplacedWithCustomImplementation()
    {
        // External callers should be able to plug in their own credential by
        // replacing the registered service with their own implementation.
        var customCredential = new TestTokenCredential();
        var customProvider = new CustomTokenCredentialProvider(customCredential);

        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddAzureProvisioning();
        builder.Services.AddSingleton<ITokenCredentialProvider>(customProvider);

        using var app = builder.Build();

        var resolved = app.Services.GetRequiredService<ITokenCredentialProvider>();

        Assert.Same(customProvider, resolved);
        Assert.Same(customCredential, resolved.TokenCredential);
    }

    private sealed class CustomTokenCredentialProvider(TokenCredential credential) : ITokenCredentialProvider
    {
        public TokenCredential TokenCredential { get; } = credential;
    }
}
