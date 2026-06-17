// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Core;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayTriggerConfig(string bicepIdentifier, string? resourceVersion = null)
    : ProvisionableResource(bicepIdentifier, "Microsoft.Web/connectorGateways/triggerConfigs", resourceVersion ?? SandboxesResourceVersions.ConnectorGateway)
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

    public BicepValue<string> Description
    {
        get { Initialize(); return _description!; }
        set { Initialize(); _description!.Assign(value); }
    }
    private BicepValue<string>? _description;

    public ConnectorGatewayTriggerConnectionDetails ConnectionDetails
    {
        get { Initialize(); return _connectionDetails!; }
    }
    private ConnectorGatewayTriggerConnectionDetails? _connectionDetails;

    public BicepDictionary<string> Metadata
    {
        get { Initialize(); return _metadata!; }
        set { Initialize(); _metadata!.Assign(value); }
    }
    private BicepDictionary<string>? _metadata;

    public ConnectorGatewayTriggerNotificationDetails NotificationDetails
    {
        get { Initialize(); return _notificationDetails!; }
    }
    private ConnectorGatewayTriggerNotificationDetails? _notificationDetails;

    public BicepValue<string> OperationName
    {
        get { Initialize(); return _operationName!; }
        set { Initialize(); _operationName!.Assign(value); }
    }
    private BicepValue<string>? _operationName;

    public BicepList<ConnectorGatewayTriggerParameter> Parameters
    {
        get { Initialize(); return _parameters!; }
        set { Initialize(); _parameters!.Assign(value); }
    }
    private BicepList<ConnectorGatewayTriggerParameter>? _parameters;

    public BicepValue<string> State
    {
        get { Initialize(); return _state!; }
        set { Initialize(); _state!.Assign(value); }
    }
    private BicepValue<string>? _state;

    public ConnectorGateway? Parent
    {
        get { Initialize(); return _parent!.Value; }
        set { Initialize(); _parent!.Value = value; }
    }
    private ResourceReference<ConnectorGateway>? _parent;

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<ResourceIdentifier>(nameof(Id), ["id"], isOutput: true);
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _description = DefineProperty<string>(nameof(Description), ["properties", "description"]);
        _connectionDetails = DefineModelProperty<ConnectorGatewayTriggerConnectionDetails>(nameof(ConnectionDetails), ["properties", "connectionDetails"]);
        _metadata = DefineDictionaryProperty<string>(nameof(Metadata), ["properties", "metadata"]);
        _notificationDetails = DefineModelProperty<ConnectorGatewayTriggerNotificationDetails>(nameof(NotificationDetails), ["properties", "notificationDetails"]);
        _operationName = DefineProperty<string>(nameof(OperationName), ["properties", "operationName"], isRequired: true);
        _parameters = DefineListProperty<ConnectorGatewayTriggerParameter>(nameof(Parameters), ["properties", "parameters"]);
        _state = DefineProperty<string>(nameof(State), ["properties", "state"]);
        _parent = DefineResource<ConnectorGateway>(nameof(Parent), ["parent"], isRequired: true);
    }
}
