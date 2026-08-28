// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using Azure.Core;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Service for handling Azure Container Registry (ACR) authentication.
/// </summary>
internal interface IAcrLoginService
{
    /// <summary>
    /// Logs into an Azure Container Registry using Azure credentials.
    /// </summary>
    /// <param name="registryEndpoint">The ACR endpoint (e.g., "myregistry.azurecr.io").</param>
    /// <param name="tenantId">The Azure tenant ID.</param>
    /// <param name="credential">The Azure credential to use for authentication.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when login succeeds.</returns>
    Task LoginAsync(
        string registryEndpoint,
        string tenantId,
        TokenCredential credential,
        CancellationToken cancellationToken = default);
}
