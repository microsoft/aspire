// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

internal class DefaultTokenCredentialProvider : ITokenCredentialProvider
{
    private readonly ILogger<DefaultTokenCredentialProvider> _logger;
    private readonly IOptions<AzureProvisionerOptions> _options;
    private readonly DistributedApplicationExecutionContext _distributedApplicationExecutionContext;
    private TokenCredential? _credential;
    private string? _lastTenantId;
    private string? _lastCredentialSource;
    private readonly object _lock = new();

    public DefaultTokenCredentialProvider(
        ILogger<DefaultTokenCredentialProvider> logger,
        IOptions<AzureProvisionerOptions> options,
        DistributedApplicationExecutionContext distributedApplicationExecutionContext)
    {
        _logger = logger;
        _options = options;
        _distributedApplicationExecutionContext = distributedApplicationExecutionContext;
    }

    public TokenCredential TokenCredential
    {
        get
        {
            lock (_lock)
            {
                var currentTenantId = _options.Value.TenantId;
                var currentCredentialSource = _options.Value.CredentialSource;

                // Recreate credential if tenant ID or credential source has changed, or credential doesn't exist
                if (_credential == null || _lastTenantId != currentTenantId || _lastCredentialSource != currentCredentialSource)
                {
                    _credential = CreateCredential(currentTenantId);
                    _lastTenantId = currentTenantId;
                    _lastCredentialSource = currentCredentialSource;
                }

                return _credential;
            }
        }
    }

    private TokenCredential CreateCredential(string? tenantId)
    {
        var credentialSetting = _options.Value.CredentialSource;

        TokenCredential credential = credentialSetting switch
        {
            "AzureCli" => new AzureCliCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" }
            }),
            "AzurePowerShell" => new AzurePowerShellCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" }
            }),
            "VisualStudio" => new VisualStudioCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" }
            }),
            "VisualStudioCode" => new VisualStudioCodeCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" }
            }),
            "AzureDeveloperCli" => new AzureDeveloperCliCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" }
            }),
            "InteractiveBrowser" => new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions()
            {
                TenantId = tenantId
            }),
            // Use AzureCli as default when no explicit credential source is set
            null or "Default" => new AzureCliCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" }
            }),
            _ => throw new InvalidOperationException($"Unsupported credential source: {credentialSetting}")
        };

        return credential;
    }

    internal void LogCredentialType()
    {
        var credential = TokenCredential;
        _logger.LogInformation("Using {credentialType} for provisioning.", credential.GetType().Name);
    }
}
