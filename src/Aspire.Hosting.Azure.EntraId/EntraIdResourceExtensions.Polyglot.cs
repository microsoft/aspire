// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

public static partial class EntraIdResourceExtensions
{
    /// <summary>
    /// References an existing registration without enabling Azure provisioning or querying Microsoft Graph.
    /// </summary>
    /// <param name="builder">The application builder.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="tenantId">The registration's home tenant GUID or domain, as a literal or parameter. Sign-in keywords are not home tenant IDs.</param>
    /// <param name="clientId">The registration's application client GUID, as a literal or parameter.</param>
    /// <returns>The application registration builder.</returns>
    /// <remarks>Literal IDs are validated immediately; parameter values are validated at startup. Other Azure resources retain their provisioning setup.</remarks>
    [AspireExport("addExistingEntraApplication")]
    internal static IResourceBuilder<EntraIdApplicationResource> AddExistingEntraApplicationForExport(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object tenantId,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object clientId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new EntraIdApplicationResource(name);
        SetExistingApplicationIds(resource, tenantId, clientId);
        return ConfigureExistingApplication(builder, resource);
    }

    /// <summary>
    /// References an existing registration without undoing the Azure provisioning setup enabled when this resource was added.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="tenantId">The registration's home tenant GUID or domain, as a literal or parameter.</param>
    /// <param name="clientId">The registration's application client GUID, as a literal or parameter.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>
    /// The registration itself is not provisioned or queried. Deployment can still require Azure authentication and configuration.
    /// Use addExistingEntraApplication instead for a configuration-only AppHost without Azure setup.
    /// </remarks>
    [AspireExport("asExistingApplication")]
    internal static IResourceBuilder<EntraIdApplicationResource> AsExistingApplicationForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object tenantId,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object clientId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        SetExistingApplicationIds(builder.Resource, tenantId, clientId);
        return ConfigureExistingApplication(builder);
    }

    private static void SetExistingApplicationIds(EntraIdApplicationResource resource, object tenantId, object clientId)
    {
        ValidateExistingId(tenantId, EntraIdValidation.ValidateTenantId, nameof(tenantId));
        ValidateExistingId(clientId, EntraIdValidation.ValidateClientId, nameof(clientId));

        resource.TenantId = tenantId as string;
        resource.TenantIdParameter = (tenantId as IResourceBuilder<ParameterResource>)?.Resource;
        resource.ClientId = clientId as string;
        resource.ClientIdParameter = (clientId as IResourceBuilder<ParameterResource>)?.Resource;
    }

    private static void ValidateExistingId(object value, Func<string, string?> validate, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        switch (value)
        {
            case string literal:
                ArgumentException.ThrowIfNullOrEmpty(literal, parameterName);
                if (validate(literal) is { } error)
                {
                    throw new ArgumentException(error, parameterName);
                }
                break;
            case IResourceBuilder<ParameterResource>:
                break;
            default:
                throw new ArgumentException("Expected a literal ID or a parameter resource.", parameterName);
        }
    }

    /// <summary>
    /// Appends web redirect URIs supplied as a literal, a parameter, or an endpoint.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="redirectUri">A full URI, a parameter containing comma-separated full URIs, or an endpoint. With callback paths, a parameter supplies a host instead.</param>
    /// <param name="callbackPaths">Callback paths starting with '/'. Required for an endpoint; with a host parameter, produces HTTPS URIs. Omit for full URIs.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>
    /// Call repeatedly to append values in order. Endpoint URLs remain deferred: run uses the allocated primary URL;
    /// publish/deploy uses the compute environment host. Parameter URI lists are split on commas without trimming.
    /// Existing registrations are not modified.
    /// </remarks>
    [AspireExport("withRedirectUris")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithRedirectUrisForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>), typeof(EndpointReference))] object redirectUri,
        string[]? callbackPaths = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(redirectUri);

        return redirectUri switch
        {
            string literal when callbackPaths is null => builder.WithRedirectUris(literal),
            IResourceBuilder<ParameterResource> parameter when callbackPaths is null => builder.WithRedirectUris(parameter),
            IResourceBuilder<ParameterResource> host when callbackPaths is { Length: > 0 } => builder.WithRedirectUris(host, callbackPaths[0], callbackPaths[1..]),
            EndpointReference endpoint when callbackPaths is not null => builder.WithRedirectUris(endpoint, callbackPaths),
            string => throw new ArgumentException("Callback paths require an endpoint or a host parameter, not a literal URI.", nameof(callbackPaths)),
            IResourceBuilder<ParameterResource> or EndpointReference => throw new ArgumentException("At least one callback path is required for a host or endpoint.", nameof(callbackPaths)),
            _ => throw new ArgumentException("Expected a literal URI, a parameter resource, or an endpoint.", nameof(redirectUri))
        };
    }

    /// <summary>
    /// Appends an application owner supplied as a literal directory object GUID or a parameter.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="ownerObjectId">The directory object ID of a user or service principal.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>Call repeatedly for multiple owners. Existing owners are not removed and existing registrations are not modified.</remarks>
    [AspireExport("withApplicationOwners")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithApplicationOwnersForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object ownerObjectId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(ownerObjectId);
        return ownerObjectId switch
        {
            string literal => builder.WithApplicationOwners(literal),
            IResourceBuilder<ParameterResource> parameter => builder.WithApplicationOwners(parameter),
            _ => throw new ArgumentException("Expected a directory object GUID or a parameter resource.", nameof(ownerObjectId))
        };
    }

    /// <summary>
    /// Appends a service principal owner supplied as a literal directory object GUID or a parameter.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="ownerObjectId">The directory object ID of a user or service principal.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>Service principal ownership is independent of application ownership. Existing registrations are not modified.</remarks>
    [AspireExport("withServicePrincipalOwners")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithServicePrincipalOwnersForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object ownerObjectId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(ownerObjectId);
        return ownerObjectId switch
        {
            string literal => builder.WithServicePrincipalOwners(literal),
            IResourceBuilder<ParameterResource> parameter => builder.WithServicePrincipalOwners(parameter),
            _ => throw new ArgumentException("Expected a directory object GUID or a parameter resource.", nameof(ownerObjectId))
        };
    }

    /// <summary>
    /// Appends an owner to both the application registration and service principal.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="ownerObjectId">The directory object GUID of a user or service principal, as a literal or parameter.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>Owners can manage the registration. Call repeatedly for multiple owners. Existing registrations are not modified.</remarks>
    [AspireExport("withOwners")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithOwnersForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        [AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object ownerObjectId)
    {
        return builder.WithApplicationOwnersForExport(ownerObjectId).WithServicePrincipalOwnersForExport(ownerObjectId);
    }

    /// <summary>
    /// Configures a pre-existing managed identity trust for token acquisition, without provisioning or assigning an identity.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="managedIdentityClientId">The client GUID of a user-assigned managed identity that the registration already trusts.</param>
    /// <param name="options">Optional token exchange settings.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>Applies in run and publish modes. This only emits consumer configuration; it does not create federation trust.</remarks>
    [AspireExport("withFederatedManagedIdentityCredential")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithFederatedManagedIdentityCredentialForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string managedIdentityClientId,
        EntraIdTokenExchangeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithCredential(new EntraIdFederatedIdentityCredential(managedIdentityClientId)
        {
            TokenExchangeUrl = options?.TokenExchangeUrl,
            TokenExchangeAuthority = options?.TokenExchangeAuthority
        });
    }

    /// <summary>
    /// Publishes managed identity federation trust for a registration created by Aspire.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="identity">A compute resource whose identity to trust, an explicit user-assigned identity, or no value to trust every consumer's own identity.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>
    /// Not used during local run. Throws for existing registrations. A selected compute resource must reference this registration.
    /// With an explicit identity, each consumer must use that identity. Only Azure public, US Government and China clouds are supported.
    /// </remarks>
    [AspireExport("publishWithFederatedManagedIdentityCredential")]
    internal static IResourceBuilder<EntraIdApplicationResource> PublishWithFederatedManagedIdentityCredentialForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        [AspireUnion(typeof(IResourceBuilder<IComputeResource>), typeof(IResourceBuilder<AzureUserAssignedIdentityResource>))] object? identity = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return identity switch
        {
            null => builder.PublishWithFederatedManagedIdentityCredential(),
            IResourceBuilder<AzureUserAssignedIdentityResource> managedIdentity => builder.PublishWithFederatedManagedIdentityCredential(managedIdentity),
            IResourceBuilder<IComputeResource> compute => builder.PublishWithFederatedManagedIdentityCredential(compute),
            _ => throw new ArgumentException("Expected a compute resource or a user-assigned managed identity.", nameof(identity))
        };
    }

    /// <summary>
    /// Declares an application role on a registration created by Aspire.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="value">The value emitted in the roles claim, for example Admin. It must be unique for the application, at most 120 characters, must not start with a period, and may contain only ASCII letters, digits and the punctuation that Microsoft Graph allows.</param>
    /// <param name="displayName">The display name shown in the Microsoft Entra admin center.</param>
    /// <param name="description">The description shown in assignment and consent experiences.</param>
    /// <param name="allowedMemberTypes">The member types the role can be assigned to, passed through to Microsoft Graph (for example User or Application). Defaults to User.</param>
    /// <param name="id">An optional role GUID. When omitted, a stable ID is derived from the registration's resource name and value.</param>
    /// <param name="isEnabled">Whether the role is enabled. Defaults to true. To retire a role, deploy it with false and leave it declared; disabling is not access revocation because existing assignments still receive the role in tokens.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>
    /// Application roles are separate from delegated scopes and from Azure RBAC roles. This only declares the role; it does not assign it or grant consent.
    /// Role IDs stay the same across deployments, and changing a role's value or ID creates a different role. Whenever at least one role is declared,
    /// the complete appRoles collection is sent, so include every role to keep. Microsoft Graph rejects deleting an enabled role.
    /// Existing registrations are never modified, so this throws for registrations created with addExistingEntraApplication.
    /// </remarks>
    [AspireExport("withAppRole")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithAppRoleForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string value,
        string displayName,
        string description,
        string[]? allowedMemberTypes = null,
        string? id = null,
        bool? isEnabled = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        Guid? roleId = null;
        if (id is not null)
        {
            roleId = Guid.TryParse(id, out var parsed)
                ? parsed
                : throw new ArgumentException($"'{id}' is not a valid role ID. Role IDs must be GUIDs.", nameof(id));
        }

        return builder.WithAppRole(new EntraIdAppRole(value, displayName, description, allowedMemberTypes, roleId, isEnabled ?? true));
    }

    /// <summary>
    /// Appends a certificate file credential. The file path is on the consuming resource, not the AppHost.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="filePath">The path to the certificate file on the consumer.</param>
    /// <param name="password">An optional secret parameter for a password-protected certificate.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>The password must be marked secret and remains a deferred parameter. File credentials are not recommended for production.</remarks>
    [AspireExport("withCertificateFromFile")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithCertificateFromFileForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string filePath,
        IResourceBuilder<ParameterResource>? password = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        return builder.WithCredential(new EntraIdFileCertificateCredential
        {
            FilePath = filePath,
            Password = password?.Resource
        });
    }

    /// <summary>
    /// Appends a signed assertion file credential, such as an AKS workload identity token.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="filePath">The token file path on the consumer. Omit to use the consumer's AZURE_FEDERATED_TOKEN_FILE setting.</param>
    /// <returns>The registration builder.</returns>
    [AspireExport("withSignedAssertionFile")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithSignedAssertionFileForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (filePath is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(filePath);
        }

        return builder.WithCredential(new EntraIdSignedAssertionFileCredential { FilePath = filePath });
    }

    /// <summary>
    /// Exposes the registration as an API identified by an application ID URI.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="applicationIdUri">A custom URI, such as api://contoso-orders or https://contoso.onmicrosoft.com/orders. Omit to use the default api://{clientId}.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>
    /// For a registration created by Aspire, the URI is written to the registration and must be unique in the tenant. The default api://{clientId}
    /// form is set by a second update of the registration, after Microsoft Graph creates it, and is accepted by the tenant's default policy.
    /// A custom URI fails deployment if the tenant's policy rejects it. For an existing registration, the URI is configuration only: Aspire does not
    /// modify the registration, and getScopeUri uses the URI to build scope names for callers.
    /// </remarks>
    [AspireExport("withApplicationIdUri")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithApplicationIdUriForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string? applicationIdUri = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return applicationIdUri is null
            ? builder.WithApplicationIdUri()
            : builder.WithApplicationIdUri(applicationIdUri);
    }

    /// <summary>
    /// Exposes a delegated permission scope that clients can request to call this registration as an API.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="name">The scope's name, which appears in the token's scp claim, such as access_as_user.</param>
    /// <param name="description">What the scope allows, shown on the consent page.</param>
    /// <param name="options">Optional settings for the scope.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>
    /// Scopes are declared explicitly. Unless an application ID URI is set with withApplicationIdUri, the registration uses api://{clientId}.
    /// The scope's ID stays the same across deployments. Microsoft Graph does not let a deployment remove an enabled scope or change its name:
    /// to retire a scope, deploy it with isEnabled set to false and remove it in a later deployment.
    /// For an existing registration the scope is configuration only, which lets callers declare permissions to it. Set its id to the ID the registration defines.
    /// </remarks>
    [AspireExport("withScope")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithScopeForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string name,
        string description,
        EntraIdScopeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithScope(name, description, scope =>
        {
            if (options is null)
            {
                return;
            }

            if (options.DisplayName is not null)
            {
                scope.DisplayName = options.DisplayName;
            }

            scope.UserConsentDisplayName = options.UserConsentDisplayName;
            scope.UserConsentDescription = options.UserConsentDescription;
            scope.RequiresAdminConsent = options.RequiresAdminConsent ?? false;
            scope.IsEnabled = options.IsEnabled ?? true;

            if (options.Id is not null)
            {
                scope.Id = Guid.TryParse(options.Id, out var id)
                    ? id
                    : throw new ArgumentException($"'{options.Id}' is not a valid scope ID. Scope IDs must be GUIDs.", nameof(options));
            }
        });
    }

    /// <summary>
    /// Gets an expression for the full URI of one of this registration's scopes, such as api://{clientId}/access_as_user.
    /// </summary>
    /// <param name="builder">The registration builder.</param>
    /// <param name="scopeName">The name of a scope added with withScope.</param>
    /// <returns>An expression that resolves when the registration's application ID URI is known.</returns>
    /// <remarks>
    /// Clients request the full URI as the token scope. Throws when the registration has no such scope, or is an existing registration without an application ID URI.
    /// </remarks>
    [AspireExport("getScopeUri")]
    internal static ReferenceExpression GetScopeUriForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string scopeName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Resource.GetScopeUri(scopeName);
    }

    /// <summary>
    /// Declares permissions to a Microsoft first-party or other API that is not an Aspire resource, such as Microsoft Graph.
    /// </summary>
    /// <param name="builder">The calling registration, which must be created by Aspire.</param>
    /// <param name="api">A well-known API, or an API identified by its application ID and application ID URI.</param>
    /// <param name="permissions">The permissions to declare. Each is delegated unless its type is Application.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>
    /// This adds the permissions to the registration's API permissions list. It grants nothing; use withExternalApiAdminConsent to opt in to consent.
    /// Delegated permissions let the application act for a signed-in user. Application permissions let it act as itself and always need administrator consent.
    /// The two kinds are separate permissions, even when they share a name. Well-known APIs resolve common permission names to their IDs;
    /// other permissions, and every permission of a custom API, need an explicit id.
    /// </remarks>
    [AspireExport("withExternalApiPermissions")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithExternalApiPermissionsForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        [AspireUnion(typeof(EntraIdWellKnownApi), typeof(EntraIdCustomApi))] object api,
        EntraIdExternalApiPermission[] permissions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithApiPermissions(ResolveApi(api), ResolvePermissions(permissions));
    }

    /// <summary>
    /// Grants tenant-wide consent to permissions that this registration declared for an API that is not an Aspire resource.
    /// </summary>
    /// <param name="builder">The calling registration, which must be created by Aspire.</param>
    /// <param name="api">A well-known API, or an API identified by its application ID and application ID URI.</param>
    /// <param name="permissions">Permissions already declared with withExternalApiPermissions.</param>
    /// <returns>The registration builder.</returns>
    /// <remarks>
    /// A delegated permission becomes a delegated permission grant for all users. An application permission becomes an app role assignment
    /// to this registration's service principal. The deploying identity needs a directory role or Microsoft Graph permission that allows consent, and deployment fails without it.
    /// Application permissions to broad scopes such as Directory.Read.All give the application access to the whole tenant, so grant only what the application needs.
    /// Throws when a permission was not declared first. This only affects registrations created by Aspire.
    /// </remarks>
    [AspireExport("withExternalApiAdminConsent")]
    internal static IResourceBuilder<EntraIdApplicationResource> WithExternalApiAdminConsentForExport(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        [AspireUnion(typeof(EntraIdWellKnownApi), typeof(EntraIdCustomApi))] object api,
        EntraIdExternalApiPermission[] permissions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithAdminConsent(ResolveApi(api), ResolvePermissions(permissions));
    }

    /// <summary>
    /// Injects Entra ID authentication configuration into a consuming resource and sets the prefix of its DownstreamApis entries.
    /// </summary>
    /// <param name="builder">The resource that receives the authentication configuration.</param>
    /// <param name="source">The Entra ID application resource to reference.</param>
    /// <param name="connectionName">The environment variable prefix. Defaults to AzureAd for .NET program resources and the registration's name otherwise.</param>
    /// <param name="downstreamApisPrefix">The configuration prefix of the DownstreamApis entries. Defaults to DownstreamApis.</param>
    /// <returns>The resource builder.</returns>
    /// <remarks>
    /// Behaves like withReference with the registration, which is enough for most references. Use this method when a resource references several registrations
    /// that declare permissions to the same API: give all but one of them their own downstreamApisPrefix, or the model is rejected.
    /// </remarks>
    [AspireExport("withEntraIdReference")]
    internal static IResourceBuilder<T> WithEntraIdReferenceForExport<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<EntraIdApplicationResource> source,
        string? connectionName = null,
        string? downstreamApisPrefix = null)
        where T : IResourceWithEnvironment
        => builder.WithReference(source, connectionName, downstreamApisPrefix);

    private static EntraIdApi ResolveApi(object api)
    {
        ArgumentNullException.ThrowIfNull(api);
        return api switch
        {
            EntraIdWellKnownApi.MicrosoftGraph => EntraIdApi.MicrosoftGraph,
            EntraIdWellKnownApi.AzureResourceManager => EntraIdApi.AzureResourceManager,
            EntraIdWellKnownApi.AzureStorage => EntraIdApi.AzureStorage,
            EntraIdCustomApi custom => EntraIdApi.FromApplicationId(custom.DisplayName, custom.ApplicationId, custom.ResourceUri, custom.BaseUrl),
            _ => throw new ArgumentException("Expected a well-known API or a custom API.", nameof(api))
        };
    }

    private static EntraIdApiPermission[] ResolvePermissions(EntraIdExternalApiPermission[] permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return [.. permissions.Select(ResolvePermission)];

        static EntraIdApiPermission ResolvePermission(EntraIdExternalApiPermission permission)
        {
            ArgumentNullException.ThrowIfNull(permission);

            Guid? id = null;
            if (permission.Id is not null)
            {
                id = Guid.TryParse(permission.Id, out var parsed)
                    ? parsed
                    : throw new ArgumentException($"'{permission.Id}' is not a valid permission ID. Permission IDs must be GUIDs.", nameof(permissions));
            }

            return (permission.Type ?? EntraIdPermissionType.Delegated, id) switch
            {
                (EntraIdPermissionType.Delegated, null) => EntraIdApiPermission.Delegated(permission.Name),
                (EntraIdPermissionType.Delegated, { } permissionId) => EntraIdApiPermission.Delegated(permission.Name, permissionId),
                (_, null) => EntraIdApiPermission.Application(permission.Name),
                (_, { } permissionId) => EntraIdApiPermission.Application(permission.Name, permissionId)
            };
        }
    }
}

