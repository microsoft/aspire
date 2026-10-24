// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// The kind of permission that an application requests from an API.
/// </summary>
public enum EntraIdPermissionType
{
    /// <summary>
    /// A delegated permission (an OAuth 2.0 scope). The application calls the API on behalf of a signed-in user.
    /// </summary>
    Delegated,

    /// <summary>
    /// An application permission (an app role of the API's service principal). The application calls the API as itself, with no user.
    /// </summary>
    Application
}

/// <summary>
/// Identifies an API that is not modeled as an Aspire resource, such as Microsoft Graph, by its well-known application ID.
/// </summary>
/// <remarks>
/// <para>
/// The well-known APIs know the IDs of their most common permissions, so permissions can be requested by name.
/// Permissions that are not in that list can be requested by supplying the permission's ID with
/// <see cref="EntraIdApiPermission.Delegated(string, Guid)"/> or <see cref="EntraIdApiPermission.Application(string, Guid)"/>.
/// To look up an ID, read the <c>oauth2PermissionScopes</c> (delegated) or <c>appRoles</c> (application) of the API's service principal
/// in your tenant, for example with <c>az ad sp show --id 00000003-0000-0000-c000-000000000000</c>.
/// </para>
/// </remarks>
public sealed class EntraIdApi
{
    private static readonly Dictionary<string, Guid> s_graphDelegated = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openid"] = new("37f7f235-527c-4136-accd-4a02d197296e"),
        ["profile"] = new("14dad69e-099b-42c9-810b-d002981feec1"),
        ["email"] = new("64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0"),
        ["offline_access"] = new("7427e0e9-2fba-42fe-b0c0-848c9e6a8182"),
        ["User.Read"] = new("e1fe6dd8-ba31-4d61-89e7-88639da4683d"),
        ["User.ReadBasic.All"] = new("b340eb25-3456-403f-be2f-af7a0d370277"),
        ["User.Read.All"] = new("a154be20-db9c-4678-8ab7-66f6cc099a59"),
        ["Directory.Read.All"] = new("06da0dbc-49e2-44d2-8312-53f166ab848a"),
        ["Group.Read.All"] = new("5f8c59db-677d-491f-a6b8-5f174b11ec1d"),
        ["GroupMember.Read.All"] = new("bc024368-1153-4739-b217-4326f2e966d0"),
        ["Application.Read.All"] = new("c79f8feb-a9db-4090-85f9-90d820caa0eb"),
        ["Organization.Read.All"] = new("4908d5b9-3fb2-4b1e-9336-1888b7937185"),
        ["Mail.Read"] = new("570282fd-fa5c-430d-a7fd-fc8dc98a9dca"),
        ["Calendars.Read"] = new("465a38f9-76ea-45b9-9f34-9e8b0d4b0b42"),
        ["Files.Read"] = new("10465720-29dd-4523-a11a-6a75c743c9d9"),
        ["Files.Read.All"] = new("df85f4d6-205c-4ac5-a5ea-6bf408dba283"),
        ["Sites.Read.All"] = new("205e70e5-aba6-4c52-a976-6d2d46c48043")
    };

    private static readonly Dictionary<string, Guid> s_graphApplication = new(StringComparer.OrdinalIgnoreCase)
    {
        ["User.ReadBasic.All"] = new("97235f07-e226-4f63-ace3-39588e11d3a1"),
        ["User.Read.All"] = new("df021288-bdef-4463-88db-98f22de89214"),
        ["Directory.Read.All"] = new("7ab1d382-f21e-4acd-a863-ba3e13f7da61"),
        ["Group.Read.All"] = new("5b567255-7703-4780-807c-7be8301ae99b"),
        ["GroupMember.Read.All"] = new("98830695-27a2-44f7-8c18-0c3ebc9698f6"),
        ["Application.Read.All"] = new("9a5d68dd-52b0-4cc2-bd40-abcf44ac3a30"),
        ["Organization.Read.All"] = new("498476ce-e0fe-48b0-b801-37ba7e2685c6"),
        ["Mail.Read"] = new("810c84a8-4a9e-49e6-bf7d-12d183f40d01"),
        ["Calendars.Read"] = new("798ee544-9d2d-430c-a058-570e29e34338"),
        ["Files.Read.All"] = new("01d4889c-1287-42c6-ac1f-5d1e02578ef6"),
        ["Sites.Read.All"] = new("332a536c-c7ef-4017-ab91-336970924f0d")
    };

    private static readonly Dictionary<string, Guid> s_armDelegated = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user_impersonation"] = new("41094075-9dad-400e-a0bd-54e686782033")
    };

    private static readonly Dictionary<string, Guid> s_storageDelegated = new(StringComparer.OrdinalIgnoreCase)
    {
        ["user_impersonation"] = new("03e0da56-190b-40ad-a80c-ea378c433f7f")
    };

    private static readonly Dictionary<string, Guid> s_noPermissions = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, Guid> _delegatedPermissions;
    private readonly Dictionary<string, Guid> _applicationPermissions;
    private readonly bool _hasCatalog;

    private EntraIdApi(
        string displayName,
        string applicationId,
        Dictionary<string, Guid> delegatedPermissions,
        Dictionary<string, Guid> applicationPermissions,
        bool hasCatalog)
    {
        DisplayName = displayName;
        ApplicationId = applicationId;
        _delegatedPermissions = delegatedPermissions;
        _applicationPermissions = applicationPermissions;
        _hasCatalog = hasCatalog;
    }

    /// <summary>
    /// Gets Microsoft Graph (<c>https://graph.microsoft.com</c>).
    /// </summary>
    public static EntraIdApi MicrosoftGraph { get; } = new(
        "Microsoft Graph", "00000003-0000-0000-c000-000000000000", s_graphDelegated, s_graphApplication, hasCatalog: true);

    /// <summary>
    /// Gets Azure Resource Manager (<c>https://management.azure.com</c>), whose delegated permission is <c>user_impersonation</c>.
    /// </summary>
    public static EntraIdApi AzureResourceManager { get; } = new(
        "Azure Resource Manager", "797f4846-ba00-4fd7-ba43-dac1f8f63013", s_armDelegated, s_noPermissions, hasCatalog: true);

    /// <summary>
    /// Gets Azure Storage (<c>https://storage.azure.com</c>), whose delegated permission is <c>user_impersonation</c>.
    /// </summary>
    public static EntraIdApi AzureStorage { get; } = new(
        "Azure Storage", "e406a681-f3d4-42a8-90b6-c2b029497af1", s_storageDelegated, s_noPermissions, hasCatalog: true);

    /// <summary>
    /// Gets the display name used in diagnostics.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the API's application (client) ID, the same in every tenant for a first-party Microsoft API.
    /// </summary>
    public string ApplicationId { get; }

    /// <summary>
    /// Identifies another API, whose service principal must exist in the tenant, by its application ID.
    /// </summary>
    /// <param name="displayName">A name for the API used in diagnostics.</param>
    /// <param name="applicationId">The API's application (client) ID, as a GUID.</param>
    /// <returns>The API. Its permissions must be requested with explicit IDs.</returns>
    /// <exception cref="ArgumentException"><paramref name="applicationId"/> is not a GUID.</exception>
    public static EntraIdApi FromApplicationId(string displayName, string applicationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        ArgumentException.ThrowIfNullOrEmpty(applicationId);

        if (EntraIdValidation.ValidateClientId(applicationId) is { } error)
        {
            throw new ArgumentException($"'{applicationId}' is not a valid application ID. {error}", nameof(applicationId));
        }

        return new(displayName, Guid.Parse(applicationId).ToString("D"), s_noPermissions, s_noPermissions, hasCatalog: false);
    }

    internal EntraIdResolvedPermission Resolve(EntraIdApiPermission permission)
    {
        ArgumentNullException.ThrowIfNull(permission);

        var known = permission.Type == EntraIdPermissionType.Delegated ? _delegatedPermissions : _applicationPermissions;
        var knownId = known.TryGetValue(permission.Name, out var id) ? id : (Guid?)null;
        var canonicalName = knownId is not null ? known.Keys.First(k => string.Equals(k, permission.Name, StringComparison.OrdinalIgnoreCase)) : permission.Name;

        if (permission.Id is { } explicitId)
        {
            // A name from the catalog with a different ID is almost certainly a typo, and would silently request another permission.
            if (knownId is { } catalogId && catalogId != explicitId)
            {
                throw new ArgumentException(
                    $"The {FormatType(permission.Type)} permission '{permission.Name}' of {DisplayName} has the ID '{catalogId:D}', not '{explicitId:D}'.",
                    nameof(permission));
            }

            return new(permission.Type, canonicalName, explicitId);
        }

        if (knownId is { } resolvedId)
        {
            return new(permission.Type, canonicalName, resolvedId);
        }

        var message = _hasCatalog
            ? $"The {FormatType(permission.Type)} permission '{permission.Name}' is not one of the permissions that Aspire knows for {DisplayName}: " +
              $"{(known.Count == 0 ? "none" : string.Join(", ", known.Keys.Order(StringComparer.OrdinalIgnoreCase)))}. " +
              "Pass the permission's ID as well to request another permission."
            : $"{DisplayName} has no well-known permissions. Pass the ID of the {FormatType(permission.Type)} permission '{permission.Name}' as well.";
        throw new ArgumentException(message, nameof(permission));
    }

    private static string FormatType(EntraIdPermissionType type) => type == EntraIdPermissionType.Delegated ? "delegated" : "application";
}

