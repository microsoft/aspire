// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Models the service principal created for an Entra application registration.
/// </summary>
/// <param name="bicepIdentifier">The resource's identifier in the Bicep module.</param>
/// <remarks>See <see href="https://learn.microsoft.com/graph/templates/bicep/reference/serviceprincipals"/>.</remarks>
public sealed class MicrosoftGraphServicePrincipal(string bicepIdentifier)
    : ProvisionableResource(bicepIdentifier, EntraIdBicepConstants.ServicePrincipalResourceType, EntraIdBicepConstants.GraphApiVersion)
{
    /// <summary>Gets the service principal's directory object ID.</summary>
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<string>? _id;

    /// <summary>Gets or sets the associated application's client ID.</summary>
    public BicepValue<string> AppId
    {
        get { Initialize(); return _appId!; }
        set { Initialize(); _appId!.Assign(value); }
    }
    private BicepValue<string>? _appId;

    /// <summary>Gets or sets the directory object IDs of the service principal's owners.</summary>
    public BicepList<string> Owners
    {
        get { Initialize(); return _owners!; }
        set { Initialize(); _owners!.Assign(value); }
    }
    private BicepList<string>? _owners;

    /// <summary>Gets or sets the custom strings used to categorize the service principal.</summary>
    /// <remarks>
    /// Add to this collection to preserve Aspire's <c>WindowsAzureActiveDirectoryIntegratedApp</c> tag,
    /// which makes the service principal visible in the Enterprise applications list.
    /// </remarks>
    public BicepList<string> Tags
    {
        get { Initialize(); return _tags!; }
        set { Initialize(); _tags!.Assign(value); }
    }
    private BicepList<string>? _tags;

    /// <summary>Gets or sets whether the service principal is enabled. When <see langword="false"/>, no user can sign in to the application.</summary>
    public BicepValue<bool> AccountEnabled
    {
        get { Initialize(); return _accountEnabled!; }
        set { Initialize(); _accountEnabled!.Assign(value); }
    }
    private BicepValue<bool>? _accountEnabled;

    /// <summary>Gets or sets whether users or other service principals need an app role assignment before they can sign in or get tokens.</summary>
    public BicepValue<bool> AppRoleAssignmentRequired
    {
        get { Initialize(); return _appRoleAssignmentRequired!; }
        set { Initialize(); _appRoleAssignmentRequired!.Assign(value); }
    }
    private BicepValue<bool>? _appRoleAssignmentRequired;

    /// <summary>Gets or sets a description of the service principal, shown to end users in portals such as My Apps.</summary>
    public BicepValue<string> Description
    {
        get { Initialize(); return _description!; }
        set { Initialize(); _description!.Assign(value); }
    }
    private BicepValue<string>? _description;

    /// <summary>Gets or sets notes for managing the service principal.</summary>
    public BicepValue<string> Notes
    {
        get { Initialize(); return _notes!; }
        set { Initialize(); _notes!.Assign(value); }
    }
    private BicepValue<string>? _notes;

    /// <summary>Gets or sets the email addresses that receive notifications, such as certificate expiry.</summary>
    public BicepList<string> NotificationEmailAddresses
    {
        get { Initialize(); return _notificationEmailAddresses!; }
        set { Initialize(); _notificationEmailAddresses!.Assign(value); }
    }
    private BicepList<string>? _notificationEmailAddresses;

    /// <summary>
    /// Creates a reference to a service principal that already exists in the tenant, such as the one for Microsoft Graph,
    /// addressed by its application (client) ID.
    /// </summary>
    /// <param name="bicepIdentifier">The resource's identifier in the Bicep module.</param>
    /// <returns>A reference whose <see cref="MicrosoftGraphExistingServicePrincipal.Name"/> must be set to the application ID.</returns>
    public static MicrosoftGraphExistingServicePrincipal FromExisting(string bicepIdentifier) => new(bicepIdentifier);

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<string>(nameof(Id), ["id"], isOutput: true);
        _appId = DefineProperty<string>(nameof(AppId), ["appId"], isRequired: true);
        _tags = DefineListProperty<string>(nameof(Tags), ["tags"]);
        _owners = DefineListProperty<string>(nameof(Owners), ["owners", "relationships"]);
        _accountEnabled = DefineProperty<bool>(nameof(AccountEnabled), ["accountEnabled"]);
        _appRoleAssignmentRequired = DefineProperty<bool>(nameof(AppRoleAssignmentRequired), ["appRoleAssignmentRequired"]);
        _description = DefineProperty<string>(nameof(Description), ["description"]);
        _notes = DefineProperty<string>(nameof(Notes), ["notes"]);
        _notificationEmailAddresses = DefineListProperty<string>(nameof(NotificationEmailAddresses), ["notificationEmailAddresses"]);
    }
}

/// <summary>
/// References a service principal that already exists in the tenant, addressed by its application (client) ID.
/// </summary>
/// <remarks>
/// Azure.Provisioning leaves only the property named <c>Name</c> settable on an existing resource, and it uses that property
/// to address the resource, so the application ID is exposed as <see cref="Name"/>.
/// </remarks>
public sealed class MicrosoftGraphExistingServicePrincipal : ProvisionableResource
{
    /// <summary>Creates a reference to an existing service principal.</summary>
    /// <param name="bicepIdentifier">The resource's identifier in the Bicep module.</param>
    public MicrosoftGraphExistingServicePrincipal(string bicepIdentifier)
        : base(bicepIdentifier, EntraIdBicepConstants.ServicePrincipalResourceType, EntraIdBicepConstants.GraphApiVersion)
    {
        IsExistingResource = true;
    }

    /// <summary>Gets the service principal's directory object ID.</summary>
    public BicepValue<string> Id
    {
        get { Initialize(); return _id!; }
    }
    private BicepValue<string>? _id;

    /// <summary>Gets or sets the application (client) ID that addresses the service principal.</summary>
    public BicepValue<string> Name
    {
        get { Initialize(); return _name!; }
        set { Initialize(); _name!.Assign(value); }
    }
    private BicepValue<string>? _name;

    /// <inheritdoc />
    protected override void DefineProvisionableProperties()
    {
        base.DefineProvisionableProperties();
        _id = DefineProperty<string>(nameof(Id), ["id"], isOutput: true);
        _name = DefineProperty<string>(nameof(Name), ["appId"], isRequired: true);
    }
}
