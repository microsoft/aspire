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

                // Recreate credential if tenant ID has changed or credential doesn't exist
                if (_credential == null || _lastTenantId != currentTenantId)
                {
                    _credential = CreateCredential(currentTenantId);
                    _lastTenantId = currentTenantId;
                }

                return _credential;
            }
        }
    }

    private TokenCredential CreateCredential(string? tenantId)
    {
        var credentialSetting = _options.Value.CredentialSource;
        var processTimeout = TimeSpan.FromSeconds(_options.Value.CredentialProcessTimeoutSeconds);

        TokenCredential credential = credentialSetting switch
        {
            "AzureCli" => new AzureCliCredential(new()
            {
                TenantId = tenantId,
                ProcessTimeout = processTimeout,
                AdditionallyAllowedTenants = { "*" }
            }),
            "AzurePowerShell" => new AzurePowerShellCredential(new()
            {
                TenantId = tenantId,
                ProcessTimeout = processTimeout,
                AdditionallyAllowedTenants = { "*" }
            }),
            "VisualStudio" => new VisualStudioCredential(new()
            {
                TenantId = tenantId,
                ProcessTimeout = processTimeout,
                AdditionallyAllowedTenants = { "*" }
            }),
            "AzureDeveloperCli" => new AzureDeveloperCliCredential(new()
            {
                TenantId = tenantId,
                ProcessTimeout = processTimeout,
                AdditionallyAllowedTenants = { "*" }
            }),
            "InteractiveBrowser" => new InteractiveBrowserCredential(new InteractiveBrowserCredentialOptions()
            {
                TenantId = tenantId
            }),
            // Use AzureCli as default for publish mode when no explicit credential source is set
            null or "Default" when _distributedApplicationExecutionContext.IsPublishMode => new AzureCliCredential(new()
            {
                TenantId = tenantId,
                ProcessTimeout = processTimeout,
                AdditionallyAllowedTenants = { "*" }
            }),
            _ => new DefaultAzureCredential(new DefaultAzureCredentialOptions()
            {
                TenantId = tenantId,
                ExcludeManagedIdentityCredential = true,
                ExcludeWorkloadIdentityCredential = true,
                ExcludeAzurePowerShellCredential = true,
                CredentialProcessTimeout = TimeSpan.FromSeconds(15),
                AdditionallyAllowedTenants = { "*" }
            })
        };

        return credential;
    }

    internal void LogCredentialType()
    {
        var credential = TokenCredential;
        _logger.LogInformation("Using {credentialType} for provisioning.", credential.GetType().Name);
    }
}
