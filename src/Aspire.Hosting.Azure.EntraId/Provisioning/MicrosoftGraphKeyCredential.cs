// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models a public certificate credential declared on a Microsoft Graph application.
/// </summary>
public sealed class MicrosoftGraphKeyCredential : ProvisionableConstruct
{
    /// <summary>Gets or sets the custom identifier for the key, encoded as base64.</summary>
    public BicepValue<string> CustomKeyIdentifier
    {
        get { Initialize(); return _customKeyIdentifier!; }
        set { Initialize(); _customKeyIdentifier!.Assign(value); }
    }
    private BicepValue<string>? _customKeyIdentifier;

    /// <summary>Gets or sets the credential's display name.</summary>
    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }
    private BicepValue<string>? _displayName;

    /// <summary>Gets or sets the credential's expiration date and time in ISO 8601 format.</summary>
    public BicepValue<string> EndDateTime
    {
        get { Initialize(); return _endDateTime!; }
        set { Initialize(); _endDateTime!.Assign(value); }
    }
    private BicepValue<string>? _endDateTime;

    /// <summary>Gets or sets the base64-encoded public certificate. Do not supply a private key.</summary>
    public BicepValue<string> Key
    {
        get { Initialize(); return _key!; }
        set { Initialize(); _key!.Assign(value); }
    }
    private BicepValue<string>? _key;

    /// <summary>Gets or sets the credential's key ID.</summary>
    public BicepValue<string> KeyId
    {
        get { Initialize(); return _keyId!; }
        set { Initialize(); _keyId!.Assign(value); }
    }
    private BicepValue<string>? _keyId;

    /// <summary>Gets or sets when the credential becomes valid, in ISO 8601 format.</summary>
    public BicepValue<string> StartDateTime
    {
        get { Initialize(); return _startDateTime!; }
        set { Initialize(); _startDateTime!.Assign(value); }
    }
    private BicepValue<string>? _startDateTime;

    /// <summary>Gets or sets the key type, such as <c>AsymmetricX509Cert</c>.</summary>
    public BicepValue<string> Type
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }
    private BicepValue<string>? _type;

    /// <summary>Gets or sets the key usage, such as <c>Verify</c>.</summary>
    public BicepValue<string> Usage
    {
        get { Initialize(); return _usage!; }
        set { Initialize(); _usage!.Assign(value); }
    }
    private BicepValue<string>? _usage;

    /// <inheritdoc />
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
