// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Captures the ARM deployment operation fields Aspire surfaces while Azure provisioning is running.
/// </summary>
// ARM deployment operations are returned by:
//   GET /subscriptions/{subscriptionId}/resourcegroups/{resourceGroupName}/providers/Microsoft.Resources/deployments/{deploymentName}/operations
// See:
// - https://learn.microsoft.com/rest/api/resources/deployment-operations/list
// - https://learn.microsoft.com/rest/api/resources/deployment-operations/get
// The documented payload is shaped as:
//   {
//     "operationId": "...",
//     "properties": {
//       "provisioningOperation": "Create",
//       "provisioningState": "Failed",
//       "timestamp": "2026-06-11T06:39:22Z",
//       "duration": "PT15S",
//       "statusCode": "BadRequest",
//       "serviceRequestId": "...",
//       "targetResource": {
//         "id": "/subscriptions/.../providers/Microsoft.Search/searchServices/search",
//         "resourceType": "Microsoft.Search/searchServices",
//         "resourceName": "search"
//       },
//       "statusMessage": {
//         "error": {
//           "code": "LocationNotAvailableForResourceType",
//           "message": "..."
//         }
//       }
//     }
//   }
// Nested Bicep modules appear as Create operations targeting Microsoft.Resources/deployments.
// We retain the raw deployment ID and operation fields so Aspire can both render progress and
// recurse into nested deployments to find provider-level failures.
internal sealed record AzureDeploymentOperationDetails(
    string? OperationId,
    string DeploymentId,
    string? ProvisioningOperation,
    string? ProvisioningState,
    DateTimeOffset? Timestamp,
    TimeSpan? Duration,
    string? StatusCode,
    string? ServiceRequestId,
    AzureDeploymentOperationTarget? TargetResource,
    AzureProvisioningFailureDetails? FailureDetails)
{
    internal const string DeploymentResourceType = "Microsoft.Resources/deployments";
    internal const string CreateOperation = "Create";
    internal const string RunningState = "Running";
    internal const string SucceededState = "Succeeded";
    internal const string FailedState = "Failed";
    internal const string CanceledState = "Canceled";

    internal bool IsCreateOperation => string.Equals(ProvisioningOperation, CreateOperation, StringComparisons.AzureProvisioningOperation);

    // ARM templates commonly expand into nested Microsoft.Resources/deployments operations. Those
    // operations are control-plane scaffolding, not user resources, but they point to child
    // deployments that contain the provider-level status and errors we need to walk.
    internal bool IsNestedDeploymentCreate =>
        IsCreateOperation &&
        string.Equals(TargetResource?.ResourceType, DeploymentResourceType, StringComparisons.AzureResourceType);

    internal bool IsTerminal =>
        string.Equals(ProvisioningState, SucceededState, StringComparisons.AzureProvisioningState) ||
        string.Equals(ProvisioningState, FailedState, StringComparisons.AzureProvisioningState) ||
        string.Equals(ProvisioningState, CanceledState, StringComparisons.AzureProvisioningState);

    internal JsonObject ToJsonObject()
    {
        var json = new JsonObject
        {
            ["deploymentId"] = DeploymentId
        };

        if (!string.IsNullOrEmpty(OperationId))
        {
            json["operationId"] = OperationId;
        }

        if (!string.IsNullOrEmpty(ProvisioningOperation))
        {
            json["provisioningOperation"] = ProvisioningOperation;
        }

        if (!string.IsNullOrEmpty(ProvisioningState))
        {
            json["provisioningState"] = ProvisioningState;
        }

        if (Timestamp is { } timestamp)
        {
            json["timestamp"] = timestamp;
        }

        if (Duration is { } duration)
        {
            json["duration"] = duration.ToString();
        }

        if (!string.IsNullOrEmpty(StatusCode))
        {
            json["statusCode"] = StatusCode;
        }

        if (!string.IsNullOrEmpty(ServiceRequestId))
        {
            json["serviceRequestId"] = ServiceRequestId;
        }

        if (TargetResource is { } targetResource)
        {
            json["targetResource"] = targetResource.ToJsonObject();
        }

        if (FailureDetails is { } failureDetails)
        {
            json["error"] = failureDetails.ToJsonObject();
        }

        return json;
    }
}

internal sealed record AzureDeploymentOperationTarget(string? Id, string? ResourceType, string? ResourceName)
{
    internal JsonObject ToJsonObject()
    {
        var json = new JsonObject();

        if (!string.IsNullOrEmpty(Id))
        {
            json["id"] = Id;
        }

        if (!string.IsNullOrEmpty(ResourceType))
        {
            json["resourceType"] = ResourceType;
        }

        if (!string.IsNullOrEmpty(ResourceName))
        {
            json["resourceName"] = ResourceName;
        }

        return json;
    }
}

internal sealed record AzureDeploymentOperationSummary(
    ImmutableArray<AzureDeploymentOperationDetails> Operations,
    ImmutableArray<AzureDeploymentOperationDetails> RunningOperations,
    ImmutableArray<AzureDeploymentOperationDetails> SucceededOperations,
    ImmutableArray<AzureDeploymentOperationDetails> FailedOperations,
    ImmutableArray<AzureDeploymentOperationDetails> CanceledOperations)
{
    // Operations keeps the raw recursive ARM operation list for diagnostics. The grouped arrays
    // intentionally contain only displayable resource create operations so progress text does not
    // count nested deployment scaffolding as successfully provisioned resources.
    internal static AzureDeploymentOperationSummary Empty { get; } = new([], [], [], [], []);

    internal JsonObject ToJsonObject()
    {
        return new JsonObject
        {
            ["operations"] = ToJsonArray(Operations),
            ["running"] = ToJsonArray(RunningOperations),
            ["succeeded"] = ToJsonArray(SucceededOperations),
            ["failed"] = ToJsonArray(FailedOperations),
            ["canceled"] = ToJsonArray(CanceledOperations)
        };
    }

    private static JsonArray ToJsonArray(IEnumerable<AzureDeploymentOperationDetails> operations)
    {
        var array = new JsonArray();
        foreach (var operation in operations)
        {
            array.Add(operation.ToJsonObject());
        }

        return array;
    }
}
