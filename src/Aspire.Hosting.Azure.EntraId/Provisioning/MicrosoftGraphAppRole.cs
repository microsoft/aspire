// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models an application role declared on a Microsoft Graph application.
/// </summary>
public sealed class MicrosoftGraphAppRole : ProvisionableConstruct
{
    /// <summary>Gets or sets the stable ID of the application role.</summary>
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
        set { Initialize(); _id!.Assign(value); }
    }
    private BicepValue<string>? _id;

    /// <summary>Gets or sets the role's display name.</summary>
    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }
    private BicepValue<string>? _displayName;

    /// <summary>Gets or sets the role's description.</summary>
    public BicepValue<string> Description
    {
        get { Initialize(); return _description!; }
        set { Initialize(); _description!.Assign(value); }
    }
    private BicepValue<string>? _description;

    /// <summary>Gets or sets the role value included in access tokens.</summary>
    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }
    private BicepValue<string>? _value;

    /// <summary>Gets or sets whether the role is available for assignment.</summary>
    public BicepValue<bool> IsEnabled
    {
        get { Initialize(); return _isEnabled!; }
        set { Initialize(); _isEnabled!.Assign(value); }
    }
    private BicepValue<bool>? _isEnabled;

    /// <summary>Gets or sets who can receive the role: <c>User</c>, <c>Application</c>, or both.</summary>
    public BicepList<string> AllowedMemberTypes
    {
        get { Initialize(); return _allowedMemberTypes!; }
        set { Initialize(); _allowedMemberTypes!.Assign(value); }
    }
    private BicepList<string>? _allowedMemberTypes;

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<string>(nameof(Id), ["id"], isRequired: true);
        _displayName = DefineProperty<string>(nameof(DisplayName), ["displayName"], isRequired: true);
        _description = DefineProperty<string>(nameof(Description), ["description"], isRequired: true);
        _value = DefineProperty<string>(nameof(Value), ["value"], isRequired: true);
        _isEnabled = DefineProperty<bool>(nameof(IsEnabled), ["isEnabled"]);
        _allowedMemberTypes = DefineListProperty<string>(nameof(AllowedMemberTypes), ["allowedMemberTypes"], isRequired: true);
    }
}
