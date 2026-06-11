// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;
using Azure.Provisioning.Roles;

namespace Aspire.Hosting.Azure;

/// <summary>
/// An Azure Provisioning resource that represents an Azure user assigned managed identity.
/// </summary>
public sealed class AzureUserAssignedIdentityResource(string name)
    : AzureProvisioningResource(name, ConfigureAppIdentityInfrastructure), IAppIdentityResource
{
    /// <summary>
    /// The identifier associated with the user assigned identity.
    /// </summary>
    public BicepOutputReference Id => new("id", this);

    /// <summary>
    /// The client ID of the user assigned identity.
    /// </summary>
    public BicepOutputReference ClientId => new("clientId", this);

    /// <summary>
    /// The principal ID of the user assigned identity.
    /// </summary>
    public BicepOutputReference PrincipalId => new("principalId", this);

    /// <summary>
    /// The principal name of the user assigned identity.
    /// </summary>
    public BicepOutputReference PrincipalName => new("principalName", this);

    /// <summary>
    /// The name of the user assigned identity.
    /// </summary>
    public BicepOutputReference NameOutputReference => new("name", this);

    private static void ConfigureAppIdentityInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var userAssignedIdentity = CreateExistingOrNewProvisionableResource(infrastructure,
            (identifier, name) =>
            {
                var resource = UserAssignedIdentity.FromExisting(identifier);
                resource.Name = name;
                return resource;
            },
            (infrastructure) =>
            {
                var identityName = Infrastructure.NormalizeBicepIdentifier(infrastructure.AspireResource.Name);
                var resource = new UserAssignedIdentity(identityName);
                return resource;
            });

        infrastructure.Add(userAssignedIdentity);

        infrastructure.Add(new ProvisioningOutput("id", typeof(string)) { Value = userAssignedIdentity.Id.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput("clientId", typeof(string)) { Value = userAssignedIdentity.ClientId.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput("principalId", typeof(string)) { Value = userAssignedIdentity.PrincipalId.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput("principalName", typeof(string)) { Value = userAssignedIdentity.Name.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput("name", typeof(string)) { Value = userAssignedIdentity.Name.ToBicepExpression() });
    }

    /// <inheritdoc/>
    public override ProvisionableResource AddAsExistingResource(AzureResourceInfrastructure infra)
    {
        var bicepIdentifier = this.GetBicepIdentifier();
        var resources = infra.GetProvisionableResources();
        
        // Check if a UserAssignedIdentity with the same identifier already exists
        var existingStore = resources.OfType<UserAssignedIdentity>().SingleOrDefault(store => store.BicepIdentifier == bicepIdentifier);
        
        if (existingStore is not null)
        {
            return existingStore;
        }
        
        // Create and add new resource if it doesn't exist
        var store = UserAssignedIdentity.FromExisting(bicepIdentifier);

        if (!TryApplyExistingResourceAnnotation(
            this,
            infra,
            store))
        {
            store.Name = PrincipalName.AsProvisioningParameter(infra);
        }

        infra.Add(store);
        return store;
    }
}
