// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models an entry of an application's <c>api.oauth2PermissionScopes</c>, a delegated permission that the API exposes.
/// </summary>
public sealed class MicrosoftGraphPermissionScope : ProvisionableConstruct
{
    /// <summary>Gets or sets the stable ID of the delegated permission.</summary>
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
        set { Initialize(); _id!.Assign(value); }
    }
    private BicepValue<string>? _id;

    /// <summary>Gets or sets the scope value included in access tokens.</summary>
    public BicepValue<string> Value
    {
        get { Initialize(); return _value!; }
        set { Initialize(); _value!.Assign(value); }
    }
    private BicepValue<string>? _value;

    /// <summary>Gets or sets who can consent to the scope: <c>Admin</c> or <c>User</c>.</summary>
    public BicepValue<string> Type
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }
    private BicepValue<string>? _type;

    /// <summary>Gets or sets whether the scope is available for use.</summary>
    public BicepValue<bool> IsEnabled
    {
        get { Initialize(); return _isEnabled!; }
        set { Initialize(); _isEnabled!.Assign(value); }
    }
    private BicepValue<bool>? _isEnabled;

    /// <summary>Gets or sets the scope's display name for administrator consent.</summary>
    public BicepValue<string> AdminConsentDisplayName
    {
        get { Initialize(); return _adminConsentDisplayName!; }
        set { Initialize(); _adminConsentDisplayName!.Assign(value); }
    }
    private BicepValue<string>? _adminConsentDisplayName;

    /// <summary>Gets or sets the scope's description for administrator consent.</summary>
    public BicepValue<string> AdminConsentDescription
    {
        get { Initialize(); return _adminConsentDescription!; }
        set { Initialize(); _adminConsentDescription!.Assign(value); }
    }
    private BicepValue<string>? _adminConsentDescription;

    /// <summary>Gets or sets the scope's display name for user consent.</summary>
    public BicepValue<string> UserConsentDisplayName
    {
        get { Initialize(); return _userConsentDisplayName!; }
        set { Initialize(); _userConsentDisplayName!.Assign(value); }
    }
    private BicepValue<string>? _userConsentDisplayName;

    /// <summary>Gets or sets the scope's description for user consent.</summary>
    public BicepValue<string> UserConsentDescription
    {
        get { Initialize(); return _userConsentDescription!; }
        set { Initialize(); _userConsentDescription!.Assign(value); }
    }
    private BicepValue<string>? _userConsentDescription;

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _id = DefineProperty<string>(nameof(Id), ["id"], isRequired: true);
        _value = DefineProperty<string>(nameof(Value), ["value"], isRequired: true);
        _type = DefineProperty<string>(nameof(Type), ["type"], isRequired: true);
        _isEnabled = DefineProperty<bool>(nameof(IsEnabled), ["isEnabled"], isRequired: true);
        _adminConsentDisplayName = DefineProperty<string>(nameof(AdminConsentDisplayName), ["adminConsentDisplayName"]);
        _adminConsentDescription = DefineProperty<string>(nameof(AdminConsentDescription), ["adminConsentDescription"]);
        _userConsentDisplayName = DefineProperty<string>(nameof(UserConsentDisplayName), ["userConsentDisplayName"]);
        _userConsentDescription = DefineProperty<string>(nameof(UserConsentDescription), ["userConsentDescription"]);
    }
}

/// <summary>
/// Models an entry of an application's <c>api.preAuthorizedApplications</c>, a client application that is
/// authorized to request delegated permissions of the API without user or admin consent prompts.
/// </summary>
public sealed class MicrosoftGraphPreAuthorizedApplication : ProvisionableConstruct
{
    /// <summary>Gets or sets the pre-authorized client's application ID.</summary>
    public BicepValue<string> AppId
    {
        get { Initialize(); return _appId!; }
        set { Initialize(); _appId!.Assign(value); }
    }
    private BicepValue<string>? _appId;

    /// <summary>Gets or sets the IDs of scopes the client can request without consent prompts.</summary>
    public BicepList<string> DelegatedPermissionIds
    {
        get { Initialize(); return _delegatedPermissionIds!; }
        set { Initialize(); _delegatedPermissionIds!.Assign(value); }
    }
    private BicepList<string>? _delegatedPermissionIds;

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _appId = DefineProperty<string>(nameof(AppId), ["appId"], isRequired: true);
        _delegatedPermissionIds = DefineListProperty<string>(nameof(DelegatedPermissionIds), ["delegatedPermissionIds"], isRequired: true);
    }
}

/// <summary>
/// Models an entry of an application's <c>requiredResourceAccess</c>: the permissions that the application declares it needs from one API.
/// </summary>
public sealed class MicrosoftGraphRequiredResourceAccess : ProvisionableConstruct
{
    /// <summary>Gets or sets the application ID of the API being requested.</summary>
    public BicepValue<string> ResourceAppId
    {
        get { Initialize(); return _resourceAppId!; }
        set { Initialize(); _resourceAppId!.Assign(value); }
    }
    private BicepValue<string>? _resourceAppId;

    /// <summary>Gets or sets the delegated permissions and application roles requested from the API.</summary>
    public BicepList<MicrosoftGraphResourceAccess> ResourceAccess
    {
        get { Initialize(); return _resourceAccess!; }
        set { Initialize(); _resourceAccess!.Assign(value); }
    }
    private BicepList<MicrosoftGraphResourceAccess>? _resourceAccess;

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _resourceAppId = DefineProperty<string>(nameof(ResourceAppId), ["resourceAppId"], isRequired: true);
        _resourceAccess = DefineListProperty<MicrosoftGraphResourceAccess>(nameof(ResourceAccess), ["resourceAccess"], isRequired: true);
    }
}

/// <summary>
/// Models a single permission in a <see cref="MicrosoftGraphRequiredResourceAccess"/>.
/// </summary>
public sealed class MicrosoftGraphResourceAccess : ProvisionableConstruct
{
    /// <summary>Gets or sets the ID of the requested scope or application role.</summary>
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
        set { Initialize(); _id!.Assign(value); }
    }
    private BicepValue<string>? _id;

    /// <summary>Gets or sets the permission type: <c>Scope</c> for a delegated permission or <c>Role</c> for an application role.</summary>
    public BicepValue<string> Type
    {
        get { Initialize(); return _type!; }
        set { Initialize(); _type!.Assign(value); }
    }
    private BicepValue<string>? _type;

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        _id = DefineProperty<string>(nameof(Id), ["id"], isRequired: true);
        _type = DefineProperty<string>(nameof(Type), ["type"], isRequired: true);
    }
}
