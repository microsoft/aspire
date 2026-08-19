// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.AI.AgentServer.Responses;
using Azure.AI.AgentServer.Responses.Models;
using Microsoft.Agents.AI.Foundry.Hosting;

/// <summary>
/// A <see cref="HostedSessionIsolationKeyProvider"/> for local Docker debugging only.
/// </summary>
public sealed class DevTemporaryLocalUserIdProvider : HostedSessionIsolationKeyProvider
{
    /// <summary>
    /// Environment variable that supplies the user id when the platform header is absent.
    /// </summary>
    public const string UserIdEnvironmentVariable = "HOSTED_USER_ID";

    /// <summary>
    /// Default user id used when neither the platform header nor the environment variable supplies a value.
    /// </summary>
    public const string DefaultLocalUserId = "local-dev-user";

    /// <inheritdoc />
    public override ValueTask<HostedSessionContext?> GetKeysAsync(
        ResponseContext context,
        CreateResponse request,
        CancellationToken cancellationToken)
    {
        var userId = !string.IsNullOrWhiteSpace(context?.PlatformContext?.UserIdKey)
            ? context!.PlatformContext!.UserIdKey
            : Environment.GetEnvironmentVariable(UserIdEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(userId))
        {
            userId = DefaultLocalUserId;
        }

        return new ValueTask<HostedSessionContext?>(new HostedSessionContext(userId!));
    }
}