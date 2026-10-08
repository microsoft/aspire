// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Data.Common;

namespace Aspire.Azure.Common;

internal static class AzureAIProjectConnectionString
{
    public static Uri ParseEndpoint(string? connectionString)
    {
        // Accept a project URI or "Endpoint=https://account.services.ai.azure.com/api/projects/project;Deployment=chat".
        // Deployment is consumed by model-client registrations, not by the project client itself.
        if (Uri.TryCreate(connectionString, UriKind.Absolute, out var uri))
        {
            return ValidateEndpoint(uri);
        }

        var values = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (values.ContainsKey("Key"))
        {
            throw new ArgumentException("Foundry project clients require Microsoft Entra authentication. API key connection strings are not supported.", nameof(connectionString));
        }

        if (!values.TryGetValue("Endpoint", out var endpoint) ||
            !Uri.TryCreate(endpoint.ToString(), UriKind.Absolute, out uri))
        {
            throw new ArgumentException("The Foundry project connection string must contain an absolute Endpoint URI.", nameof(connectionString));
        }

        return ValidateEndpoint(uri);
    }

    public static Uri ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The Foundry project endpoint must be an absolute HTTP or HTTPS URI.", nameof(endpoint));
        }

        return endpoint;
    }
}
