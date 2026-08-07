// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayAccessPolicyPrincipalIdentity : ProvisionableConstruct
{
    public BicepValue<string> ObjectId
    {
        get { Initialize(); return _objectId!; }
        set { Initialize(); _objectId!.Assign(value); }
    }
    private BicepValue<string>? _objectId;

    public BicepValue<string> TenantId
    {
        get { Initialize(); return _tenantId!; }
        set { Initialize(); _tenantId!.Assign(value); }
    }
    private BicepValue<string>? _tenantId;

    protected override void DefineProvisionableProperties()
    {
        _objectId = DefineProperty<string>(nameof(ObjectId), ["objectId"], isRequired: true);
        _tenantId = DefineProperty<string>(nameof(TenantId), ["tenantId"], isRequired: true);
    }
}
