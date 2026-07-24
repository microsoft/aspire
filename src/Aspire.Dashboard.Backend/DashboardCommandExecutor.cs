// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.Backend;

internal interface IDashboardCommandExecutor
{
    ValueTask<DashboardCommandResponse?> ExecuteAsync(
        DashboardExecuteCommandRequest request,
        CancellationToken cancellationToken);
}

internal sealed class DashboardCommandExecutor(
    IDashboardResourceServiceConnection resourceServiceConnection,
    IDashboardResourceSnapshotProvider resourceSnapshotProvider,
    ILogger<DashboardCommandExecutor> logger) : IDashboardCommandExecutor
{
    public async ValueTask<DashboardCommandResponse?> ExecuteAsync(
        DashboardExecuteCommandRequest request,
        CancellationToken cancellationToken)
    {
        var resources = await resourceSnapshotProvider.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var resource = resources.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, request.ResourceName, StringComparison.OrdinalIgnoreCase));
        var command = resource?.Commands.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, request.CommandName, StringComparison.Ordinal));
        if (resource is null || command is null)
        {
            return null;
        }

        if (!resourceServiceConnection.IsConfigured)
        {
            throw new DashboardResourceServiceUnavailableException(resourceServiceConnection.UnavailableMessage);
        }

        logger.LogDebug(
            "Executing resource command {CommandName} on {ResourceName} through the shared AppHost session.",
            command.Name,
            resource.Name);
        var response = await resourceServiceConnection.ExecuteResourceCommandAsync(
            new ResourceCommandRequest
            {
                ResourceName = resource.Name,
                ResourceType = resource.ResourceType,
                CommandName = command.Name
            },
            cancellationToken).ConfigureAwait(false);

        return new DashboardCommandResponse(
            response.Kind switch
            {
                ResourceCommandResponseKind.Succeeded => "succeeded",
                ResourceCommandResponseKind.Failed => "failed",
                ResourceCommandResponseKind.Cancelled => "cancelled",
                ResourceCommandResponseKind.InvalidArguments => "invalidArguments",
                _ => "undefined"
            },
            response.HasMessage ? response.Message : null,
            response.Result is { } result
                ? new DashboardCommandResult(
                    result.Value,
                    result.Format switch
                    {
                        CommandResultFormat.Json => "json",
                        CommandResultFormat.Markdown => "markdown",
                        _ => "text"
                    },
                    result.DisplayImmediately)
                : null);
    }
}
