// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable AZPROVISION001 // Azure.Provisioning is experimental.
#pragma warning disable ASPIREAZURE003 // Azure provisioning APIs are experimental.

using Aspire.Hosting.Azure.ApiManagement.Provisioning;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Resources;
using Azure.Provisioning.Roles;

namespace Aspire.Hosting.Azure;

internal sealed class AzureApiManagementPublicNetworkAccessUpdateResource(
    string name,
    AzureApiManagementResource apiManagement,
    AzurePrivateEndpointResource privateEndpoint)
    : AzureProvisioningResource(name, BuildInfrastructure)
{
    private const string ApiManagementServiceOperatorRoleId = "e022efe7-f5ba-4159-bbe4-b44f577e9b61";

    public AzureApiManagementResource ApiManagement { get; } = apiManagement;

    public AzurePrivateEndpointResource PrivateEndpoint { get; } = privateEndpoint;

    private static void BuildInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var resource = (AzureApiManagementPublicNetworkAccessUpdateResource)infrastructure.AspireResource;
        var apiManagement =
            (ApiManagementServiceProvisioningResource)resource.ApiManagement.AddAsExistingResource(infrastructure);
        var privateEndpoint = resource.PrivateEndpoint.AddAsExistingResource(infrastructure);

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
            Timeout = TimeSpan.FromMinutes(20),
            ScriptContent =
                """
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
                """,
        };
        script.EnvironmentVariables.Add(new ScriptEnvironmentVariable
        {
            Name = "APIM_ID",
            Value = apiManagement.Id,
        });
        script.Identity.IdentityType = ArmDeploymentScriptManagedIdentityType.UserAssigned;
        script.Identity.UserAssignedIdentities[
            BicepFunction.Interpolate($"{identity.Id}").Compile().ToString()] = new UserAssignedIdentityDetails();
        script.DependsOn.Add(roleAssignment);
        script.DependsOn.Add(privateEndpoint);
        infrastructure.Add(script);
    }
}
