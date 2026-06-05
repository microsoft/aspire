// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREUSERSECRETS001

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Pipelines.Internal;

/// <summary>
/// User secrets implementation of <see cref="IDeploymentStateManager"/>.
/// </summary>
internal sealed class UserSecretsDeploymentStateManager : DeploymentStateManagerBase<UserSecretsDeploymentStateManager>
{
    private readonly IUserSecretsManager _userSecretsManager;

    public UserSecretsDeploymentStateManager(ILogger<UserSecretsDeploymentStateManager> logger, IUserSecretsManager userSecretsManager)
        : base(logger)
    {
        _userSecretsManager = userSecretsManager;
    }

    /// <inheritdoc/>
    public override string? StateFilePath => GetStatePath();

    /// <inheritdoc/>
    protected override string? GetStatePath()
    {
        return _userSecretsManager.FilePath;
    }

    /// <inheritdoc/>
    protected override async Task SaveStateToStorageAsync(JsonObject state, CancellationToken cancellationToken)
    {
        try
        {
            // Use the shared manager which handles locking
            await _userSecretsManager.SaveStateAsync(state, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Azure resource connection strings saved to user secrets.");
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to provision Azure resources because user secrets file is not well-formed JSON.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save user secrets.");
            throw;
        }
    }
}
