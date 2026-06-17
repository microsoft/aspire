// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayTriggerNotificationDetails : ProvisionableConstruct
{
    public ConnectorGatewayTriggerAuthentication Authentication
    {
        get { Initialize(); return _authentication!; }
    }
    private ConnectorGatewayTriggerAuthentication? _authentication;

    public BicepValue<string> CallbackUrl
    {
        get { Initialize(); return _callbackUrl!; }
        set { Initialize(); _callbackUrl!.Assign(value); }
    }
    private BicepValue<string>? _callbackUrl;

    public BicepValue<string> HttpMethod
    {
        get { Initialize(); return _httpMethod!; }
        set { Initialize(); _httpMethod!.Assign(value); }
    }
    private BicepValue<string>? _httpMethod;

    protected override void DefineProvisionableProperties()
    {
        _authentication = DefineModelProperty<ConnectorGatewayTriggerAuthentication>(nameof(Authentication), ["authentication"]);
        _callbackUrl = DefineProperty<string>(nameof(CallbackUrl), ["callbackUrl"], isRequired: true);
        _httpMethod = DefineProperty<string>(nameof(HttpMethod), ["httpMethod"], isRequired: true);
    }
}
