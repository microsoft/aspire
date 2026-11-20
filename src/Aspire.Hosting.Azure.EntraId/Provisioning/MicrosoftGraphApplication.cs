// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models the Microsoft Graph application properties used by Entra registration provisioning.
/// </summary>
/// <param name="bicepIdentifier">The resource's identifier in the Bicep module.</param>
/// <remarks>
/// Retrieve this model from <c>GetProvisionableResources()</c> in a
/// <c>ConfigureInfrastructure</c> callback to customize a registration created by Aspire.
/// See <see href="https://learn.microsoft.com/graph/templates/bicep/reference/applications"/>.
/// </remarks>
public sealed class MicrosoftGraphApplication(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, EntraIdBicepConstants.ApplicationResourceType, EntraIdBicepConstants.GraphApiVersion)
{
    /// <summary>Gets the application's directory object ID.</summary>
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<string>? _id;

    /// <summary>Gets the application (client) ID assigned by Microsoft Entra ID.</summary>
    public BicepValue<string> AppId
    {
        get { Initialize(); return _appId!; }
    }
    private BicepValue<string>? _appId;

    /// <summary>Gets or sets the application's display name.</summary>
    /// <remarks>
    /// An API registration that uses the default application ID URI declares the application a second time to set that URI.
    /// That declaration repeats <see cref="DisplayName"/> and <see cref="UniqueName"/>, so change them on every
    /// <see cref="MicrosoftGraphApplication"/> in the infrastructure, or the second declaration reverts the display name
    /// and a different unique name targets another registration.
    /// </remarks>
    public BicepValue<string> DisplayName
    {
        get { Initialize(); return _displayName!; }
        set { Initialize(); _displayName!.Assign(value); }
    }
    private BicepValue<string>? _displayName;

    /// <summary>Gets or sets the immutable alternate key used to identify the application across deployments.</summary>
    /// <remarks>See <see cref="DisplayName"/> for the second declaration of API registrations.</remarks>
    public BicepValue<string> UniqueName
    {
        get { Initialize(); return _uniqueName!; }
        set { Initialize(); _uniqueName!.Assign(value); }
    }
    private BicepValue<string>? _uniqueName;

    /// <summary>Gets or sets the supported Microsoft account types.</summary>
    public BicepValue<string> SignInAudience
    {
        get { Initialize(); return _signInAudience!; }
        set { Initialize(); _signInAudience!.Assign(value); }
    }
    private BicepValue<string>? _signInAudience;

    /// <summary>Gets or sets the access token version requested by this API.</summary>
    public BicepValue<int> RequestedAccessTokenVersion
    {
        get { Initialize(); return _requestedAccessTokenVersion!; }
        set { Initialize(); _requestedAccessTokenVersion!.Assign(value); }
    }
    private BicepValue<int>? _requestedAccessTokenVersion;

    /// <summary>Gets or sets the web application's redirect URIs.</summary>
    public BicepList<string> RedirectUris
    {
        get { Initialize(); return _redirectUris!; }
        set { Initialize(); _redirectUris!.Assign(value); }
    }
    private BicepList<string>? _redirectUris;

    /// <summary>Gets or sets whether the authorization endpoint can issue ID tokens through implicit or hybrid flows.</summary>
    public BicepValue<bool> EnableIdTokenIssuance
    {
        get { Initialize(); return _enableIdTokenIssuance!; }
        set { Initialize(); _enableIdTokenIssuance!.Assign(value); }
    }
    private BicepValue<bool>? _enableIdTokenIssuance;

    /// <summary>Gets or sets the directory object IDs of the application's owners.</summary>
    public BicepList<string> Owners
    {
        get { Initialize(); return _owners!; }
        set { Initialize(); _owners!.Assign(value); }
    }
    private BicepList<string>? _owners;

    /// <summary>Gets or sets the application ID URIs used as prefixes for this API's scopes.</summary>
    public BicepList<string> IdentifierUris
    {
        get { Initialize(); return _identifierUris!; }
        set { Initialize(); _identifierUris!.Assign(value); }
    }
    private BicepList<string>? _identifierUris;

    /// <summary>Gets or sets the delegated permissions exposed by this API.</summary>
    public BicepList<MicrosoftGraphPermissionScope> PermissionScopes
    {
        get { Initialize(); return _permissionScopes!; }
        set { Initialize(); _permissionScopes!.Assign(value); }
    }
    private BicepList<MicrosoftGraphPermissionScope>? _permissionScopes;

    /// <summary>Gets or sets the client applications pre-authorized to request this API's delegated permissions.</summary>
    public BicepList<MicrosoftGraphPreAuthorizedApplication> PreAuthorizedApplications
    {
        get { Initialize(); return _preAuthorizedApplications!; }
        set { Initialize(); _preAuthorizedApplications!.Assign(value); }
    }
    private BicepList<MicrosoftGraphPreAuthorizedApplication>? _preAuthorizedApplications;

    /// <summary>Gets or sets the API permissions requested by the application.</summary>
    public BicepList<MicrosoftGraphRequiredResourceAccess> RequiredResourceAccess
    {
        get { Initialize(); return _requiredResourceAccess!; }
        set { Initialize(); _requiredResourceAccess!.Assign(value); }
    }
    private BicepList<MicrosoftGraphRequiredResourceAccess>? _requiredResourceAccess;

    /// <summary>Gets or sets the application roles exposed by the application.</summary>
    public BicepList<MicrosoftGraphAppRole> AppRoles
    {
        get { Initialize(); return _appRoles!; }
        set { Initialize(); _appRoles!.Assign(value); }
    }
    private BicepList<MicrosoftGraphAppRole>? _appRoles;

