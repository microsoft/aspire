// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning.Resources;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class SandboxGroup(string bicepIdentifier)
    : global::Azure.Provisioning.AppContainers.SandboxGroup(bicepIdentifier)
{
    // The service documents group identities for registry pulls and workload tokens, but
    // the generated model and ARM specification omit identity. Extend only that property;
    // keep the generated resource's current API version and naming requirements.
    // https://sandboxes.azure.com/docs/sandboxes/identity
    // https://github.com/Azure/azure-rest-api-specs/blob/main/specification/app/resource-manager/Microsoft.App/ContainerApps/SandboxGroup.tsp
    public ManagedServiceIdentity Identity
    {
        get { Initialize(); return _identity!; }
    }
    private ManagedServiceIdentity? _identity;

    public static SandboxGroup FromExisting(string bicepIdentifier)
    {
        var resource = new SandboxGroup(bicepIdentifier)
        {
            IsExistingResource = true
        };

        return resource;
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _identity = DefineModelProperty<ManagedServiceIdentity>(nameof(Identity), ["identity"]);
    }
}
