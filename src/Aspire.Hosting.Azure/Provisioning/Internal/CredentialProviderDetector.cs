// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure.Provisioning.Internal;

/// <summary>
/// Detects available Azure credential providers in the user's environment.
/// </summary>
internal sealed class CredentialProviderDetector(ILogger logger)
{
    private readonly ILogger _logger = logger;

    /// <summary>
    /// Detects which credential providers are available in the current environment.
    /// </summary>
    /// <param name="tenantId">Optional tenant ID to use for credential detection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of available credential provider names.</returns>
    public async Task<List<string>> DetectAvailableProvidersAsync(string? tenantId = null, CancellationToken cancellationToken = default)
    {
        var availableProviders = new List<string>();

        // Check each credential provider in parallel for better performance
        var detectionTasks = new[]
        {
            Task.Run(() => IsCredentialAvailableAsync("AzureCli", () => new AzureCliCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" }
            }), cancellationToken), cancellationToken),
            Task.Run(() => IsCredentialAvailableAsync("VisualStudio", () => new VisualStudioCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" },
                ProcessTimeout = TimeSpan.FromSeconds(5)
            }), cancellationToken), cancellationToken),
            Task.Run(() => IsCredentialAvailableAsync("VisualStudioCode", () => new VisualStudioCodeCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" }
            }), cancellationToken), cancellationToken),
            Task.Run(() => IsCredentialAvailableAsync("AzurePowerShell", () => new AzurePowerShellCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" },
                ProcessTimeout = TimeSpan.FromSeconds(5)
            }), cancellationToken), cancellationToken),
            Task.Run(() => IsCredentialAvailableAsync("AzureDeveloperCli", () => new AzureDeveloperCliCredential(new()
            {
                TenantId = tenantId,
                AdditionallyAllowedTenants = { "*" },
                ProcessTimeout = TimeSpan.FromSeconds(5)
            }), cancellationToken), cancellationToken)
        };

        var results = await Task.WhenAll(detectionTasks).ConfigureAwait(false);

        var providers = new[] { "AzureCli", "VisualStudio", "VisualStudioCode", "AzurePowerShell", "AzureDeveloperCli" };
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i])
            {
                availableProviders.Add(providers[i]);
                _logger.LogDebug("Detected available credential provider: {Provider}", providers[i]);
            }
        }

        // Always include InteractiveBrowser as a fallback option
        availableProviders.Add("InteractiveBrowser");
        _logger.LogDebug("Added InteractiveBrowser as fallback credential provider");

        return availableProviders;
    }

    private async Task<bool> IsCredentialAvailableAsync(string providerName, Func<TokenCredential> credentialFactory, CancellationToken cancellationToken)
    {
        try
        {
            var credential = credentialFactory();

            // Try to get a token for Azure Resource Manager with a timeout
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var tokenRequest = new TokenRequestContext(["https://management.azure.com/.default"]);
            var token = await credential.GetTokenAsync(tokenRequest, cts.Token).ConfigureAwait(false);
            return !string.IsNullOrEmpty(token.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to detect {Provider} credential availability", providerName);
            return false;
        }
    }
}
