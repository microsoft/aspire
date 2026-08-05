// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayConnectionAccessPolicy(string bicepIdentifier, string? resourceVersion = null)
    : ProvisionableResource(bicepIdentifier, "Microsoft.Web/connectorGateways/connections/accessPolicies", resourceVersion ?? SandboxesResourceVersions.ConnectorGateway)
{
    public BicepValue<ResourceIdentifier> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<ResourceIdentifier>? _id;

    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    public BicepValue<AzureLocation> Location
    {
        get { Initialize(); return _location!; }
        set { Initialize(); _location!.Assign(value); }
    }
    private BicepValue<AzureLocation>? _location;

    public ConnectorGatewayAccessPolicyPrincipal Principal
    {
        get { Initialize(); return _principal!; }
    }
    private ConnectorGatewayAccessPolicyPrincipal? _principal;

    public ConnectorGatewayConnection? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }
    private ResourceReference<ConnectorGatewayConnection>? _parent;

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<ResourceIdentifier>(nameof(Id), ["id"], isOutput: true);
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _location = DefineProperty<AzureLocation>(nameof(Location), ["location"], isRequired: true);
        _principal = DefineModelProperty<ConnectorGatewayAccessPolicyPrincipal>(nameof(Principal), ["properties", "principal"]);
        _parent = DefineResource<ConnectorGatewayConnection>(nameof(Parent), ["parent"], isRequired: true);
    }
}
