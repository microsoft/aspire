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
        string resourceUri,
        string? baseUrl,
        Dictionary<string, Guid> delegatedPermissions,
        Dictionary<string, Guid> applicationPermissions,
        bool hasCatalog)
    {
        DisplayName = displayName;
        ApplicationId = applicationId;
        ResourceUri = resourceUri;
        BaseUrl = baseUrl;
        _delegatedPermissions = delegatedPermissions;
        _applicationPermissions = applicationPermissions;
        _hasCatalog = hasCatalog;
    }

    /// <summary>
    /// Gets Microsoft Graph (<c>https://graph.microsoft.com</c>).
    /// </summary>
    public static EntraIdApi MicrosoftGraph { get; } = new(
        "Microsoft Graph", "00000003-0000-0000-c000-000000000000", "https://graph.microsoft.com", "https://graph.microsoft.com/v1.0",
        s_graphDelegated, s_graphApplication, hasCatalog: true);

    /// <summary>
    /// Gets Azure Resource Manager (<c>https://management.azure.com</c>), whose delegated permission is <c>user_impersonation</c>.
    /// </summary>
    public static EntraIdApi AzureResourceManager { get; } = new(
        "Azure Resource Manager", "797f4846-ba00-4fd7-ba43-dac1f8f63013", "https://management.azure.com", "https://management.azure.com",
        s_armDelegated, s_noPermissions, hasCatalog: true);

    /// <summary>
    /// Gets Azure Storage (<c>https://storage.azure.com</c>), whose delegated permission is <c>user_impersonation</c>.
    /// </summary>
    public static EntraIdApi AzureStorage { get; } = new(
        "Azure Storage", "e406a681-f3d4-42a8-90b6-c2b029497af1", "https://storage.azure.com", null,
        s_storageDelegated, s_noPermissions, hasCatalog: true);

    /// <summary>
    /// Gets the display name used in diagnostics.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the API's application (client) ID, the same in every tenant for a first-party Microsoft API.
    /// </summary>
    public string ApplicationId { get; }

    // The prefix of the API's scopes in token requests, such as "https://graph.microsoft.com/User.Read". An API
    // identified by its application ID only is assumed to use the default application ID URI, api://{appId}.
    internal string ResourceUri { get; }

    // The API's root URL for Microsoft.Identity.Web's DownstreamApis settings, when it is the same for every caller.
    internal string? BaseUrl { get; }

    // Microsoft Graph lists the OpenID Connect scopes among its delegated permissions, but they are requested at sign-in and don't belong
    // to a resource, so they have no resource-qualified form. Another API can define a scope with one of these names, which is its own.
    internal bool IsOpenIdScope(string name) =>
        string.Equals(ApplicationId, MicrosoftGraph.ApplicationId, StringComparison.OrdinalIgnoreCase) &&
        (name.Equals("openid", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("email", StringComparison.OrdinalIgnoreCase) ||
         name.Equals("offline_access", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Identifies another API, whose service principal must exist in the tenant, by its application ID.
    /// </summary>
    /// <param name="displayName">A name for the API used in diagnostics and as the name of its <c>DownstreamApis</c> entry (its letters and digits).</param>
    /// <param name="applicationId">The API's application (client) ID, as a GUID.</param>
    /// <param name="resourceUri">
    /// The API's application ID URI, the prefix of its scopes in token requests, such as <c>api://contoso-orders</c> or
    /// <c>https://contoso.onmicrosoft.com/orders</c>. It is shown as "Application ID URI" on the API's registration. It is
    /// not derived from <paramref name="applicationId"/> because an API can use any URI.
    /// </param>
    /// <param name="baseUrl">
    /// The API's root URL, such as <c>https://graph.microsoft.us</c>, which Aspire adds to the <c>DownstreamApis</c> entry as <c>BaseUrl</c>.
    /// When <see langword="null"/>, no <c>BaseUrl</c> is set and the caller must supply the address itself.
    /// </param>
    /// <returns>The API. Its permissions must be requested with explicit IDs.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="applicationId"/> is not a GUID, <paramref name="resourceUri"/> is not a valid application ID URI, or
    /// <paramref name="baseUrl"/> is not an absolute HTTP or HTTPS URL.
    /// </exception>
    public static EntraIdApi FromApplicationId(string displayName, string applicationId, string resourceUri, string? baseUrl = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(displayName);
        ArgumentException.ThrowIfNullOrEmpty(applicationId);
        ArgumentException.ThrowIfNullOrEmpty(resourceUri);

        if (EntraIdValidation.ValidateClientId(applicationId) is { } error)
        {
            throw new ArgumentException($"'{applicationId}' is not a valid application ID. {error}", nameof(applicationId));
        }

        if (EntraIdValidation.ValidateApplicationIdUri(resourceUri) is { } uriError)
        {
            throw new ArgumentException($"'{resourceUri}' is not a valid application ID URI. {uriError}", nameof(resourceUri));
        }

        if (baseUrl is not null && !(Uri.TryCreate(baseUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"))
        {
            throw new ArgumentException($"'{baseUrl}' is not an absolute HTTP or HTTPS URL.", nameof(baseUrl));
        }

        // Keep the URI as given: a trailing slash is part of the resource identifier, and tokens for it carry that audience.
        // See https://learn.microsoft.com/entra/identity-platform/scopes-oidc#trailing-slash-and-default.
        return new(displayName, Guid.Parse(applicationId).ToString("D"), resourceUri, baseUrl, s_noPermissions, s_noPermissions, hasCatalog: false);
    }

    // A permission value is appended to the resource identifier: "https://graph.microsoft.com/User.Read". When the identifier ends
    // with a slash that slash is the separator, as in "https://management.azure.com/user_impersonation". The ".default" scope is
    // different: it always follows a slash, which gives "{identifier}//.default" for an identifier with a trailing slash.
    internal static string CreateScope(string resourceUri, string scope) =>
        resourceUri.EndsWith('/') && scope != ".default" ? $"{resourceUri}{scope}" : $"{resourceUri}/{scope}";

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

            // The reverse mistake: a name that isn't in the catalog, with the ID of a permission that is. The consent would then name
            // one permission while the declaration requests another.
            if (knownId is null && known.FirstOrDefault(p => p.Value == explicitId) is { Key: { } otherName })
            {
                throw new ArgumentException(
                    $"The ID '{explicitId:D}' is the {FormatType(permission.Type)} permission '{otherName}' of {DisplayName}, not '{permission.Name}'.",
                    nameof(permission));
            }

            return EntraIdResolvedPermission.Fixed(permission.Type, canonicalName, explicitId);
        }

        if (knownId is { } resolvedId)
        {
            return EntraIdResolvedPermission.Fixed(permission.Type, canonicalName, resolvedId);
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
/// For a permission that isn't in an API's built-in list, Aspire can't check that the name and the ID belong together. The declaration
/// uses the ID, while a delegated consent grant names the permission, so a name that doesn't match the ID would grant a permission that
/// was never declared. Take both from the API's service principal.
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
        ValidateName(name);
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
        ValidateName(name);
        return new(EntraIdPermissionType.Delegated, name, ValidateId(id));
    }

    /// <summary>
    /// Requests an application permission by name, using the ID that Aspire knows for the API.
    /// </summary>
    /// <param name="name">The permission's name, such as <c>User.Read.All</c>.</param>
    /// <returns>The permission.</returns>
    public static EntraIdApiPermission Application(string name)
    {
        ValidateName(name);
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
        ValidateName(name);
        return new(EntraIdPermissionType.Application, name, ValidateId(id));
    }

    // The names of granted delegated permissions are written to one space-separated string, so a name that contains a space would
    // consent to a second, undeclared permission. Names must follow Microsoft Graph's rule for scope and app role values.
    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (EntraIdValidation.ValidateScopeName(name) is { } error)
        {
            throw new ArgumentException($"'{name}' is not a valid permission name. {error}", nameof(name));
        }
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

/// <summary>
/// A permission of an API that an application declares or has consented, with the ID that identifies it.
/// </summary>
/// <remarks>
/// A first-party API's permission has a fixed ID. A delegated permission of a registration in the AppHost refers to the scope itself and
/// reads its ID when the template is generated, because <see cref="EntraIdScope.Id"/> can still be set after the permission is declared and
/// a copied ID would then disagree with the one that the API's own template writes.
/// </remarks>
internal readonly record struct EntraIdResolvedPermission
{
    private readonly Guid? _fixedId;
    private readonly EntraIdApplicationResource? _owner;
    private readonly EntraIdScope? _scope;

    private EntraIdResolvedPermission(EntraIdPermissionType type, string name, Guid? fixedId, EntraIdApplicationResource? owner, EntraIdScope? scope)
    {
        Type = type;
        Name = name;
        _fixedId = fixedId;
        _owner = owner;
        _scope = scope;
    }

    public EntraIdPermissionType Type { get; }

    public string Name { get; }

    public Guid Id => _fixedId ?? _owner!.GetScopeId(_scope!);

    // The scope of a registration in the AppHost, whose settings can still change after the permission is declared.
    public EntraIdScope? Scope => _scope;

    public static EntraIdResolvedPermission Fixed(EntraIdPermissionType type, string name, Guid id) => new(type, name, id, null, null);

    public static EntraIdResolvedPermission ForScope(EntraIdApplicationResource owner, EntraIdScope scope) =>
        new(EntraIdPermissionType.Delegated, scope.Name, null, owner, scope);
}
