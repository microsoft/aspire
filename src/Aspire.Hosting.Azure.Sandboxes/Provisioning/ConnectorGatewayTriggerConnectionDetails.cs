// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayTriggerConnectionDetails : ProvisionableConstruct
{
    public BicepValue<string> ConnectorName
    {
        get { Initialize(); return _connectorName!; }
        set { Initialize(); _connectorName!.Assign(value); }
    }
    private BicepValue<string>? _connectorName;

    public BicepValue<string> ConnectionName
    {
        get { Initialize(); return _connectionName!; }
        set { Initialize(); _connectionName!.Assign(value); }
    }
    private BicepValue<string>? _connectionName;

    protected override void DefineProvisionableProperties()
    {
        _connectorName = DefineProperty<string>(nameof(ConnectorName), ["connectorName"], isRequired: true);
        _connectionName = DefineProperty<string>(nameof(ConnectionName), ["connectionName"], isRequired: true);
    }
}
