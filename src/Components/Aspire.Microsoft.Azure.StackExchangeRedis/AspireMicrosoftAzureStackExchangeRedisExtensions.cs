// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire;
using Aspire.StackExchange.Redis;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using StackExchange.Redis.Configuration;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Extension methods for connecting to an Azure Cache for Redis with StackExchange.Redis client using Azure AD authentication.
/// </summary>
public static class AspireMicrosoftAzureStackExchangeRedisExtensions
{
    /// <summary>
    /// Configures the Redis client to use Azure AD authentication for connecting to Azure Cache for Redis.
    /// </summary>
    /// <param name="builder">The <see cref="AspireRedisClientBuilder"/> to configure.</param>
    /// <param name="credential">The <see cref="TokenCredential"/> to use for Azure AD authentication. If <see langword="null"/>, a default credential will be used.</param>
    /// <returns>The <see cref="AspireRedisClientBuilder"/> for method chaining.</returns>
    /// <remarks>
    /// This extension method configures the Redis client to authenticate with Azure Cache for Redis using Azure AD (Entra ID) instead of access keys.
    /// It leverages the Microsoft.Azure.StackExchangeRedis library to handle the Azure AD authentication flow.
    /// </remarks>
    public static AspireRedisClientBuilder WithAzureAuthentication(this AspireRedisClientBuilder builder, TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var optionsName = builder.ServiceKey is null ? Options.Options.DefaultName : builder.ServiceKey;

        builder.HostBuilder.Services.Configure<ConfigurationOptions>(
            optionsName,
            configurationOptions =>
            {
                var azureOptionsProvider = new AzureOptionsProvider();
                var azureManagedOptionsProvider = new AzureManagedRedisOptionsProvider();
                if (configurationOptions.EndPoints.Any(ep => azureOptionsProvider.IsMatch(ep) || azureManagedOptionsProvider.IsMatch(ep)))
                {
                    // only set up Azure AD authentication if the endpoint indicates it's an Azure Cache for Redis instance
                    credential ??= AzureCredentialHelper.CreateDefaultAzureCredential();

                    // Configure Microsoft.Azure.StackExchangeRedis for Azure AD authentication
                    // Note: Using GetAwaiter().GetResult() is acceptable here because this is configuration-time setup
                    AzureCacheForRedis.ConfigureForAzureWithTokenCredentialAsync(configurationOptions, credential).GetAwaiter().GetResult();
                }
            });

        return builder;
    }
}
