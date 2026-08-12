// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Identity;

namespace Aspire;

internal static class AzureCredentialHelper
{
    /// <summary>
    /// Creates a <see cref="TokenCredential"/> for code that can run in local development or deployed to Azure.
    /// </summary>
    internal static TokenCredential CreateDefaultAzureCredential()
    {
        if (Environment.GetEnvironmentVariable(DefaultAzureCredential.DefaultEnvironmentVariableName) is not null)
        {
            return new DefaultAzureCredential(DefaultAzureCredential.DefaultEnvironmentVariableName);
        }

        if (Environment.GetEnvironmentVariable("AZURE_CLIENT_ID") is string azureClientId)
        {
            // When we don't see DefaultEnvironmentVariableName, but we do see AZURE_CLIENT_ID,
            // we just use ManagedIdentityCredential because that's the only credential type that
            // Aspire Hosting enables by default.
            // This is also used to support user assigned managed identities in Azure App Service and Azure Functions.
            // If this doesn't work for applications, they can override the TokenCredential in their settings.
            return new ManagedIdentityCredential(new ManagedIdentityCredentialOptions(ManagedIdentityId.FromUserAssignedClientId(azureClientId)));
        }
        else if (Environment.GetEnvironmentVariable("IDENTITY_ENDPOINT") is not null)
        {
            // When we see IDENTITY_ENDPOINT, but not AZURE_CLIENT_ID, we assume we're in an Azure
            // environment with a system assigned managed identity such as Azure App Service or Azure Functions.
            return new ManagedIdentityCredential(new ManagedIdentityCredentialOptions(ManagedIdentityId.SystemAssigned));
        }

        // when we can't detect a known Azure environment, fall back to the development credential
        return CreateDevelopmentAzureCredential();
    }

    /// <summary>
    /// Creates a <see cref="DefaultAzureCredential"/> optimized for local development by excluding
    /// credential types not applicable on developer machines.
    /// </summary>
    private static TokenCredential CreateDevelopmentAzureCredential()
    {
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeEnvironmentCredential = true,
            ExcludeWorkloadIdentityCredential = true,
            ExcludeManagedIdentityCredential = true
        });
    }
}