    /// <summary>Gets or sets the application's public certificate credentials.</summary>
    public BicepList<MicrosoftGraphKeyCredential> KeyCredentials
    {
        get { Initialize(); return _keyCredentials!; }
        set { Initialize(); _keyCredentials!.Assign(value); }
    }
    private BicepList<MicrosoftGraphKeyCredential>? _keyCredentials;

    /// <summary>Gets or sets the custom strings used to categorize the application.</summary>
    /// <remarks>Microsoft Graph tags are a string collection, not Azure Resource Manager key/value tags.</remarks>
    public BicepList<string> Tags
    {
        get { Initialize(); return _tags!; }
        set { Initialize(); _tags!.Assign(value); }
    }
    private BicepList<string>? _tags;

    /// <summary>Gets or sets the reference to application or service contact information in a service or asset management database.</summary>
    public BicepValue<string> ServiceManagementReference
    {
        get { Initialize(); return _serviceManagementReference!; }
        set { Initialize(); _serviceManagementReference!.Assign(value); }
    }
    private BicepValue<string>? _serviceManagementReference;

    /// <summary>Gets or sets a description of the application, shown to end users. The maximum length is 1,024 characters.</summary>
    public BicepValue<string> Description
    {
        get { Initialize(); return _description!; }
        set { Initialize(); _description!.Assign(value); }
    }
    private BicepValue<string>? _description;

    /// <summary>Gets or sets notes for managing the application.</summary>
    public BicepValue<string> Notes
    {
        get { Initialize(); return _notes!; }
        set { Initialize(); _notes!.Assign(value); }
    }
    private BicepValue<string>? _notes;

    /// <summary>Gets or sets the groups claim that tokens for this application contain: <c>None</c>, <c>SecurityGroup</c>, or <c>All</c>.</summary>
    public BicepValue<string> GroupMembershipClaims
    {
        get { Initialize(); return _groupMembershipClaims!; }
        set { Initialize(); _groupMembershipClaims!.Assign(value); }
    }
    private BicepValue<string>? _groupMembershipClaims;

    /// <summary>Gets or sets whether the application is treated as a public client when Microsoft Entra ID can't determine its type.</summary>
    public BicepValue<bool> IsFallbackPublicClient
    {
        get { Initialize(); return _isFallbackPublicClient!; }
        set { Initialize(); _isFallbackPublicClient!.Assign(value); }
    }
    private BicepValue<bool>? _isFallbackPublicClient;

    /// <summary>Gets or sets the URL of the web application's home page.</summary>
    public BicepValue<string> HomePageUrl
    {
        get { Initialize(); return _homePageUrl!; }
        set { Initialize(); _homePageUrl!.Assign(value); }
    }
    private BicepValue<string>? _homePageUrl;

    /// <summary>Gets or sets the URL that Microsoft Entra ID uses to sign users out of the web application.</summary>
    public BicepValue<string> LogoutUrl
    {
        get { Initialize(); return _logoutUrl!; }
        set { Initialize(); _logoutUrl!.Assign(value); }
    }
    private BicepValue<string>? _logoutUrl;

    /// <summary>Gets or sets whether the authorization endpoint can issue access tokens through implicit or hybrid flows.</summary>
    public BicepValue<bool> EnableAccessTokenIssuance
    {
        get { Initialize(); return _enableAccessTokenIssuance!; }
        set { Initialize(); _enableAccessTokenIssuance!.Assign(value); }
    }
    private BicepValue<bool>? _enableAccessTokenIssuance;

    /// <summary>Gets or sets the redirect URIs of the single-page application.</summary>
    public BicepList<string> SpaRedirectUris
    {
        get { Initialize(); return _spaRedirectUris!; }
        set { Initialize(); _spaRedirectUris!.Assign(value); }
    }
    private BicepList<string>? _spaRedirectUris;

    /// <summary>Gets or sets the redirect URIs of the installed (desktop or mobile) client application.</summary>
    public BicepList<string> PublicClientRedirectUris
    {
        get { Initialize(); return _publicClientRedirectUris!; }
        set { Initialize(); _publicClientRedirectUris!.Assign(value); }
    }
    private BicepList<string>? _publicClientRedirectUris;

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _appRoles = DefineListProperty<MicrosoftGraphAppRole>(nameof(AppRoles), ["appRoles"]);
        _keyCredentials = DefineListProperty<MicrosoftGraphKeyCredential>(nameof(KeyCredentials), ["keyCredentials"]);
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
        _tags = DefineListProperty<string>(nameof(Tags), ["tags"]);
        _serviceManagementReference = DefineProperty<string>(nameof(ServiceManagementReference), ["serviceManagementReference"]);
        _description = DefineProperty<string>(nameof(Description), ["description"]);
        _notes = DefineProperty<string>(nameof(Notes), ["notes"]);
        _groupMembershipClaims = DefineProperty<string>(nameof(GroupMembershipClaims), ["groupMembershipClaims"]);
        _isFallbackPublicClient = DefineProperty<bool>(nameof(IsFallbackPublicClient), ["isFallbackPublicClient"]);
        _homePageUrl = DefineProperty<string>(nameof(HomePageUrl), ["web", "homePageUrl"]);
        _logoutUrl = DefineProperty<string>(nameof(LogoutUrl), ["web", "logoutUrl"]);
        _enableAccessTokenIssuance = DefineProperty<bool>(nameof(EnableAccessTokenIssuance), ["web", "implicitGrantSettings", "enableAccessTokenIssuance"]);
        _spaRedirectUris = DefineListProperty<string>(nameof(SpaRedirectUris), ["spa", "redirectUris"]);
        _publicClientRedirectUris = DefineListProperty<string>(nameof(PublicClientRedirectUris), ["publicClient", "redirectUris"]);
    }
}
