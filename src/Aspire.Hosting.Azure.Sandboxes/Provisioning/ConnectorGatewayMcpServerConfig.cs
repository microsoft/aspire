// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayMcpServerConfig(string bicepIdentifier, string? resourceVersion = null)
    : ProvisionableResource(bicepIdentifier, "Microsoft.Web/connectorGateways/mcpserverConfigs", resourceVersion ?? SandboxesResourceVersions.ConnectorGateway)
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

    public BicepValue<string> Kind
    {
        get { Initialize(); return _kind!; }
        set { Initialize(); _kind!.Assign(value); }
    }
    private BicepValue<string>? _kind;

    public BicepValue<string> Description
    {
        get { Initialize(); return _description!; }
        set { Initialize(); _description!.Assign(value); }
    }
    private BicepValue<string>? _description;

    public BicepList<ConnectorGatewayMcpConnector> Connectors
    {
        get { Initialize(); return _connectors!; }
        set { Initialize(); _connectors!.Assign(value); }
    }
    private BicepList<ConnectorGatewayMcpConnector>? _connectors;

    public ConnectorGateway? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }
    private ResourceReference<ConnectorGateway>? _parent;

    public static ConnectorGatewayMcpServerConfig FromExisting(string bicepIdentifier, string? resourceVersion = null)
    {
        var resource = new ConnectorGatewayMcpServerConfig(bicepIdentifier, resourceVersion)
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
        _kind = DefineProperty<string>(nameof(Kind), ["kind"], isRequired: true);
        _description = DefineProperty<string>(nameof(Description), ["properties", "description"]);
        _connectors = DefineListProperty<ConnectorGatewayMcpConnector>(nameof(Connectors), ["properties", "connectors"]);
        _parent = DefineResource<ConnectorGateway>(nameof(Parent), ["parent"], isRequired: true);
    }
}
