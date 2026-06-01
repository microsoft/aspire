// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Radius.CloudProviders;

/// <summary>
/// In-flight builder backing <see cref="IAzureRadiusProviderBuilder"/>. The
/// <see cref="Credential"/> slot is mutated by each <c>With*</c> call (last
/// write wins per FR-008) and the final value is hoisted into
/// <see cref="AzureRadiusProviderConfig"/> by <c>WithAzureProvider</c> after
/// the user callback returns.
/// </summary>
internal sealed class AzureRadiusProviderBuilder : IAzureRadiusProviderBuilder
{
    private readonly ILogger _logger;

    internal AzureRadiusProviderBuilder(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    internal AzureRadiusCredential? Credential { get; private set; }

    public IAzureRadiusProviderBuilder WithServicePrincipal(
        string tenantId,
        string clientId,
        IResourceBuilder<ParameterResource> clientSecret)
    {
        CloudProviderValidation.ValidateGuid(tenantId, nameof(tenantId));
        CloudProviderValidation.ValidateGuid(clientId, nameof(clientId));
        ArgumentNullException.ThrowIfNull(clientSecret);

        LogOverrideIfNeeded(nameof(WithServicePrincipal));
        Credential = new AzureRadiusCredential.ServicePrincipal(tenantId, clientId, clientSecret);
        return this;
    }

    public IAzureRadiusProviderBuilder WithWorkloadIdentity(string clientId, string tenantId)
    {
        CloudProviderValidation.ValidateGuid(clientId, nameof(clientId));
        CloudProviderValidation.ValidateGuid(tenantId, nameof(tenantId));

        LogOverrideIfNeeded(nameof(WithWorkloadIdentity));
        Credential = new AzureRadiusCredential.WorkloadIdentity(clientId, tenantId);
        return this;
    }

    private void LogOverrideIfNeeded(string newMode)
    {
        if (Credential is not null)
        {
            _logger.LogDebug(
                "Azure credential overridden inside WithAzureProvider callback: '{Previous}' replaced by '{New}'.",
                Credential.GetType().Name,
                newMode);
        }
    }
}
