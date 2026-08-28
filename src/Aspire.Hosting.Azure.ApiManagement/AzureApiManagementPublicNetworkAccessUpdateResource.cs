// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Azure.Provisioning is experimental.
#pragma warning disable ASPIREAZURE001 // Azure environment APIs are experimental.
#pragma warning disable ASPIREAZURE003 // Azure provisioning APIs are experimental.
#pragma warning disable ASPIREPIPELINES001 // Pipeline APIs are experimental.

using System.Text.Json;
using Aspire.Hosting.Azure.ApiManagement.Provisioning;
using Aspire.Hosting.Pipelines;
using Azure;
using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Network;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;
using Azure.ResourceManager;
using Microsoft.Extensions.DependencyInjection;
using ArmGenericResourceData = global::Azure.ResourceManager.Resources.GenericResourceData;

namespace Aspire.Hosting.Azure;

internal sealed class AzureApiManagementPublicNetworkAccessUpdateResource
    : AzureProvisioningResource
{
    private const string ApiManagementServiceOperatorRoleId = "e022efe7-f5ba-4159-bbe4-b44f577e9b61";
    private const string ReaderRoleId = "acdd72a7-3385-48ef-bd42-f606fba81ae7";

    public AzureApiManagementPublicNetworkAccessUpdateResource(
        string name,
        AzureApiManagementResource apiManagement,
        AzurePrivateEndpointResource privateEndpoint)
        : base(name, BuildInfrastructure)
    {
        ApiManagement = apiManagement;
        PrivateEndpoint = privateEndpoint;

        // Publishing still emits the deployment script from BuildInfrastructure so the artifact is
        // independently deployable. During `aspire deploy`, replace the inherited Bicep provisioning
        // step with a host-side PATCH to avoid Azure Deployment Scripts' shared-key storage dependency.
        var bicepProvisioningStep = Annotations.OfType<PipelineStepAnnotation>().Single();
        Annotations.Remove(bicepProvisioningStep);
        Annotations.Add(new PipelineStepAnnotation(_ =>
        {
            ProvisioningTaskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

            var step = new PipelineStep
            {
                Name = $"provision-{name}",
                Description = $"Disables public network access for Azure API Management resource {apiManagement.Name}.",
                Action = DisablePublicNetworkAccessAsync,
                Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
            };
            step.RequiredBy(AzureEnvironmentResource.ProvisionInfrastructureStepName);
            return step;
        }));
    }

    public AzureApiManagementResource ApiManagement { get; }

    public AzurePrivateEndpointResource PrivateEndpoint { get; }

    private async Task DisablePublicNetworkAccessAsync(PipelineStepContext context)
    {
        var reportingTask = await context.ReportingStep
            .CreateTaskAsync(
                new MarkdownString($"Disabling public network access for **{ApiManagement.Name}**"),
                context.CancellationToken)
            .ConfigureAwait(false);

        await using (reportingTask.ConfigureAwait(false))
        {
            try
            {
                var apiManagementId = await ApiManagement.Id.GetValueAsync(context.CancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Missing resource ID output for API Management resource '{ApiManagement.Name}'.");
                var privateEndpointId = await PrivateEndpoint.Id.GetValueAsync(context.CancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Missing resource ID output for private endpoint '{PrivateEndpoint.Name}'.");
                var credential = context.Services.GetRequiredService<ITokenCredentialProvider>().TokenCredential;
                var armClientOptions = context.Services.GetRequiredService<ArmClientOptions>();
                var armClient = new ArmClient(credential, default, armClientOptions);

                await AzureApiManagementPublicNetworkAccessUpdater.DisableAsync(
                    armClient,
                    apiManagementId,
                    privateEndpointId,
                    context.CancellationToken).ConfigureAwait(false);

                ProvisioningTaskCompletionSource?.TrySetResult();
                await reportingTask.CompleteAsync(
                    new MarkdownString($"Disabled public network access for **{ApiManagement.Name}**"),
                    CompletionState.Completed,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ProvisioningTaskCompletionSource?.TrySetException(ex);
                await reportingTask.CompleteAsync(
                    new MarkdownString($"Failed to disable public network access for **{ApiManagement.Name}**: {ex.Message}"),
                    CompletionState.CompletedWithError,
                    context.CancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    private static void BuildInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var resource = (AzureApiManagementPublicNetworkAccessUpdateResource)infrastructure.AspireResource;
        var apiManagement =
            (ApiManagementServiceProvisioningResource)resource.ApiManagement.AddAsExistingResource(infrastructure);
        var privateEndpoint =
            (PrivateEndpoint)resource.PrivateEndpoint.AddAsExistingResource(infrastructure);

        var identity = new UserAssignedIdentity("_apim_disablePublicAccessIdentity")
        {
            Name = BicepFunction.Take(
                BicepFunction.Interpolate($"apim-network-id-{BicepFunction.GetUniqueString(apiManagement.Id)}"),
                128),
        };
        infrastructure.Add(identity);

        var roleDefinitionId = BicepFunction.GetSubscriptionResourceId(
            "Microsoft.Authorization/roleDefinitions",
            ApiManagementServiceOperatorRoleId);
        var roleAssignment = new RoleAssignment("_apim_disablePublicAccessRole")
        {
            Name = BicepFunction.CreateGuid(apiManagement.Id, identity.Id, roleDefinitionId),
            Scope = new IdentifierExpression(apiManagement.BicepIdentifier),
            PrincipalType = RoleManagementPrincipalType.ServicePrincipal,
            PrincipalId = identity.PrincipalId,
            RoleDefinitionId = roleDefinitionId,
        };
        infrastructure.Add(roleAssignment);

        var readerRoleDefinitionId = BicepFunction.GetSubscriptionResourceId(
            "Microsoft.Authorization/roleDefinitions",
            ReaderRoleId);
        var privateEndpointReaderRole = new RoleAssignment("_apim_privateEndpointReaderRole")
        {
            Name = BicepFunction.CreateGuid(privateEndpoint.Id, identity.Id, readerRoleDefinitionId),
            Scope = new IdentifierExpression(privateEndpoint.BicepIdentifier),
            PrincipalType = RoleManagementPrincipalType.ServicePrincipal,
            PrincipalId = identity.PrincipalId,
            RoleDefinitionId = readerRoleDefinitionId,
        };
        infrastructure.Add(privateEndpointReaderRole);

        var forceUpdateTag = new ProvisioningParameter("_apim_forceUpdateTag", typeof(string))
        {
            // utcNow() is valid only as a parameter default and is reevaluated for every deployment.
            Value = new BicepValue<string>(
                new FunctionCallExpression(new IdentifierExpression("utcNow"))),
        };
        infrastructure.Add(forceUpdateTag);

        var script = new AzureCliScript("_apim_disablePublicAccess", "2023-08-01")
        {
            Name = BicepFunction.Take(
                BicepFunction.Interpolate($"apim-network-update-{BicepFunction.GetUniqueString(apiManagement.Id)}"),
                64),
            AzCliVersion = "2.64.0",
            ForceUpdateTag = forceUpdateTag,
            RetentionInterval = TimeSpan.FromHours(1),
            Timeout = TimeSpan.FromMinutes(30),
            ScriptContent =
                """
                approved=false
                for attempt in $(seq 1 60); do
                  connection_state=$(az resource show \
                    --ids "${PRIVATE_ENDPOINT_ID}" \
                    --api-version 2024-05-01 \
                    --query "properties.privateLinkServiceConnections[0].properties.privateLinkServiceConnectionState.status" \
                    --output tsv)
                  case "${connection_state}" in
                    Approved)
                      approved=true
                      break
                      ;;
                    Rejected|Disconnected)
                      echo "The API Management private endpoint connection was ${connection_state}. Public network access was not disabled." >&2
                      exit 1
                      ;;
                  esac
                  sleep 10
                done

                if [ "${approved}" != "true" ]; then
                  echo "The API Management private endpoint connection was not approved before the timeout. Public network access was not disabled." >&2
                  exit 1
                fi

                updated=false
                for attempt in $(seq 1 30); do
                  if az resource update \
                    --ids "${APIM_ID}" \
                    --api-version 2025-03-01-preview \
                    --set properties.publicNetworkAccess=Disabled; then
                    updated=true
                    break
                  fi
                  sleep 10
                done

                if [ "${updated}" != "true" ]; then
                  echo "Failed to start the public network access update." >&2
                  exit 1
                fi

                for attempt in $(seq 1 60); do
                  public_access=$(az resource show \
                    --ids "${APIM_ID}" \
                    --api-version 2025-03-01-preview \
                    --query properties.publicNetworkAccess \
                    --output tsv)
                  if [ "${public_access}" = "Disabled" ]; then
                    exit 0
                  fi
                  sleep 10
                done

                echo "Failed to disable public network access after the private endpoint was created." >&2
                exit 1
                """.ReplaceLineEndings("\n"),
        };
        script.EnvironmentVariables.Add(new ScriptEnvironmentVariable
        {
            Name = "APIM_ID",
            Value = apiManagement.Id,
        });
        script.EnvironmentVariables.Add(new ScriptEnvironmentVariable
        {
            Name = "PRIVATE_ENDPOINT_ID",
            Value = privateEndpoint.Id,
        });
        script.Identity.IdentityType = ArmDeploymentScriptManagedIdentityType.UserAssigned;
        script.Identity.UserAssignedIdentities[
            BicepFunction.Interpolate($"{identity.Id}").Compile().ToString()] = new UserAssignedIdentityDetails();
        script.DependsOn.Add(roleAssignment);
        script.DependsOn.Add(privateEndpointReaderRole);
        script.DependsOn.Add(privateEndpoint);
        infrastructure.Add(script);
    }
}

internal static class AzureApiManagementPublicNetworkAccessUpdater
{
    private const int PrivateEndpointApprovalAttempts = 60;
    private const int PatchAttempts = 30;
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(10);

    public static async Task DisableAsync(
        ArmClient armClient,
        string apiManagementResourceId,
        string privateEndpointResourceId,
        CancellationToken cancellationToken)
    {
        var privateEndpoint = armClient.GetGenericResource(new ResourceIdentifier(privateEndpointResourceId));
        await WaitForPrivateEndpointApprovalAsync(
            async token =>
            {
                var response = await privateEndpoint.GetAsync(token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(response.Value.Data.Properties);
                return GetPrivateEndpointApprovalState(document.RootElement);
            },
            PrivateEndpointApprovalAttempts,
            s_pollInterval,
            cancellationToken).ConfigureAwait(false);

        var apiManagement = armClient.GetGenericResource(new ResourceIdentifier(apiManagementResourceId));
        var current = await apiManagement.GetAsync(cancellationToken).ConfigureAwait(false);
        var update = new ArmGenericResourceData(current.Value.Data.Location)
        {
            // GenericResource.UpdateAsync sends PATCH, so only this APIM property is mutated.
            Properties = BinaryData.FromObjectAsJson(new
            {
                publicNetworkAccess = "Disabled",
            }),
        };

        await UpdatePublicNetworkAccessAsync(
            apiManagement,
            update,
            cancellationToken).ConfigureAwait(false);

        var updated = await apiManagement.GetAsync(cancellationToken).ConfigureAwait(false);
        using var updatedProperties = JsonDocument.Parse(updated.Value.Data.Properties);
        if (!updatedProperties.RootElement.TryGetProperty("publicNetworkAccess", out var publicNetworkAccess) ||
            !publicNetworkAccess.ValueEquals("Disabled"))
        {
            throw new InvalidOperationException(
                "Azure API Management did not report public network access as disabled after the update completed.");
        }
    }

    internal static async Task WaitForPrivateEndpointApprovalAsync(
        Func<CancellationToken, Task<PrivateEndpointApprovalState>> getState,
        int attempts,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var state = await getState(cancellationToken).ConfigureAwait(false);
            if (state == PrivateEndpointApprovalState.Approved)
            {
                return;
            }

            if (state == PrivateEndpointApprovalState.Rejected)
            {
                throw new InvalidOperationException(
                    "The API Management private endpoint connection was rejected or disconnected. Public network access was not disabled.");
            }

            if (attempt < attempts)
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new TimeoutException(
            "The API Management private endpoint connection was not approved before the timeout. Public network access was not disabled.");
    }

    internal static PrivateEndpointApprovalState GetPrivateEndpointApprovalState(JsonElement privateEndpoint)
    {
        // GenericResourceData.Properties contains the value of the ARM "properties" object:
        // { "privateLinkServiceConnections": [{ "properties": {
        //   "privateLinkServiceConnectionState": { "status": "Approved" } } }] }
        var states = GetConnectionStates(privateEndpoint, "privateLinkServiceConnections")
            .Concat(GetConnectionStates(privateEndpoint, "manualPrivateLinkServiceConnections"))
            .ToArray();

        if (states.Any(static state =>
            state.Equals("Rejected", StringComparison.OrdinalIgnoreCase) ||
            state.Equals("Disconnected", StringComparison.OrdinalIgnoreCase)))
        {
            return PrivateEndpointApprovalState.Rejected;
        }

        return states.Length > 0 &&
            states.All(static state => state.Equals("Approved", StringComparison.OrdinalIgnoreCase))
                ? PrivateEndpointApprovalState.Approved
                : PrivateEndpointApprovalState.Pending;
    }

    private static IEnumerable<string> GetConnectionStates(JsonElement properties, string collectionName)
    {
        if (!properties.TryGetProperty(collectionName, out var connections) ||
            connections.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var connection in connections.EnumerateArray())
        {
            if (connection.TryGetProperty("properties", out var connectionProperties) &&
                connectionProperties.TryGetProperty("privateLinkServiceConnectionState", out var connectionState) &&
                connectionState.TryGetProperty("status", out var status) &&
                status.GetString() is { Length: > 0 } value)
            {
                yield return value;
            }
        }
    }

    private static async Task UpdatePublicNetworkAccessAsync(
        global::Azure.ResourceManager.Resources.GenericResource apiManagement,
        ArmGenericResourceData update,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= PatchAttempts; attempt++)
        {
            try
            {
                await apiManagement.UpdateAsync(
                    WaitUntil.Completed,
                    update,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 409 && attempt < PatchAttempts)
            {
                // A newly approved connection can briefly leave APIM busy even after ARM reports the
                // private endpoint as approved. Retry only that transient conflict; the SDK handles
                // throttling and server failures through its normal retry pipeline.
                await Task.Delay(s_pollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The API Management public network access update could not be completed.");
    }
}

internal enum PrivateEndpointApprovalState
{
    Pending,
    Approved,
    Rejected,
}
