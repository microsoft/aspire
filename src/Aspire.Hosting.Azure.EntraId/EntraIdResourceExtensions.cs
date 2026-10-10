// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Microsoft Entra ID resources to the application model.
/// </summary>
[AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
public static class EntraIdResourceExtensions
{
    /// <summary>
    /// Adds a Microsoft Entra ID application registration resource to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{EntraIdApplicationResource}"/>.</returns>
    /// <remarks>
    /// <para>
    /// The Entra ID application resource injects configuration as environment variables
    /// into consuming services. For .NET programs, the variables default to the <c>AzureAd</c> configuration section
    /// that Microsoft.Identity.Web reads natively.
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var entraApi = builder.AddEntraIdApplication("entra-api")
    ///     .AsExistingApplication(
    ///         tenantId: builder.AddParameter("EntraTenantId"),
    ///         clientId: builder.AddParameter("EntraApiClientId"));
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithReference(entraApi);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<EntraIdApplicationResource> AddEntraIdApplication(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new EntraIdApplicationResource(name);
        return ConfigureEntraIdResource(builder.AddResource(resource));
    }

    private static IResourceBuilder<EntraIdApplicationResource> ConfigureEntraIdResource(
        IResourceBuilder<EntraIdApplicationResource> resourceBuilder)
    {
        return resourceBuilder
            .WithIconName("ShieldKeyhole")
            // The resource waits until its IDs are known, which takes a while when they come from parameters that the
            // dashboard prompts for. Initialization then validates them and moves the resource to Running or FailedToStart.
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "EntraIdApplication",
                State = KnownResourceStates.Waiting,
                Properties = []
            })
            // The resource only describes an app registration that already exists, so there is nothing to deploy.
            .ExcludeFromManifest()
            .OnInitializeResource(InitializeEntraIdResourceAsync);
    }

    private static async Task InitializeEntraIdResourceAsync(
        EntraIdApplicationResource resource,
        InitializeResourceEvent evt,
        CancellationToken cancellationToken)
    {
        var logger = evt.Logger;

        if ((resource.TenantIdParameter is null && resource.TenantId is null) ||
            (resource.ClientIdParameter is null && resource.ClientId is null))
        {
            logger.LogError("The app registration is not fully configured. Call AsExistingApplication with the registration's tenant ID and client ID.");
            await PublishFailedToStartAsync(resource, evt).ConfigureAwait(false);
            return;
        }

        // Check every setting before failing, so that a misconfigured resource reports all of its problems at once
        // rather than one per restart of the AppHost.
        var tenantId = await ResolveIdAsync(resource.TenantIdParameter, resource.TenantId, "tenant ID", EntraIdValidation.ValidateTenantId, logger, cancellationToken).ConfigureAwait(false);
        var clientId = await ResolveIdAsync(resource.ClientIdParameter, resource.ClientId, "client ID", EntraIdValidation.ValidateClientId, logger, cancellationToken).ConfigureAwait(false);
        var hasInvalidSetting = false;

        if (EntraIdValidation.ValidateInstance(resource.Instance) is { } instanceError)
        {
            logger.LogError("The instance '{Instance}' is not valid. {Reason}", resource.Instance, instanceError);
            hasInvalidSetting = true;
        }

        if (!Enum.IsDefined(resource.SignInAudience))
        {
            logger.LogError("The sign-in audience '{SignInAudience}' is not a valid {EnumType} value.", resource.SignInAudience, nameof(EntraIdSignInAudience));
            hasInvalidSetting = true;
        }

        // A misconfigured credential would otherwise surface only when a referencing app starts and its environment
        // variables are evaluated, which reports the problem on the wrong resource.
        for (var i = 0; i < resource.ClientCredentials.Count; i++)
        {
            try
            {
                _ = resource.ClientCredentials[i].SourceType;
            }
            catch (InvalidOperationException ex)
            {
                logger.LogError("Client credential {Index} is not valid. {Reason}", i, ex.Message);
                hasInvalidSetting = true;
            }
        }

        if (hasInvalidSetting || tenantId is null || clientId is null)
        {
            await PublishFailedToStartAsync(resource, evt).ConfigureAwait(false);
            return;
        }

        try
        {
            // Other resources that stand in for an external service publish this event before reporting Running, which
            // runs the OnBeforeResourceStarted callbacks registered on the resource.
            await evt.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource, evt.Services), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "A {EventName} subscriber failed.", nameof(BeforeResourceStartedEvent));
            await PublishFailedToStartAsync(resource, evt).ConfigureAwait(false);
            return;
        }

        // Apps that accept other tenants sign users in through a keyword such as "organizations" instead of the home
        // tenant, so their discovery document lives under the keyword while the registration lives in the home tenant.
        var signInTenant = resource.SignInTenantKeyword ?? tenantId;
        var urls = ImmutableArray.CreateBuilder<UrlSnapshot>();
        urls.Add(new UrlSnapshot("OpenID Config", EntraIdUrls.GetOpenIdConfigurationUrl(resource.Instance, signInTenant), IsInternal: false)
        {
            DisplayProperties = new UrlDisplayPropertiesSnapshot("OpenID configuration")
        });

        if (EntraIdUrls.GetPortalUrl(resource.Instance, tenantId, clientId) is { } portalUrl)
        {
            urls.Add(new UrlSnapshot("Azure Portal", portalUrl, IsInternal: false)
            {
                DisplayProperties = new UrlDisplayPropertiesSnapshot("App registration")
            });
        }

        var properties = ImmutableArray.CreateBuilder<ResourcePropertySnapshot>();
        properties.Add(CreateHighlightedProperty("entra.tenant.id", "Tenant ID", tenantId, isSensitive: resource.TenantIdParameter?.Secret ?? false, sortOrder: 0));
        properties.Add(CreateHighlightedProperty("entra.client.id", "Client ID", clientId, isSensitive: resource.ClientIdParameter?.Secret ?? false, sortOrder: 1));
        properties.Add(CreateHighlightedProperty("entra.signin.audience", "Sign-in audience", resource.SignInAudience.ToString(), isSensitive: false, sortOrder: 2));
        properties.Add(CreateHighlightedProperty("entra.instance", "Instance", resource.Instance, isSensitive: false, sortOrder: 3));
        if (resource.ClientCredentials.Count > 0)
        {
            var sourceTypes = string.Join(", ", resource.ClientCredentials.Select(c => c.SourceType));
            properties.Add(CreateHighlightedProperty("entra.credentials", "Credentials", sourceTypes, isSensitive: false, sortOrder: 4));
        }

        // Append rather than replace, so URLs and properties that the orchestrator already published, such as ones added
        // with WithUrl or a parent relationship, are kept. Initialization runs once, so nothing is added twice.
        await evt.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            Urls = snapshot.Urls.AddRange(urls),
            Properties = snapshot.Properties.AddRange(properties),
            State = KnownResourceStates.Running
        }).ConfigureAwait(false);
    }

    // Gets an ID from its parameter, or the fixed value when there is no parameter, and validates it. Problems are
    // logged to the resource's console and reported by returning null. Parameter values are not logged because the
    // parameter may be secret; fixed values come from AppHost code and are logged to make the mistake easy to find.
    private static async Task<string?> ResolveIdAsync(
        ParameterResource? parameter,
        string? value,
        string setting,
        Func<string?, string?> validate,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (parameter is null)
        {
            if (validate(value) is { } error)
            {
                logger.LogError("The {Setting} '{Value}' is not valid. {Reason}", setting, value, error);
                return null;
            }

            return value;
        }

        string? parameterValue;
        try
        {
            parameterValue = await parameter.GetValueAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to get the value of the {Setting} parameter '{ParameterName}'.", setting, parameter.Name);
            return null;
        }

        if (validate(parameterValue) is { } parameterError)
        {
            logger.LogError("Parameter '{ParameterName}' does not contain a valid {Setting}. {Reason}", parameter.Name, setting, parameterError);
            return null;
        }

        return parameterValue;
    }

    private static Task PublishFailedToStartAsync(EntraIdApplicationResource resource, InitializeResourceEvent evt)
    {
        return evt.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            State = KnownResourceStates.FailedToStart
        });
    }

    // Unknown property names are hidden in the dashboard unless they are highlighted.
    private static ResourcePropertySnapshot CreateHighlightedProperty(string name, string displayName, string value, bool isSensitive, int sortOrder)
    {
        return new(name, value)
        {
            DisplayName = displayName,
            IsHighlighted = true,
            IsSensitive = isSensitive,
            SortOrder = sortOrder
        };
    }

    /// <summary>
    /// Marks this Entra ID application as an existing resource, identified by its tenant ID and client ID.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="tenantId">
    /// A parameter containing the ID of the app's home tenant: the directory where the app is registered, shown as
    /// "Directory (tenant) ID" on the app registration's Overview page.
    /// </param>
    /// <param name="clientId">
    /// A parameter containing the application (client) ID, shown as "Application (client) ID" on the app registration's
    /// Overview page.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Both the tenant ID and client ID are required to identify an existing Entra ID application.
    /// Unlike ARM resources that can be identified by name and resource group, Entra ID app registrations
    /// live in a specific tenant directory and are uniquely identified by their client ID within that tenant.
    /// </para>
    /// <para>
    /// The parameter values are validated when the application starts. The tenant ID must be a GUID or a domain such as
    /// <c>contoso.onmicrosoft.com</c>, and the client ID must be a GUID. When a value is missing or malformed, the resource
    /// reports <c>FailedToStart</c> and logs the reason.
    /// </para>
    /// <para>
    /// This is deliberately not named <c>AsExisting</c>. Aspire's Azure resources use
    /// <c>AsExisting(name, resourceGroup)</c>, so reusing that name here would give the same call shape
    /// two different meanings once this resource participates in Azure provisioning.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<EntraIdApplicationResource> AsExistingApplication(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        IResourceBuilder<ParameterResource> tenantId,
        IResourceBuilder<ParameterResource> clientId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tenantId);
        ArgumentNullException.ThrowIfNull(clientId);

        builder.Resource.TenantIdParameter = tenantId.Resource;
        builder.Resource.ClientIdParameter = clientId.Resource;
        return builder;
    }

    /// <summary>
    /// Marks this Entra ID application as an existing resource, identified by its tenant ID and client ID.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="tenantId">
    /// The ID of the app's home tenant: the directory where the app is registered, shown as "Directory (tenant) ID" on the
    /// app registration's Overview page. A domain such as <c>contoso.onmicrosoft.com</c> is also accepted.
    /// </param>
    /// <param name="clientId">
    /// The application (client) ID, shown as "Application (client) ID" on the app registration's Overview page.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="tenantId"/> is empty, is not a GUID or domain name, or is one of the sign-in audience
    /// keywords <c>organizations</c>, <c>common</c> or <c>consumers</c>; or when <paramref name="clientId"/> is empty
    /// or is not a GUID.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Both the tenant ID and client ID are required to identify an existing Entra ID application.
    /// Unlike ARM resources that can be identified by name and resource group, Entra ID app registrations
    /// live in a specific tenant directory and are uniquely identified by their client ID within that tenant.
    /// </para>
    /// <para>
    /// To let accounts from other tenants sign in, keep the home tenant here and call
    /// <see cref="WithSignInAudience"/>.
    /// </para>
    /// <para>
    /// This is deliberately not named <c>AsExisting</c>. Aspire's Azure resources use
    /// <c>AsExisting(name, resourceGroup)</c>, so reusing that name here would give the same call shape
    /// two different meanings once this resource participates in Azure provisioning.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<EntraIdApplicationResource> AsExistingApplication(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string tenantId,
        string clientId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(clientId);

        if (EntraIdValidation.ValidateTenantId(tenantId) is { } tenantIdError)
        {
            throw new ArgumentException($"'{tenantId}' is not a valid tenant ID. {tenantIdError}", nameof(tenantId));
        }

        if (EntraIdValidation.ValidateClientId(clientId) is { } clientIdError)
        {
            throw new ArgumentException($"'{clientId}' is not a valid client ID. {clientIdError}", nameof(clientId));
        }

        builder.Resource.TenantId = tenantId;
        builder.Resource.ClientId = clientId;
        return builder;
    }

    /// <summary>
    /// Adds a client secret credential to this Entra ID application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="clientSecret">A secret parameter containing the client secret.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="clientSecret"/> is not marked as a secret parameter.
    /// </exception>
    /// <remarks>
    /// <para>
    /// This adds an entry to the <c>ClientCredentials</c> array in the Microsoft.Identity.Web
    /// configuration with <c>SourceType</c> set to <c>"ClientSecret"</c>.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<EntraIdApplicationResource> WithClientSecret(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        IResourceBuilder<ParameterResource> clientSecret)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(clientSecret);

        if (!clientSecret.Resource.Secret)
        {
            throw new ArgumentException("The client secret parameter must be marked as secret. Use AddParameter with secret: true when creating the parameter.", nameof(clientSecret));
        }

        builder.Resource.ClientCredentials.Add(new EntraIdClientSecretCredential
        {
            ClientSecret = clientSecret.Resource
        });

        return builder;
    }

    /// <summary>
    /// Adds a federated identity credential (FIC) with managed identity to this Entra ID application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="managedIdentityClientId">
    /// The client ID of a user-assigned managed identity.
    /// If <see langword="null"/>, the system-assigned managed identity is used.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This adds an entry to the <c>ClientCredentials</c> array in the Microsoft.Identity.Web
    /// configuration with <c>SourceType</c> set to <c>"SignedAssertionFromManagedIdentity"</c>.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<EntraIdApplicationResource> WithFicMsi(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string? managedIdentityClientId = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ClientCredentials.Add(new EntraIdFederatedIdentityCredential
        {
            ManagedIdentityClientId = managedIdentityClientId
        });

        return builder;
    }

    /// <summary>
    /// Adds a certificate credential from Azure Key Vault to this Entra ID application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="keyVaultUrl">The URL of the Key Vault (e.g., <c>"https://myvault.vault.azure.net"</c>).</param>
    /// <param name="certificateName">The name of the certificate in Key Vault.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<EntraIdApplicationResource> WithCertificateFromKeyVault(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string keyVaultUrl,
        string certificateName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(keyVaultUrl);
        ArgumentException.ThrowIfNullOrEmpty(certificateName);

        builder.Resource.ClientCredentials.Add(new EntraIdKeyVaultCertificateCredential
        {
            KeyVaultUrl = keyVaultUrl,
            CertificateNameInKeyVault = certificateName
        });

        return builder;
    }

    /// <summary>
    /// Adds a certificate credential from the certificate store by thumbprint.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="storePath">The certificate store path (e.g., <c>"CurrentUser/My"</c>).</param>
    /// <param name="thumbprint">The certificate thumbprint.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<EntraIdApplicationResource> WithCertificateThumbprint(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string storePath,
        string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(storePath);
        ArgumentException.ThrowIfNullOrEmpty(thumbprint);

        builder.Resource.ClientCredentials.Add(new EntraIdStoreCertificateCredential
        {
            StorePath = storePath,
            Thumbprint = thumbprint
        });

        return builder;
    }

    /// <summary>
    /// Adds a certificate credential from the certificate store by distinguished name.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="storePath">The certificate store path (e.g., <c>"CurrentUser/My"</c>).</param>
    /// <param name="distinguishedName">The certificate distinguished name (e.g., <c>"CN=MyCert"</c>).</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<EntraIdApplicationResource> WithCertificateDistinguishedName(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string storePath,
        string distinguishedName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(storePath);
        ArgumentException.ThrowIfNullOrEmpty(distinguishedName);

        builder.Resource.ClientCredentials.Add(new EntraIdStoreCertificateCredential
        {
            StorePath = storePath,
            DistinguishedName = distinguishedName
        });

        return builder;
    }

    /// <summary>
    /// Adds a raw credential entry for advanced scenarios not covered by convenience methods.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="credential">A fully configured <see cref="EntraIdClientCredential"/>.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<EntraIdApplicationResource> WithCredential(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        EntraIdClientCredential credential)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credential);

        builder.Resource.ClientCredentials.Add(credential);
        return builder;
    }

    /// <summary>
    /// Configures the Entra ID instance URL. Defaults to <c>https://login.microsoftonline.com/</c>.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="instance">
    /// The Entra ID instance URL, such as <c>https://login.microsoftonline.us/</c> for Azure Government or
    /// <c>https://contoso.ciamlogin.com/</c> for an external tenant.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="instance"/> is empty or is not an absolute HTTPS URL without a query string or fragment.
    /// </exception>
    public static IResourceBuilder<EntraIdApplicationResource> WithInstance(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string instance)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(instance);

        if (EntraIdValidation.ValidateInstance(instance) is { } error)
        {
            throw new ArgumentException($"'{instance}' is not a valid Entra ID instance. {error}", nameof(instance));
        }

        builder.Resource.Instance = instance;
        return builder;
    }

    /// <summary>
    /// Configures which accounts can sign in to this application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="signInAudience">
    /// The accounts that can sign in. It must match the "Supported account types" setting of the app registration.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="signInAudience"/> is not a defined <see cref="EntraIdSignInAudience"/> value.
    /// </exception>
    /// <remarks>
    /// <para>
    /// By default only accounts in the app's home tenant can sign in, and referencing services receive the home tenant
    /// as <c>TenantId</c>. For the other audiences they receive the keyword that Microsoft.Identity.Web uses to accept
    /// those accounts (<c>organizations</c>, <c>common</c> or <c>consumers</c>) as <c>TenantId</c>, and the home tenant
    /// as <c>AppHomeTenantId</c>, which Microsoft.Identity.Web uses when the app acquires tokens as itself.
    /// </para>
    /// <para>
    /// Users from any tenant that the audience allows can then sign in, so check the tenant ID (<c>tid</c>) claim of
    /// incoming tokens before giving access to tenant-specific data. For more information, see
    /// <a href="https://learn.microsoft.com/entra/identity-platform/claims-validation">Secure applications and APIs by validating claims</a>.
    /// </para>
    /// <example>
    /// Let work or school accounts from any organization sign in:
    /// <code lang="csharp">
    /// var entraWeb = builder.AddEntraIdApplication("entra-web")
    ///     .AsExistingApplication(tenantId, clientId)
    ///     .WithSignInAudience(EntraIdSignInAudience.AzureADMultipleOrgs);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<EntraIdApplicationResource> WithSignInAudience(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        EntraIdSignInAudience signInAudience)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!Enum.IsDefined(signInAudience))
        {
            throw new ArgumentOutOfRangeException(nameof(signInAudience), signInAudience, $"The value is not a defined {nameof(EntraIdSignInAudience)} value.");
        }

        builder.Resource.SignInAudience = signInAudience;
        return builder;
    }

    /// <summary>
    /// Adds a client capability (e.g., <c>"cp1"</c> for Continuous Access Evaluation).
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="capability">The capability identifier.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<EntraIdApplicationResource> WithClientCapability(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string capability)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(capability);

        builder.Resource.ClientCapabilities.Add(capability);
        return builder;
    }

    /// <summary>
    /// Enables ACL-based authorization for daemon-to-API scenarios.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<EntraIdApplicationResource> WithAllowWebApiToBeAuthorizedByACL(
        this IResourceBuilder<EntraIdApplicationResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.AllowWebApiToBeAuthorizedByACL = true;
        return builder;
    }

    /// <summary>
    /// Sends the <c>x5c</c> claim (the public key of the certificate) with token requests.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// Sending <c>x5c</c> enables certificate rollover without redeploying the application. It only applies
    /// to certificate-based credentials and increases the size of each token request, so it is opt-in.
    /// </remarks>
    public static IResourceBuilder<EntraIdApplicationResource> WithSendX5C(
        this IResourceBuilder<EntraIdApplicationResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.SendX5C = true;
        return builder;
    }

    /// <summary>
    /// Adds an extra query parameter to send to the identity provider.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="key">The query parameter key.</param>
    /// <param name="value">The query parameter value.</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<EntraIdApplicationResource> WithExtraQueryParameter(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string key,
        string value)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(value);

        builder.Resource.ExtraQueryParameters[key] = value;
        return builder;
    }

    /// <summary>
    /// Adds an accepted audience for this application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="audience">The audience value (e.g., <c>api://&lt;client-id&gt;</c>).</param>
    /// <returns>The resource builder for chaining.</returns>
    public static IResourceBuilder<EntraIdApplicationResource> WithAudience(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string audience)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(audience);

        builder.Resource.Audiences.Add(audience);
        return builder;
    }

    /// <summary>
    /// Injects Entra ID authentication configuration into a consuming resource.
    /// </summary>
    /// <typeparam name="T">The type of the destination resource.</typeparam>
    /// <param name="builder">The resource that will receive the authentication configuration.</param>
    /// <param name="source">The Entra ID application resource to reference.</param>
    /// <param name="connectionName">
    /// The environment variable prefix. When <see langword="null"/>, uses <c>"AzureAd"</c> for .NET program resources;
    /// otherwise, uses the source resource's name encoded as a portable environment variable name and uppercased.
    /// When empty, omits the prefix and its separator, producing <c>ClientId</c> for .NET program resources and <c>CLIENT_ID</c> for other resources.
    /// For .NET program resources, <c>:</c> in the name is replaced by <c>__</c>.
    /// </param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// .NET program resources use <c>__</c> between keys and array indexes; other resources use <c>_</c>.
    /// Non-.NET environment variable names, including custom connection names, use uppercase snake case,
    /// such as <c>ENTRA_API_CLIENT_ID</c> and <c>ENTRA_API_CLIENT_CREDENTIALS_0_SOURCE_TYPE</c>.
    /// Configuration values retain their original casing.
    /// The default names, such as <c>AzureAd__TenantId</c> and <c>AzureAd__ClientId</c>, map to the
    /// <c>AzureAd</c> configuration section in .NET and are compatible with Microsoft.Identity.Web.
    /// </para>
    /// <para>
    /// The consuming service can then use Microsoft.Identity.Web's standard configuration:
    /// </para>
    /// <code lang="csharp">
    /// builder.Services.AddAuthentication()
    ///     .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
    /// </code>
    /// <example>
    /// <code lang="csharp">
    /// var entraApi = builder.AddEntraIdApplication("entra-api")
    ///     .AsExistingApplication(
    ///         tenantId: tenantId,
    ///         clientId: clientId);
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithReference(entraApi);
    /// </code>
    /// </example>
    /// </remarks>
    public static IResourceBuilder<T> WithReference<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<EntraIdApplicationResource> source,
        string? connectionName = null)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        // Inspect the resource itself so .NET defaults also apply when the builder's type has been erased.
