// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.DevTunnels;

internal sealed class DevTunnelLoginManager(
    IDevTunnelClient devTunnelClient,
    IInteractionService interactionService,
    IConfiguration configuration,
    ILogger<DevTunnelLoginManager> logger) : CoalescingAsyncOperation
{
    private const string PreferredAuthProviderKey = "ASPIRE_DEVTUNNELS_AUTH_PROVIDER";

    private readonly IDevTunnelClient _devTunnelClient = devTunnelClient;
    private readonly IInteractionService _interactionService = interactionService;
    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<DevTunnelLoginManager> _logger = logger;

    public Task EnsureUserLoggedInAsync(CancellationToken cancellationToken = default) => RunAsync(cancellationToken);

    protected override async Task ExecuteCoreAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            _logger.LogDebug("Checking dev tunnel login status");
            var loginStatus = await _devTunnelClient.GetUserLoginStatusAsync(_logger, cancellationToken).ConfigureAwait(false);
            if (loginStatus.IsLoggedIn)
            {
                _logger.LogDebug("User already logged in to dev tunnel service as {Username} with {Provider}", loginStatus.Username, loginStatus.Provider);
                // Already logged in
                break;
            }
            else
            {
                var selectedProvider = LoginProvider.Microsoft;
                if (_configuration[PreferredAuthProviderKey] is string preferredProvider)
                {
                    if (Enum.TryParse<LoginProvider>(preferredProvider, ignoreCase: true, out var provider))
                    {
                        selectedProvider = provider;
                    }
                    else
                    {
                        _logger.LogWarning($"Invalid {PreferredAuthProviderKey} value '{{PreferredProvider}}', defaulting to {{SelectedProvider}}", preferredProvider, selectedProvider);
                    }
                }

                if (_interactionService.IsAvailable)
                {
                    // Not logged in, prompt the user to login
                    _logger.LogDebug("Prompting user to login to dev tunnel service");
                    var result = await _interactionService.PromptNotificationAsync(
                        "Dev tunnels",
                        Resources.MessageStrings.AuthenticationRequiredNotification,
                        new()
                        {
                            Intent = MessageIntent.Warning,
                            PrimaryButtonText = Resources.MessageStrings.LoginWithMicrosoft,
                            SecondaryButtonText = Resources.MessageStrings.LoginWithGitHub,
                            ShowSecondaryButton = true,
                            ShowDismiss = false
                        },
                        cancellationToken).ConfigureAwait(false);

                    selectedProvider = result.Data ? LoginProvider.Microsoft : LoginProvider.GitHub;
                    _logger.LogDebug("User selected {LoginProvider} for dev tunnel login", selectedProvider);
                    // Check again in case they logged in from another window while we were prompting
                    loginStatus = await _devTunnelClient.GetUserLoginStatusAsync(_logger, cancellationToken).ConfigureAwait(false);
                }
                if (!loginStatus.IsLoggedIn || loginStatus.Provider != selectedProvider)
                {
                    // Trigger the login flow
                    _logger.LogInformation("Initiating dev tunnel login via {LoginProvider}", selectedProvider);
                    loginStatus = await _devTunnelClient.UserLoginAsync(selectedProvider, _logger, cancellationToken).ConfigureAwait(false);

                    if (loginStatus.IsLoggedIn)
                    {
                        // Successfully logged in
                        _logger.LogInformation("User logged in to dev tunnel service as {Username} with {Provider}", loginStatus.Username, loginStatus.Provider);
                        break;
                    }
                }
                else
                {
                    // Logged in from another window while we were prompting
                    _logger.LogDebug("User already logged in to dev tunnel service as {Username} with {Provider}", loginStatus.Username, loginStatus.Provider);
                    break;
                }

                _logger.LogDebug("User login to dev tunnel service failed, retrying login prompt");
            }
        }
    }
}