/// <summary>
/// Optional settings for a scope exposed by a registration created by Aspire.
/// </summary>
[AspireDto]
internal sealed class EntraIdScopeOptions
{
    /// <summary>
    /// Gets the scope's title, shown on the admin consent page. Defaults to the scope's name.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets the scope's title shown on the consent page for users. Defaults to the display name.
    /// </summary>
    public string? UserConsentDisplayName { get; init; }

    /// <summary>
    /// Gets what the scope allows, shown on the consent page for users. Defaults to the scope's description.
    /// </summary>
    public string? UserConsentDescription { get; init; }

    /// <summary>
    /// Gets whether only an administrator can consent to the scope. Defaults to false, which lets users consent.
    /// </summary>
    public bool? RequiresAdminConsent { get; init; }

    /// <summary>
    /// Gets whether clients can request the scope. Defaults to true.
    /// </summary>
    public bool? IsEnabled { get; init; }

    /// <summary>
    /// Gets the scope's ID as a GUID, which must stay the same across deployments. Aspire derives a stable ID when omitted.
    /// </summary>
    public string? Id { get; init; }
}

/// <summary>
/// The Microsoft first-party APIs whose common permissions Aspire knows by name.
/// </summary>
internal enum EntraIdWellKnownApi
{
    /// <summary>
    /// Microsoft Graph.
    /// </summary>
    MicrosoftGraph,

