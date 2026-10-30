// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models a public certificate credential declared on a Microsoft Graph application.
/// </summary>
internal sealed class MicrosoftGraphKeyCredential : ProvisionableConstruct
{
    public BicepValue<string> CustomKeyIdentifier
    {
        get { Initialize(); return _customKeyIdentifier!; }
        set { Initialize(); _customKeyIdentifier!.Assign(value); }
    }
    private BicepValue<string>? _customKeyIdentifier;

    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }
    private BicepValue<string>? _displayName;

    public BicepValue<string> EndDateTime
    {
        get { Initialize(); return _endDateTime!; }
        set { Initialize(); _endDateTime!.Assign(value); }
    }
    private BicepValue<string>? _endDateTime;

    public BicepValue<string> Key
    {
        get { Initialize(); return _key!; }
        set { Initialize(); _key!.Assign(value); }
    }
    private BicepValue<string>? _key;

    public BicepValue<string> KeyId
    {
        get { Initialize(); return _keyId!; }
        set { Initialize(); _keyId!.Assign(value); }
    }
    private BicepValue<string>? _keyId;

    public BicepValue<string> StartDateTime
    {
        get { Initialize(); return _startDateTime!; }
        set { Initialize(); _startDateTime!.Assign(value); }
    }
    private BicepValue<string>? _startDateTime;

    public BicepValue<string> Type
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }
    private BicepValue<string>? _type;

    public BicepValue<string> Usage
    {
        get { Initialize(); return _usage!; }
        set { Initialize(); _usage!.Assign(value); }
    }
    private BicepValue<string>? _usage;

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _customKeyIdentifier = DefineProperty<string>(nameof(CustomKeyIdentifier), ["customKeyIdentifier"]);
        _displayName = DefineProperty<string>(nameof(DisplayName), ["displayName"]);
        _endDateTime = DefineProperty<string>(nameof(EndDateTime), ["endDateTime"]);
        _key = DefineProperty<string>(nameof(Key), ["key"], isRequired: true);
        _keyId = DefineProperty<string>(nameof(KeyId), ["keyId"]);
        _startDateTime = DefineProperty<string>(nameof(StartDateTime), ["startDateTime"]);
        _type = DefineProperty<string>(nameof(Type), ["type"], isRequired: true);
        _usage = DefineProperty<string>(nameof(Usage), ["usage"], isRequired: true);
    }
}
