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
