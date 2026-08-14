// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayConnection(string bicepIdentifier, string? resourceVersion = null)
    : ProvisionableResource(bicepIdentifier, "Microsoft.Web/connectorGateways/connections", resourceVersion ?? SandboxesResourceVersions.ConnectorGateway)
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

    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }
    private BicepValue<string>? _displayName;

    public BicepValue<string> ConnectorName
    {
        get { Initialize(); return _connectorName!; }
        set { Initialize(); _connectorName!.Assign(value); }
    }
    private BicepValue<string>? _connectorName;

    public BicepValue<string> ConnectionRuntimeUrl
    {
        get { Initialize(); return _connectionRuntimeUrl!; }
    }
    private BicepValue<string>? _connectionRuntimeUrl;

    public ConnectorGateway? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }
    private ResourceReference<ConnectorGateway>? _parent;

    public static ConnectorGatewayConnection FromExisting(string bicepIdentifier, string? resourceVersion = null)
    {
        var resource = new ConnectorGatewayConnection(bicepIdentifier, resourceVersion)
        {
            IsExistingResource = true
        };

        return resource;
    }

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<ResourceIdentifier>(nameof(Id), ["id"], isOutput: true);
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _displayName = DefineProperty<string>(nameof(DisplayName), ["properties", "displayName"]);
        _connectorName = DefineProperty<string>(nameof(ConnectorName), ["properties", "connectorName"], isRequired: true);
        _connectionRuntimeUrl = DefineProperty<string>(nameof(ConnectionRuntimeUrl), ["properties", "connectionRuntimeUrl"], isOutput: true);
        _parent = DefineResource<ConnectorGateway>(nameof(Parent), ["parent"], isRequired: true);
    }
}
