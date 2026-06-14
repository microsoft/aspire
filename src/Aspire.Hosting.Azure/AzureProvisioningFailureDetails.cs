// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Resources;
using Aspire.Hosting.Utils;
using Azure;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Carries normalized Azure provisioning failure diagnostics across resource state, command JSON, and rendered messages.
/// </summary>
/// <remarks>
/// Azure failures can arrive from several places: <see cref="RequestFailedException"/>, ARM deployment operations
/// (<c>properties.statusMessage.error</c>), or Aspire-local state checks such as missing cached resource IDs. ARM
/// often wraps the provider failure inside generic codes such as <c>DeploymentFailed</c> or
/// <c>ResourceDeploymentFailure</c>, while the useful provider code, target resource, request ID, and remediation
/// data live in nested <c>details[]</c> objects.
///
/// This record is the canonical shape used after parsing those inputs and before rendering diagnostics back to the
/// dashboard, resource snapshot properties, JSON command output, or human-readable recovery text. Keeping this
/// normalized representation separate from the rendered message prevents callers from scraping strings and lets
/// provisioning/recovery commands attach operation names, request IDs, target resources, supported locations, and
/// recommended actions consistently.
///
/// See the ARM error response and deployment operation payloads documented at:
/// https://learn.microsoft.com/rest/api/resources/deployments/validate?view=rest-resources-2025-04-01
/// https://learn.microsoft.com/rest/api/resources/deployment-operations/list?view=rest-resources-2025-04-01
/// </remarks>
internal sealed record AzureProvisioningFailureDetails(
    string Provider,
    string? ResourceType,
    string? ResourceName,
    string? TargetResourceId,
    string? CurrentLocation,
    ImmutableArray<string> SupportedLocations,
    int? HttpStatus,
    string? ErrorCode,
    string ErrorMessage,
    string? Operation,
    string? RequestId,
    string? CorrelationId,
    ImmutableArray<AzureProvisioningRecommendedAction> RecommendedActions)
{
    internal const string ProvisionOperation = "provision";
    internal const string LiveResourceCheckOperation = "live-resource-check";
    internal const string MissingResourceIdReason = "missing-resource-id";
    internal const string MissingLiveResourceReason = "missing-live-resource";
    internal const string ResourceGroupBeingDeletedErrorCode = "ResourceGroupBeingDeleted";
    internal const string LocationNotAvailableForResourceTypeErrorCode = "LocationNotAvailableForResourceType";
    internal const string SubscriptionNotFoundErrorCode = "SubscriptionNotFound";
    internal const string ServiceModelDeprecatedErrorCode = "ServiceModelDeprecated";
    internal const string InvalidResourcePropertiesErrorCode = "InvalidResourceProperties";

    private const string PropertyPrefix = "azure.provisioning.error.";

    internal bool IsLocationAvailabilityFailure =>
        string.Equals(ErrorCode, LocationNotAvailableForResourceTypeErrorCode, StringComparisons.AzureProvisioningErrorCode) &&
        !string.IsNullOrEmpty(ResourceType);

    internal static AzureProvisioningFailureDetails FromRequestFailedException(RequestFailedException requestEx, string? operation = null)
    {
        var response = requestEx.GetRawResponse();

        // RequestFailedException.Message is often flattened by Azure.Core. Keep parsing the raw
        // ARM body because the useful provider error is usually nested under details[] and can be
        // lost when only the exception message is rendered.
        var parsedError = TryParseResponseContent(response?.Content?.ToString());
        var errorCode = StringUtils.FirstNonEmpty(parsedError?.ErrorCode, requestEx.ErrorCode);
        var errorMessage = StringUtils.FirstNonEmpty(parsedError?.ErrorMessage, requestEx.Message) ?? requestEx.Message;
        var resourceType = StringUtils.FirstNonEmpty(parsedError?.ResourceType, TryGetResourceTypeFromProviderMessage(errorMessage));
        var requestId = TryGetHeaderValue(response, "x-ms-request-id");
        var correlationId = TryGetHeaderValue(response, "x-ms-correlation-request-id");

        if (string.IsNullOrEmpty(requestId))
        {
            requestId = response?.ClientRequestId;
        }

        return new(
            Provider: GetProvider(resourceType),
            ResourceType: resourceType,
            ResourceName: parsedError?.ResourceName,
            TargetResourceId: parsedError?.TargetResourceId,
            CurrentLocation: null,
            SupportedLocations: [],
            HttpStatus: requestEx.Status > 0 ? requestEx.Status : null,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage,
            Operation: StringUtils.FirstNonEmpty(operation, parsedError?.Operation),
            RequestId: requestId,
            CorrelationId: correlationId,
            RecommendedActions: GetRecommendedActions(errorCode));
    }

    internal static AzureProvisioningFailureDetails? TryCreate(Exception exception, string? operation = null)
    {
        foreach (var current in ExceptionUtils.EnumerateSelfAndInnerExceptions(exception))
        {
            if (current is AzureProvisioningFailureException provisioningFailureException)
            {
                return provisioningFailureException.FailureDetails;
            }

            if (current is RequestFailedException requestFailedException)
            {
                return FromRequestFailedException(requestFailedException, operation);
            }
        }

        return null;
    }

    internal static AzureProvisioningFailureDetails? FromResponseError(
        ResponseError? responseError,
        AzureDeploymentOperationTarget? targetResource,
        string? operation = null,
        string? statusCode = null,
        string? requestId = null,
        string? statusMessageContent = null)
    {
        var parsedError = TryParseResponseContent(statusMessageContent, targetResource);
        if (responseError is null && parsedError is null)
        {
            return null;
        }

        var errorCode = StringUtils.FirstNonEmpty(parsedError?.ErrorCode, responseError?.Code);
        var errorMessage = StringUtils.FirstNonEmpty(parsedError?.ErrorMessage, responseError?.Message);
        if (string.IsNullOrEmpty(errorCode) && string.IsNullOrEmpty(errorMessage))
        {
            return null;
        }

        errorMessage = StringUtils.FirstNonEmpty(errorMessage, errorCode) ?? "Azure deployment operation failed.";
        var resourceType = StringUtils.FirstNonEmpty(
            parsedError?.ResourceType,
            StringUtils.FirstNonEmpty(targetResource?.ResourceType, TryGetResourceTypeFromProviderMessage(errorMessage)));
        var httpStatus = int.TryParse(statusCode, out var parsedHttpStatus) ? parsedHttpStatus : (int?)null;

        return new(
            Provider: GetProvider(resourceType),
            ResourceType: resourceType,
            ResourceName: StringUtils.FirstNonEmpty(parsedError?.ResourceName, targetResource?.ResourceName),
            TargetResourceId: StringUtils.FirstNonEmpty(parsedError?.TargetResourceId, targetResource?.Id),
            CurrentLocation: null,
            SupportedLocations: [],
            HttpStatus: httpStatus,
            ErrorCode: errorCode,
            ErrorMessage: errorMessage,
            Operation: StringUtils.FirstNonEmpty(operation, parsedError?.Operation),
            RequestId: requestId,
            CorrelationId: null,
            RecommendedActions: GetRecommendedActions(errorCode));
    }

    internal AzureProvisioningFailureDetails WithLocationAvailability(string currentLocation, IEnumerable<string> supportedLocations)
    {
        var locations = supportedLocations
            .Where(static location => !string.IsNullOrWhiteSpace(location))
            .Distinct(StringComparers.AzureLocation)
            .OrderBy(static location => location, StringComparers.AzureLocation)
            .ToImmutableArray();

        return this with
        {
            CurrentLocation = currentLocation,
            SupportedLocations = locations,
            RecommendedActions = GetRecommendedActions(ErrorCode, locations)
        };
    }

    internal AzureProvisioningFailureDetails WithDeploymentRecommendedActions()
        => this with
        {
            RecommendedActions = GetDeploymentRecommendedActions(ErrorCode, SupportedLocations)
        };

    internal static ImmutableArray<AzureProvisioningRecommendedAction> GetRecommendedActions(
        string? errorCodeOrReason,
        ImmutableArray<string> supportedLocations = default)
    {
        if (string.Equals(errorCodeOrReason, ResourceGroupBeingDeletedErrorCode, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("change-resource-group", $"Run '{AzureProvisioningController.ChangeAzureContextCommandName}' with a different resource group, then reprovision."),
                Action("wait-for-deletion", "Wait for Azure to finish deleting the resource group before reusing that name.")
            ];
        }

        if (string.Equals(errorCodeOrReason, LocationNotAvailableForResourceTypeErrorCode, StringComparisons.AzureProvisioningErrorCode))
        {
            if (!supportedLocations.IsDefaultOrEmpty)
            {
                // The first supported location gives the CLI a concrete command to suggest instead
                // of forcing the user to choose from a long provider-specific region list.
                return
                [
                    Action("change-location", $"Run '{AzureProvisioningController.ChangeResourceLocationCommandName} --location {supportedLocations[0]}' or choose another supported region."),
                    Action("supported-locations", $"Supported regions include: {string.Join(", ", supportedLocations)}.")
                ];
            }

            return
            [
                Action("change-location", $"Run '{AzureProvisioningController.ChangeResourceLocationCommandName} --location <region>' with a supported Azure region."),
                Action("inspect-effective-location", $"Run '{AzureProvisioningController.GetAzureResourceCommandName}' to inspect the current effective location before retrying.")
            ];
        }

        // These Aspire-only reasons are shaped like provider errors so command output and resource
        // snapshots can use one diagnostics pipeline for both live Azure failures and forgotten or
        // incomplete local deployment state.
        if (string.Equals(errorCodeOrReason, MissingResourceIdReason, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("reprovision-or-change-context", $"Run '{AzureProvisioningController.ReprovisionResourceCommandName}' to recreate cached deployment state."),
                Action("change-resource-group", $"If the selected resource group is unavailable, run '{AzureProvisioningController.ChangeAzureContextCommandName}' with a different resource group.")
            ];
        }

        if (string.Equals(errorCodeOrReason, MissingLiveResourceReason, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("reprovision-or-forget-state", $"Run '{AzureProvisioningController.ReprovisionResourceCommandName}' to recreate the missing Azure resource, or '{AzureProvisioningController.ForgetStateCommandName}' if it was intentionally deleted."),
                Action("change-resource-group", $"Run '{AzureProvisioningController.ChangeAzureContextCommandName}' if the cached Azure context points at the wrong resource group.")
            ];
        }

        if (string.Equals(errorCodeOrReason, SubscriptionNotFoundErrorCode, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("change-subscription", $"Run '{AzureProvisioningController.ChangeAzureContextCommandName}' with a subscription that exists in the selected tenant."),
                Action("check-subscription-access", "Verify the signed-in Azure account can access the selected subscription.")
            ];
        }

        // Azure OpenAI repros showed model-deployment failures come back as provider-specific
        // validation codes rather than location errors:
        //   ServiceModelDeprecated: The model 'gpt-4o' with version '2024-05-13' has been deprecated.
        //   InvalidResourceProperties: The specified scale type 'Standard' is not supported by the model ...
        // Both point at model/version/SKU availability, so avoid sending users down the generic
        // resource-group or location troubleshooting path.
        if (string.Equals(errorCodeOrReason, ServiceModelDeprecatedErrorCode, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("choose-supported-model-version", "Update the Azure OpenAI deployment to a supported model and version for the selected region, then reprovision."),
                Action("inspect-model-availability", "Check Azure OpenAI model availability for the selected region before retrying.")
            ];
        }

        if (string.Equals(errorCodeOrReason, InvalidResourcePropertiesErrorCode, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("fix-resource-properties", "Update the Azure resource configuration to use properties supported by the selected provider, SKU, and region."),
                Action("inspect-provider-availability", "Check the provider error details and Azure SKU or model availability for the selected region before retrying.")
            ];
        }

        return [];
    }

    private static ImmutableArray<AzureProvisioningRecommendedAction> GetDeploymentRecommendedActions(
        string? errorCodeOrReason,
        ImmutableArray<string> supportedLocations)
    {
        if (string.Equals(errorCodeOrReason, ResourceGroupBeingDeletedErrorCode, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("set-resource-group", "Set the Azure:ResourceGroup configuration value, or Azure__ResourceGroup environment variable, to a different resource group before rerunning the deployment."),
                Action("wait-for-deletion", "Wait for Azure to finish deleting the resource group before reusing that name.")
            ];
        }

        if (string.Equals(errorCodeOrReason, LocationNotAvailableForResourceTypeErrorCode, StringComparisons.AzureProvisioningErrorCode))
        {
            if (!supportedLocations.IsDefaultOrEmpty)
            {
                return
                [
                    Action("set-location", $"Set the Azure:Location configuration value, or Azure__Location environment variable, to {supportedLocations[0]} or another supported region, then rerun the deployment."),
                    Action("clear-deployment-cache", "If the deployment environment already cached Azure context, rerun with --clear-cache so the new location is used.")
                ];
            }

            return
            [
                Action("set-location", "Set the Azure:Location configuration value, or Azure__Location environment variable, to a supported Azure region before rerunning the deployment."),
                Action("clear-deployment-cache", "If the deployment environment already cached Azure context, rerun with --clear-cache so the new location is used.")
            ];
        }

        if (string.Equals(errorCodeOrReason, MissingResourceIdReason, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("clear-deployment-cache", "Rerun the deployment with --clear-cache to rebuild cached deployment state."),
                Action("set-resource-group", "If the selected resource group is unavailable, set Azure:ResourceGroup or Azure__ResourceGroup to a different resource group.")
            ];
        }

        if (string.Equals(errorCodeOrReason, MissingLiveResourceReason, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("clear-deployment-cache", "Rerun the deployment with --clear-cache to recreate missing Azure resources."),
                Action("set-resource-group", "Set Azure:ResourceGroup or Azure__ResourceGroup if the cached Azure context points at the wrong resource group.")
            ];
        }

        if (string.Equals(errorCodeOrReason, SubscriptionNotFoundErrorCode, StringComparisons.AzureProvisioningErrorCode))
        {
            return
            [
                Action("set-subscription", "Set the Azure:SubscriptionId configuration value, or Azure__SubscriptionId environment variable, to a subscription in the selected tenant."),
                Action("check-subscription-access", "Verify the signed-in Azure account can access the selected subscription.")
            ];
        }

        return GetRecommendedActions(errorCodeOrReason, supportedLocations);
    }

    internal static JsonArray CreateRecommendedActionsJsonArray(IEnumerable<AzureProvisioningRecommendedAction> recommendedActions)
    {
        var array = new JsonArray();
        foreach (var action in recommendedActions)
        {
            array.Add(new JsonObject
            {
                ["code"] = action.Code,
                ["message"] = action.Message
            });
        }

        return array;
    }

    internal ImmutableArray<ResourcePropertySnapshot> SetResourceProperties(ImmutableArray<ResourcePropertySnapshot> properties, string fallbackOperation)
    {
        var failureProperties = ImmutableArray.CreateBuilder<ResourcePropertySnapshot>();
        failureProperties.Add(CreateHighlightedFailureProperty("provider", Provider, AzureProvisioningStrings.FailurePropertyProviderDisplayName));
        failureProperties.Add(CreateHighlightedFailureProperty("message", ErrorMessage, AzureProvisioningStrings.FailurePropertyMessageDisplayName));
        failureProperties.Add(CreateHighlightedFailureProperty("operation", Operation ?? fallbackOperation, AzureProvisioningStrings.FailurePropertyOperationDisplayName));

        if (!string.IsNullOrEmpty(ResourceType))
        {
            failureProperties.Add(CreateHighlightedFailureProperty("resource.type", ResourceType, AzureProvisioningStrings.FailurePropertyResourceTypeDisplayName));
        }

        if (!string.IsNullOrEmpty(ResourceName))
        {
            failureProperties.Add(CreateHighlightedFailureProperty("resource.name", ResourceName, AzureProvisioningStrings.FailurePropertyResourceNameDisplayName));
        }

        if (!string.IsNullOrEmpty(TargetResourceId))
        {
            failureProperties.Add(CreateHighlightedFailureProperty("target.resource.id", TargetResourceId, AzureProvisioningStrings.FailurePropertyTargetResourceIdDisplayName));
        }

        if (!string.IsNullOrEmpty(CurrentLocation))
        {
            failureProperties.Add(CreateHighlightedFailureProperty("current.location", CurrentLocation, AzureProvisioningStrings.FailurePropertyCurrentLocationDisplayName));
        }

        if (!SupportedLocations.IsDefaultOrEmpty)
        {
            failureProperties.Add(CreateHighlightedFailureProperty("supported.locations", SupportedLocations.ToArray(), AzureProvisioningStrings.FailurePropertySupportedLocationsDisplayName));
        }

        if (!string.IsNullOrEmpty(ErrorCode))
        {
            failureProperties.Add(CreateHighlightedFailureProperty("code", ErrorCode, AzureProvisioningStrings.FailurePropertyCodeDisplayName));
        }

        if (HttpStatus is { } httpStatus)
        {
            failureProperties.Add(CreateHighlightedFailureProperty("http.status", httpStatus, AzureProvisioningStrings.FailurePropertyHttpStatusDisplayName));
        }

        if (!string.IsNullOrEmpty(RequestId))
        {
            failureProperties.Add(CreateHighlightedFailureProperty("request.id", RequestId, AzureProvisioningStrings.FailurePropertyRequestIdDisplayName));
        }

        if (!string.IsNullOrEmpty(CorrelationId))
        {
            failureProperties.Add(CreateHighlightedFailureProperty("correlation.id", CorrelationId, AzureProvisioningStrings.FailurePropertyCorrelationIdDisplayName));
        }

        if (!RecommendedActions.IsDefaultOrEmpty)
        {
            failureProperties.Add(CreateHighlightedFailureProperty("recommendedActions", RecommendedActions.Select(static action => action.Message).ToArray(), AzureProvisioningStrings.FailurePropertyRecommendedActionsDisplayName));
        }

        return properties.WithoutAzureProvisioningFailureProperties().SetResourcePropertyRange(failureProperties);
    }

    internal static bool IsFailureProperty(string propertyName)
        => propertyName.StartsWith(PropertyPrefix, StringComparison.Ordinal);

    private static ResourcePropertySnapshot CreateHighlightedFailureProperty(string name, object value, string displayName)
        => new($"{PropertyPrefix}{name}", value) { DisplayName = displayName, IsHighlighted = true };

    internal JsonObject ToJsonObject()
    {
        var json = new JsonObject
        {
            ["source"] = "azure",
            ["provider"] = Provider,
            ["errorMessage"] = ErrorMessage
        };

        if (!string.IsNullOrEmpty(ErrorCode))
        {
            json["errorCode"] = ErrorCode;
        }

        if (!string.IsNullOrEmpty(ResourceType))
        {
            json["resourceType"] = ResourceType;
        }

        if (!string.IsNullOrEmpty(ResourceName))
        {
            json["resourceName"] = ResourceName;
        }

        if (!string.IsNullOrEmpty(TargetResourceId))
        {
            json["targetResourceId"] = TargetResourceId;
        }

        if (!string.IsNullOrEmpty(CurrentLocation))
        {
            json["currentLocation"] = CurrentLocation;
        }

        if (!SupportedLocations.IsDefaultOrEmpty)
        {
            var supportedLocations = new JsonArray();
            foreach (var location in SupportedLocations)
            {
                supportedLocations.Add(location);
            }

            json["supportedLocations"] = supportedLocations;
        }

        if (HttpStatus is { } httpStatus)
        {
            json["httpStatus"] = httpStatus;
        }

        if (!string.IsNullOrEmpty(Operation))
        {
            json["operation"] = Operation;
        }

        if (!string.IsNullOrEmpty(RequestId))
        {
            json["requestId"] = RequestId;
        }

        if (!string.IsNullOrEmpty(CorrelationId))
        {
            json["correlationId"] = CorrelationId;
        }

        if (!RecommendedActions.IsDefaultOrEmpty)
        {
            json["recommendedActions"] = CreateRecommendedActionsJsonArray(RecommendedActions);
        }

        return json;
    }

    internal string ToDetailedMessage()
    {
        if (!string.IsNullOrEmpty(ErrorCode))
        {
            return $"Error code = {ErrorCode}, Message = {ErrorMessage}";
        }

        return ErrorMessage;
    }

    internal string ToCommandMessage()
    {
        var message = !string.IsNullOrEmpty(ErrorCode)
            ? $"{ErrorCode}: {ErrorMessage}"
            : ErrorMessage;

        return RecommendedActions.IsDefaultOrEmpty
            ? message
            : $"{message} Recommended action: {RecommendedActions[0].Message}";
    }

    private static string? TryGetHeaderValue(Response? response, string name)
    {
        return response?.Headers.TryGetValue(name, out var value) == true && !string.IsNullOrEmpty(value)
            ? value
            : null;
    }

    private static ParsedAzureError? TryParseResponseContent(string? content, AzureDeploymentOperationTarget? fallbackTargetResource = null)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            // Azure.ResourceManager surfaces some provider failures as raw JSON in the response
            // body, while test fakes and older SDK paths can surface only plain text. Treat parse
            // failures as "no structured details" so the caller still falls back to ErrorCode/Message.
            if (JsonNode.Parse(content) is not JsonObject responseObj)
            {
                return null;
            }

            var targetResource = TryParseTargetResource(responseObj) ?? CreateParsedAzureTargetResource(fallbackTargetResource);
            return TryGetErrorObject(responseObj) is { } errorObj
                ? TryParseAzureError(errorObj, targetResource)
                : TryParseAzureError(responseObj, targetResource);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject? TryGetErrorObject(JsonObject responseObj)
    {
        // ARM errors observed during repros used two top-level shapes:
        //   { "error": { "code": "LocationNotAvailableForResourceType", "message": "..." } }
        //   { "properties": { "error": { "code": "DeploymentFailed", "details": [...] } } }
        // The deployment-operations REST API additionally documents:
        //   { "properties": { "statusMessage": { "error": { "code": "InvalidAccountType", "message": "..." } } } }
        // See https://learn.microsoft.com/rest/api/resources/deployment-operations/list
        // The deployment-operation shape often starts with generic wrapper codes, so parse these
        // forms and let TryParseAzureError promote the actionable provider failure from details[].
        if (responseObj["error"] is JsonObject errorObj)
        {
            return errorObj;
        }

        if (responseObj["properties"] is JsonObject propertiesObj &&
            propertiesObj["error"] is JsonObject deploymentErrorObj)
        {
            return deploymentErrorObj;
        }

        if (responseObj["properties"] is JsonObject operationPropertiesObj &&
            operationPropertiesObj["statusMessage"] is JsonObject statusMessageObj &&
            statusMessageObj["error"] is JsonObject statusMessageErrorObj)
        {
            return statusMessageErrorObj;
        }

        return null;
    }

    private static ParsedAzureError? TryParseAzureError(JsonObject errorObj, ParsedAzureTargetResource? targetResource)
    {
        var current = CreateParsedAzureError(errorObj, targetResource);

        // Deployment failures can be nested as:
        //   DeploymentFailed -> ResourceDeploymentFailure -> LocationNotAvailableForResourceType
        //   DeploymentFailed -> ResourceDeploymentFailure -> ServiceModelDeprecated
        // The outer codes explain that the deployment failed, but the inner provider code/message
        // is what tells the user which resource, model, SKU, or region to fix.
        if (errorObj["details"] is JsonArray detailsArray)
        {
            foreach (var detail in detailsArray)
            {
                if (detail is JsonObject detailObj &&
                    TryParseAzureError(detailObj, targetResource) is { } nested)
                {
                    if (!string.IsNullOrEmpty(nested.ErrorMessage))
                    {
                        return nested;
                    }

                    current ??= nested;
                }
            }
        }

        return current;
    }

    // Normalize the small set of Azure error fields we need from either an ARM error response or
    // a deployment-operation statusMessage error. Observed/documented shapes include:
    //   {
    //     "code": "ResourceDeploymentFailure",
    //     "message": "The resource write operation failed...",
    //     "details": [
    //       {
    //         "code": "LocationNotAvailableForResourceType",
    //         "message": "The provided location 'australiacentral' is not available...",
    //         "target": "search",
    //         "resourceType": "Microsoft.Search/searchServices"
    //       }
    //     ]
    //   }
    // Deployment operations add target resource metadata beside the error:
    //   {
    //     "properties": {
    //       "targetResource": {
    //         "id": "/subscriptions/.../providers/Microsoft.Search/searchServices/search",
    //         "resourceName": "search",
    //         "resourceType": "Microsoft.Search/searchServices"
    //       },
    //       "statusMessage": {
    //         "error": { "code": "LocationNotAvailableForResourceType", "message": "..." }
    //       }
    //     }
    //   }
    // Azure providers are not perfectly consistent about property names, so this accepts both ARM
    // names (code/message/target) and provider-specific aliases (errorCode/errorMessage) before
    // TryParseAzureError decides whether a nested details[] entry is more actionable.
    private static ParsedAzureError? CreateParsedAzureError(JsonObject errorObj, ParsedAzureTargetResource? targetResource)
    {
        var code = StringUtils.FirstNonEmpty(errorObj["code"]?.ToString(), errorObj["errorCode"]?.ToString());
        var message = StringUtils.FirstNonEmpty(errorObj["message"]?.ToString(), errorObj["errorMessage"]?.ToString());
        var operation = StringUtils.FirstNonEmpty(errorObj["operation"]?.ToString(), errorObj["target"]?.ToString());
        var resourceType = StringUtils.FirstNonEmpty(
            errorObj["resourceType"]?.ToString(),
            StringUtils.FirstNonEmpty(targetResource?.ResourceType, TryGetResourceTypeFromProviderMessage(message)));
        var resourceName = StringUtils.FirstNonEmpty(errorObj["resourceName"]?.ToString(), targetResource?.ResourceName);
        var targetResourceId = StringUtils.FirstNonEmpty(errorObj["targetResourceId"]?.ToString(), targetResource?.Id);

        return !string.IsNullOrEmpty(code) ||
               !string.IsNullOrEmpty(message) ||
               !string.IsNullOrEmpty(operation) ||
               !string.IsNullOrEmpty(resourceType) ||
               !string.IsNullOrEmpty(resourceName) ||
               !string.IsNullOrEmpty(targetResourceId)
            ? new ParsedAzureError(code, message, operation, resourceType, resourceName, targetResourceId)
            : null;
    }

    // Deployment-operation responses carry target resource metadata alongside the status/error:
    //   {
    //     "properties": {
    //       "provisioningOperation": "Create",
    //       "provisioningState": "Failed",
    //       "targetResource": {
    //         "id": "/subscriptions/.../resourceGroups/rg/providers/Microsoft.Search/searchServices/search",
    //         "resourceName": "search",
    //         "resourceType": "Microsoft.Search/searchServices"
    //       },
    //       "statusMessage": {
    //         "error": { "code": "LocationNotAvailableForResourceType", "message": "..." }
    //       }
    //     }
    //   }
    // Plain ARM error bodies usually do not include this object, so absence simply means the caller
    // falls back to resource information parsed from the provider error message.
    private static ParsedAzureTargetResource? TryParseTargetResource(JsonObject responseObj)
    {
        if (responseObj["properties"] is not JsonObject propertiesObj ||
            propertiesObj["targetResource"] is not JsonObject targetResourceObj)
        {
            return null;
        }

        var id = targetResourceObj["id"]?.ToString();
        var resourceName = targetResourceObj["resourceName"]?.ToString();
        var resourceType = targetResourceObj["resourceType"]?.ToString();

        return !string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(resourceName) || !string.IsNullOrEmpty(resourceType)
            ? new ParsedAzureTargetResource(id, resourceName, resourceType)
            : null;
    }

    private static string GetProvider(string? resourceType)
    {
        if (string.IsNullOrEmpty(resourceType))
        {
            return "Azure";
        }

        var providerSeparator = resourceType.IndexOf('/');
        return providerSeparator > 0
            ? resourceType[..providerSeparator]
            : "Azure";
    }

    private static string? TryGetResourceTypeFromProviderMessage(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return null;
        }

        // This is a best-effort fallback for older provider errors that omit structured
        // resourceType/targetResource metadata. If the provider localizes or changes the message,
        // parsing fails closed and callers still preserve the original error code and message.
        // ARM provider messages commonly include the target type as:
        //   The provided location 'invalidlocationxyz' is not available for resource type 'Microsoft.Search/searchServices'.
        //   The provided location 'invalidlocationxyz' is not available for resource type 'Microsoft.DocumentDB/databaseAccounts'.
        // The response body does not always include that value as a structured field, so preserve it from the message.
        const string marker = "resource type ";
        var markerIndex = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var valueStart = markerIndex + marker.Length;
        if (valueStart >= message.Length)
        {
            return null;
        }

        var quote = message[valueStart];
        if (quote is not ('\'' or '"'))
        {
            return null;
        }

        valueStart++;
        var valueEnd = message.IndexOf(quote, valueStart);
        return valueEnd > valueStart
            ? message[valueStart..valueEnd]
            : null;
    }

    private static ParsedAzureTargetResource? CreateParsedAzureTargetResource(AzureDeploymentOperationTarget? targetResource)
        => targetResource is not null
            ? new ParsedAzureTargetResource(targetResource.Id, targetResource.ResourceName, targetResource.ResourceType)
            : null;

    private static AzureProvisioningRecommendedAction Action(string code, string message)
        => new(code, message);

    private sealed record ParsedAzureError(string? ErrorCode, string? ErrorMessage, string? Operation, string? ResourceType, string? ResourceName, string? TargetResourceId);

    private sealed record ParsedAzureTargetResource(string? Id, string? ResourceName, string? ResourceType);
}

internal static class AzureProvisioningFailureResourcePropertyExtensions
{
    internal static ImmutableArray<ResourcePropertySnapshot> WithoutAzureProvisioningFailureProperties(this ImmutableArray<ResourcePropertySnapshot> properties)
    {
        if (properties.IsDefaultOrEmpty)
        {
            return [];
        }

        if (!properties.Any(static property => AzureProvisioningFailureDetails.IsFailureProperty(property.Name)))
        {
            return properties;
        }

        return [.. properties.Where(static property => !AzureProvisioningFailureDetails.IsFailureProperty(property.Name))];
    }
}

internal sealed class AzureProvisioningFailureException : Exception
{
    public AzureProvisioningFailureException(AzureProvisioningFailureDetails failureDetails, Exception innerException)
        : base(failureDetails.ToCommandMessage(), innerException)
    {
        FailureDetails = failureDetails;
    }

    public AzureProvisioningFailureDetails FailureDetails { get; }
}

internal sealed record AzureProvisioningRecommendedAction(string Code, string Message);
