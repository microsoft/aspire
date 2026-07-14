// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;
using Grpc.Core;
using Grpc.Net.Client;

namespace Aspire.Dashboard.Backend;

internal interface IDashboardCommandExecutor
{
    ValueTask<DashboardCommandResponse?> ExecuteAsync(
        DashboardExecuteCommandRequest request,
        CancellationToken cancellationToken);
}

internal sealed class DashboardCommandExecutor(
    IConfiguration configuration,
    IDashboardResourceSnapshotProvider resourceSnapshotProvider,
    ILoggerFactory loggerFactory) : IDashboardCommandExecutor
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

        var endpoint = configuration["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"]
            ?? configuration["DOTNET_RESOURCE_SERVICE_ENDPOINT_URL"];
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var resourceServiceUri))
        {
            throw new DashboardResourceServiceUnavailableException(
                "Configure ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL with the AppHost resource-service endpoint.");
        }

        using var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true };
        using var channel = GrpcChannel.ForAddress(resourceServiceUri, new GrpcChannelOptions
        {
            HttpHandler = handler,
            LoggerFactory = loggerFactory,
            ThrowOperationCanceledOnCancellation = true
        });
        var client = new DashboardService.Proto.V1.DashboardService.DashboardServiceClient(channel);
        var headers = new Metadata();
        if (string.Equals(configuration["Dashboard:ResourceServiceClient:AuthMode"], "ApiKey", StringComparison.OrdinalIgnoreCase)
            && configuration["Dashboard:ResourceServiceClient:ApiKey"] is { Length: > 0 } apiKey)
        {
            headers.Add("x-resource-service-api-key", apiKey);
        }

        var response = await client.ExecuteResourceCommandAsync(
            new ResourceCommandRequest
            {
                ResourceName = resource.Name,
                ResourceType = resource.ResourceType,
                CommandName = command.Name
            },
            headers,
            cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);

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
