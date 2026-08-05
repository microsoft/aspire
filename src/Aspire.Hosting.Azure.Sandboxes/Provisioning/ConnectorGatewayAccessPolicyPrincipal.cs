// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayAccessPolicyPrincipal : ProvisionableConstruct
{
    public BicepValue<string> Type
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }
    private BicepValue<string>? _type;

    public ConnectorGatewayAccessPolicyPrincipalIdentity Identity
    {
        get { Initialize(); return _identity!; }
    }
    private ConnectorGatewayAccessPolicyPrincipalIdentity? _identity;

    protected override void DefineProvisionableProperties()
    {
        _type = DefineProperty<string>(nameof(Type), ["type"], isRequired: true);
        _identity = DefineModelProperty<ConnectorGatewayAccessPolicyPrincipalIdentity>(nameof(Identity), ["identity"]);
    }
}