#pragma warning disable ASPIREPROJECTS001
        var isDotnetProgram = builder.Resource is IDotnetProgramResource;
#pragma warning restore ASPIREPROJECTS001
        connectionName ??= isDotnetProgram ? "AzureAd" : EnvironmentVariableNameEncoder.Encode(source.Resource.Name);

        if (isDotnetProgram)
        {
            connectionName = connectionName.Replace(":", "__", StringComparison.Ordinal);
        }

        return WithReferenceCore(builder, source, connectionName, isDotnetProgram);
    }

    private static IResourceBuilder<T> WithReferenceCore<T>(
        IResourceBuilder<T> builder,
        IResourceBuilder<EntraIdApplicationResource> source,
        string connectionName,
        bool isDotnetProgram)
        where T : IResourceWithEnvironment
    {
        var entra = source.Resource;
        var separator = isDotnetProgram ? "__" : "_";
        var keyPrefix = connectionName.Length == 0 ? "" : $"{connectionName}{separator}";

        // Create a reference relationship so the dashboard shows the connection
        builder.WithReferenceRelationship(entra);

        builder.WithEnvironment(context =>
        {
            // Collect only this reference's settings so normalization never changes unrelated environment variables.
            var environmentVariables = new Dictionary<string, object>();

            // Core identity properties
            environmentVariables[$"{keyPrefix}Instance"] = entra.Instance;

            // Microsoft.Identity.Web builds the sign-in authority from Instance and TenantId, so TenantId decides who can
            // sign in. Audiences beyond the home tenant need a keyword such as "organizations" there, but a keyword can't
            // be used when the app acquires tokens as itself, so Microsoft.Identity.Web falls back to AppHomeTenantId for
            // those requests and fails when it isn't set. See
            // https://learn.microsoft.com/entra/identity-platform/howto-convert-app-to-be-multi-tenant
            object? homeTenantId = (object?)entra.TenantIdParameter ?? entra.TenantId;
            if (entra.SignInTenantKeyword is { } signInTenantKeyword)
            {
                environmentVariables[$"{keyPrefix}TenantId"] = signInTenantKeyword;

                if (homeTenantId is not null)
                {
                    environmentVariables[$"{keyPrefix}AppHomeTenantId"] = homeTenantId;
                }
            }
            else if (homeTenantId is not null)
            {
                environmentVariables[$"{keyPrefix}TenantId"] = homeTenantId;
            }

            if (entra.ClientIdParameter is not null)
            {
                environmentVariables[$"{keyPrefix}ClientId"] = entra.ClientIdParameter;
            }
            else if (entra.ClientId is not null)
            {
                environmentVariables[$"{keyPrefix}ClientId"] = entra.ClientId;
            }

            // Send the x5c claim only when explicitly requested. It enables certificate rollover but
            // is only meaningful for certificate credentials, so it is opt-in via WithSendX5C().
            if (entra.SendX5C)
            {
                environmentVariables[$"{keyPrefix}SendX5C"] = "true";
            }

            // Client credentials — each type emits its own env vars
            for (var i = 0; i < entra.ClientCredentials.Count; i++)
            {
                var credPrefix = $"{keyPrefix}ClientCredentials{separator}{i}";
                entra.ClientCredentials[i].EmitEnvironmentVariables(environmentVariables, credPrefix, separator);
            }

            // Client capabilities (e.g., "cp1" for CAE)
            for (var i = 0; i < entra.ClientCapabilities.Count; i++)
            {
                environmentVariables[$"{keyPrefix}ClientCapabilities{separator}{i}"] = entra.ClientCapabilities[i];
            }

            // Audiences
            for (var i = 0; i < entra.Audiences.Count; i++)
            {
                environmentVariables[$"{keyPrefix}Audiences{separator}{i}"] = entra.Audiences[i];
            }

            // Web API authorization
            if (entra.AllowWebApiToBeAuthorizedByACL)
            {
                environmentVariables[$"{keyPrefix}AllowWebApiToBeAuthorizedByACL"] = "true";
            }

            // Extra query parameters
            foreach (var kvp in entra.ExtraQueryParameters)
            {
                environmentVariables[$"{keyPrefix}ExtraQueryParameters{separator}{kvp.Key}"] = kvp.Value;
            }

            if (!isDotnetProgram)
            {
                environmentVariables = environmentVariables.ToDictionary(
                    kvp => JsonNamingPolicy.SnakeCaseUpper.ConvertName(EnvironmentVariableNameEncoder.Encode(kvp.Key)),
                    kvp => kvp.Value);
            }

            foreach (var variable in environmentVariables)
            {
                context.EnvironmentVariables[variable.Key] = variable.Value;
            }
        });

        return builder;
    }
}
