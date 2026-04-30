// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Provisioning;
using Aspire.Hosting.Azure.Provisioning.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding support for generating Azure resources dynamically during application startup.
/// </summary>
public static class AzureProvisionerExtensions
{
    /// <summary>
    /// Adds support for generating Azure resources dynamically during application startup.
    /// The application must configure the appropriate Azure subscription and location before resources can be provisioned.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <returns>The distributed application builder.</returns>
    [AspireExport(Description = "Adds Azure provisioning services to the distributed application builder")]
    public static IDistributedApplicationBuilder AddAzureProvisioning(this IDistributedApplicationBuilder builder)
    {
        // Always add the Azure environment, even if the user doesn't explicitly add it.
#pragma warning disable ASPIREAZURE001
        builder.AddAzureEnvironment();
#pragma warning restore ASPIREAZURE001

        builder.Services.TryAddSingleton<AzureResourcePreparer>();
        builder.Services.TryAddSingleton<AzureProvisioner>();

        // Attempt to read azure configuration from configuration
        builder.Services.AddOptions<AzureProvisionerOptions>()
            .BindConfiguration("Azure")
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services.TryAddSingleton<ITokenCredentialProvider, DefaultTokenCredentialProvider>();

        // Register ACR login service for container registry authentication
        builder.Services.TryAddSingleton<IAcrLoginService, AcrLoginService>();
        
        // Add named HTTP client for ACR OAuth2 exchange
        // HTTP request logging can be controlled via logging configuration:
        // "Logging": { "LogLevel": { "System.Net.Http.HttpClient.AcrLogin": "Debug" } }
        builder.Services.AddHttpClient("AcrLogin");
        
        builder.Services.AddHttpClient(); // Add default IHttpClientFactory

        // Register BicepProvisioner via interface
        builder.Services.TryAddSingleton<IBicepProvisioner, BicepProvisioner>();

        // Register the new internal services for testability
        builder.Services.TryAddSingleton<IArmClientProvider, DefaultArmClientProvider>();
        builder.Services.TryAddSingleton<ISecretClientProvider, DefaultSecretClientProvider>();
        builder.Services.TryAddSingleton<IBicepCompiler, BicepCliCompiler>();
        builder.Services.TryAddSingleton<IUserPrincipalProvider, DefaultUserPrincipalProvider>();

        if (builder.ExecutionContext.IsPublishMode)
        {
            builder.Services.AddSingleton<IProvisioningContextProvider, PublishModeProvisioningContextProvider>();
        }
        else
        {
            builder.Services.AddSingleton<IProvisioningContextProvider, RunModeProvisioningContextProvider>();
        }
        builder.Services.TryAddSingleton<IProcessRunner, DefaultProcessRunner>();

        return builder;
    }
}
