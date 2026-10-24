// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models the Microsoft Graph application properties used by Entra registration provisioning.
/// </summary>
internal sealed class MicrosoftGraphApplication(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, EntraIdBicepConstants.ApplicationResourceType, EntraIdBicepConstants.GraphApiVersion)
{
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<string>? _id;

    public BicepValue<string> AppId
    {
        get { Initialize(); return _appId!; }
    }
    private BicepValue<string>? _appId;

    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }
    private BicepValue<string>? _displayName;

    public BicepValue<string> UniqueName
    {
        get { Initialize(); return _uniqueName!; }
        set { Initialize(); _uniqueName!.Assign(value); }
    }
    private BicepValue<string>? _uniqueName;

    public BicepValue<string> SignInAudience
    {
        get { Initialize(); return _signInAudience!; }
        set { Initialize(); _signInAudience!.Assign(value); }
    }
    private BicepValue<string>? _signInAudience;

    public BicepValue<int> RequestedAccessTokenVersion
    {
        get { Initialize(); return _requestedAccessTokenVersion!; }
        set { Initialize(); _requestedAccessTokenVersion!.Assign(value); }
    }
    private BicepValue<int>? _requestedAccessTokenVersion;

    public BicepList<string> RedirectUris
    {
        get { Initialize(); return _redirectUris!; }
        set { Initialize(); _redirectUris!.Assign(value); }
    }
    private BicepList<string>? _redirectUris;

    public BicepValue<bool> EnableIdTokenIssuance
    {
        get { Initialize(); return _enableIdTokenIssuance!; }
        set { Initialize(); _enableIdTokenIssuance!.Assign(value); }
    }
    private BicepValue<bool>? _enableIdTokenIssuance;

    public BicepList<string> Owners
    {
        get { Initialize(); return _owners!; }
        set { Initialize(); _owners!.Assign(value); }
    }
    private BicepList<string>? _owners;

    public BicepList<string> IdentifierUris
    {
        get { Initialize(); return _identifierUris!; }
        set { Initialize(); _identifierUris!.Assign(value); }
    }
    private BicepList<string>? _identifierUris;

    public BicepList<MicrosoftGraphPermissionScope> PermissionScopes
    {
        get { Initialize(); return _permissionScopes!; }
        set { Initialize(); _permissionScopes!.Assign(value); }
    }
    private BicepList<MicrosoftGraphPermissionScope>? _permissionScopes;

    public BicepList<MicrosoftGraphPreAuthorizedApplication> PreAuthorizedApplications
    {
        get { Initialize(); return _preAuthorizedApplications!; }
        set { Initialize(); _preAuthorizedApplications!.Assign(value); }
    }
    private BicepList<MicrosoftGraphPreAuthorizedApplication>? _preAuthorizedApplications;

    public BicepList<MicrosoftGraphRequiredResourceAccess> RequiredResourceAccess
    {
        get { Initialize(); return _requiredResourceAccess!; }
        set { Initialize(); _requiredResourceAccess!.Assign(value); }
    }
    private BicepList<MicrosoftGraphRequiredResourceAccess>? _requiredResourceAccess;

    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<string>(nameof(Id), ["id"], isOutput: true);
        _appId = DefineProperty<string>(nameof(AppId), ["appId"], isOutput: true);
        _displayName = DefineProperty<string>(nameof(DisplayName), ["displayName"], isRequired: true);
        _uniqueName = DefineProperty<string>(nameof(UniqueName), ["uniqueName"], isRequired: true);
        _signInAudience = DefineProperty<string>(nameof(SignInAudience), ["signInAudience"]);
        _requestedAccessTokenVersion = DefineProperty<int>(nameof(RequestedAccessTokenVersion), ["api", "requestedAccessTokenVersion"]);
        _redirectUris = DefineListProperty<string>(nameof(RedirectUris), ["web", "redirectUris"]);
        _enableIdTokenIssuance = DefineProperty<bool>(nameof(EnableIdTokenIssuance), ["web", "implicitGrantSettings", "enableIdTokenIssuance"]);
        _owners = DefineListProperty<string>(nameof(Owners), ["owners", "relationships"]);
        _identifierUris = DefineListProperty<string>(nameof(IdentifierUris), ["identifierUris"]);
        _permissionScopes = DefineListProperty<MicrosoftGraphPermissionScope>(nameof(PermissionScopes), ["api", "oauth2PermissionScopes"]);
        _preAuthorizedApplications = DefineListProperty<MicrosoftGraphPreAuthorizedApplication>(nameof(PreAuthorizedApplications), ["api", "preAuthorizedApplications"]);
        _requiredResourceAccess = DefineListProperty<MicrosoftGraphRequiredResourceAccess>(nameof(RequiredResourceAccess), ["requiredResourceAccess"]);
    }
}