/// <summary>
/// A permission, identified by name and optionally by ID, that an application requests from an API.
/// </summary>
/// <remarks>
/// Delegated permissions are scopes that the API exposes and the application uses on behalf of a signed-in user.
/// Application permissions are app roles that the API defines and the application uses as itself. They are different
/// permissions, even when they share a name, and each needs its own consent.
/// </remarks>
public sealed class EntraIdApiPermission
{
    private EntraIdApiPermission(EntraIdPermissionType type, string name, Guid? id)
    {
        Type = type;
        Name = name;
        Id = id;
    }

    /// <summary>
    /// Gets the kind of permission.
    /// </summary>
    public EntraIdPermissionType Type { get; }

    /// <summary>
    /// Gets the permission's name, such as <c>User.Read</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the permission's ID, or <see langword="null"/> when it is resolved from the API's well-known permissions.
    /// </summary>
    public Guid? Id { get; }

    /// <summary>
    /// Requests a delegated permission by name, using the ID that Aspire knows for the API.
    /// </summary>
    /// <param name="name">The permission's name, such as <c>User.Read</c>.</param>
    /// <returns>The permission.</returns>
    public static EntraIdApiPermission Delegated(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(EntraIdPermissionType.Delegated, name, null);
    }

    /// <summary>
    /// Requests a delegated permission by name and ID.
    /// </summary>
    /// <param name="name">The permission's name, such as <c>Mail.Send</c>.</param>
    /// <param name="id">The permission's ID, defined by the API's service principal.</param>
    /// <returns>The permission.</returns>
    public static EntraIdApiPermission Delegated(string name, Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(EntraIdPermissionType.Delegated, name, ValidateId(id));
    }

