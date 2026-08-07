// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Sandboxes.Provisioning;

internal sealed class ConnectorGatewayTriggerParameter : ProvisionableConstruct
{
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }
    private BicepValue<string>? _value;

    protected override void DefineProvisionableProperties()
    {
        _name = DefineProperty<string>(nameof(Name), ["name"], isRequired: true);
        _value = DefineProperty<string>(nameof(Value), ["value"], isRequired: true);
    }
}