    /// <summary>
    /// Azure Resource Manager, whose delegated permission is user_impersonation.
    /// </summary>
    AzureResourceManager,

    /// <summary>
    /// Azure Storage, whose delegated permission is user_impersonation.
    /// </summary>
    AzureStorage
}

/// <summary>
/// Identifies an API that is not an Aspire resource by its application ID and application ID URI.
/// </summary>
[AspireDto]
internal sealed class EntraIdCustomApi
{
    /// <summary>
    /// Gets a name for the API used in diagnostics and as the name of its DownstreamApis entry (its letters and digits).
    /// </summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    /// Gets the API's application (client) ID as a GUID.
    /// </summary>
    public string ApplicationId { get; init; } = "";

    /// <summary>
    /// Gets the API's application ID URI, the prefix of its scopes in token requests. It is not derived from the application ID because an API can use any URI.
    /// </summary>
    public string ResourceUri { get; init; } = "";

    /// <summary>
    /// Gets the API's root URL, which Aspire adds to the DownstreamApis entry as BaseUrl. When omitted, the caller must supply the address.
    /// </summary>
    public string? BaseUrl { get; init; }
}

/// <summary>
/// A permission of an API that is not an Aspire resource.
/// </summary>
[AspireDto]
internal sealed class EntraIdExternalApiPermission
{
    /// <summary>
    /// Gets the permission's name, such as User.Read.
    /// </summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// Gets the kind of permission. Defaults to a delegated permission.
    /// </summary>
    public EntraIdPermissionType? Type { get; init; }

    /// <summary>
    /// Gets the permission's ID as a GUID. Required for a custom API and for a permission that a well-known API does not list by name.
    /// </summary>
    public string? Id { get; init; }
}

/// <summary>
/// Optional token exchange settings for a pre-existing managed identity federation trust.
/// </summary>
[AspireDto]
internal sealed class EntraIdTokenExchangeOptions
{
    /// <summary>
    /// Gets the token exchange audience. Defaults to api://AzureADTokenExchange when omitted.
    /// </summary>
    public string? TokenExchangeUrl { get; init; }

    /// <summary>
    /// Gets the token exchange authority URL when it differs from the application's authority.
    /// </summary>
    public string? TokenExchangeAuthority { get; init; }
}