    /// <summary>
    /// Requests an application permission by name, using the ID that Aspire knows for the API.
    /// </summary>
    /// <param name="name">The permission's name, such as <c>User.Read.All</c>.</param>
    /// <returns>The permission.</returns>
    public static EntraIdApiPermission Application(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(EntraIdPermissionType.Application, name, null);
    }

    /// <summary>
    /// Requests an application permission by name and ID.
    /// </summary>
    /// <param name="name">The permission's name, such as <c>Mail.Send</c>.</param>
    /// <param name="id">The permission's ID, defined by the API's service principal.</param>
    /// <returns>The permission.</returns>
    public static EntraIdApiPermission Application(string name, Guid id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new(EntraIdPermissionType.Application, name, ValidateId(id));
    }

    private static Guid ValidateId(Guid id) => id == Guid.Empty
        ? throw new ArgumentException("The permission ID must not be empty.", nameof(id))
        : id;
}

/// <summary>
/// Describes a delegated permission scope that an API registration created by Aspire exposes.
/// </summary>
/// <remarks>
/// The scope's ID is stable across deployments: unless <see cref="Id"/> is set, it derives from the registration's name and the
/// scope's <see cref="Name"/>. Microsoft Graph does not allow a scope to be removed, or its name to change, while it is enabled.
/// To retire a scope, set <see cref="IsEnabled"/> to <see langword="false"/>, deploy, and then remove the scope in a later deployment.
/// </remarks>
public sealed class EntraIdScope
{
    internal EntraIdScope(string name, string description)
    {
        Name = name;
        Description = description;
        DisplayName = name;
    }

    /// <summary>
    /// Gets the scope's name, the value that appears in the token's <c>scp</c> claim, such as <c>access_as_user</c>.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or sets what the scope allows, shown on the admin consent page.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Gets or sets the scope's title, shown on the admin consent page. Defaults to <see cref="Name"/>.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the scope's title shown on the consent page for users. When <see langword="null"/>, <see cref="DisplayName"/> is used.
    /// </summary>
    public string? UserConsentDisplayName { get; set; }

    /// <summary>
    /// Gets or sets what the scope allows, shown on the consent page for users. When <see langword="null"/>, <see cref="Description"/> is used.
    /// </summary>
    public string? UserConsentDescription { get; set; }

    /// <summary>
    /// Gets or sets whether only an administrator can consent to the scope. Defaults to <see langword="false"/>, which lets users consent.
    /// </summary>
    public bool RequiresAdminConsent { get; set; }

    /// <summary>
    /// Gets or sets whether clients can request the scope. Defaults to <see langword="true"/>.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the scope's ID, which must stay the same across deployments.
    /// </summary>
    /// <remarks>
    /// Aspire derives a stable ID for scopes of registrations it creates. Set the ID explicitly to adopt a scope that already exists,
    /// or for a scope of an existing registration, whose ID cannot be derived.
    /// </remarks>
    public Guid? Id { get; set; }
}

internal readonly record struct EntraIdResolvedPermission(EntraIdPermissionType Type, string Name, Guid Id);
