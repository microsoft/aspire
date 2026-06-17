// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayMcpConnector : ProvisionableConstruct
{
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    public BicepValue<string> ConnectionName
    {
        get { Initialize(); return _connectionName!; }
        set { Initialize(); _connectionName!.Assign(value); }
    }
    private BicepValue<string>? _connectionName;

    public BicepList<ConnectorGatewayMcpOperation> Operations
    {
        get { Initialize(); return _operations!; }
        set { Initialize(); _operations!.Assign(value); }
    }
    private BicepList<ConnectorGatewayMcpOperation>? _operations;

    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _connectionName = DefineProperty<string>(nameof(ConnectionName), ["connectionName"], isRequired: true);
        _operations = DefineListProperty<ConnectorGatewayMcpOperation>(nameof(Operations), ["operations"], isRequired: true);
    }
}
