// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Microsoft Entra ID resources to the application model.
/// </summary>
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
    /// into consuming services. The variables map to the <c>AzureAd</c> configuration section
    /// that Microsoft.Identity.Web reads natively.
    /// </para>
    /// <example>
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var entraApi = builder.AddEntraIdApplication("entra-api")
    ///     .AsExisting(
    ///         tenantId: builder.AddParameter("EntraTenantId"),
    ///         clientId: builder.AddParameter("EntraApiClientId"));
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithReference(entraApi);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> AddEntraIdApplication(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new EntraIdApplicationResource(name);
        return ConfigureEntraIdResource(builder.AddResource(resource));
    }

    /// <summary>
    /// Adds a Microsoft Entra ID application registration resource with a custom configuration section name.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/>.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="configSectionName">The configuration section name (e.g., <c>"AzureAd"</c>, <c>"AzureAdApi"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{EntraIdApplicationResource}"/>.</returns>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> AddEntraIdApplication(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string configSectionName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(configSectionName);

        var resource = new EntraIdApplicationResource(name, configSectionName);
        return ConfigureEntraIdResource(builder.AddResource(resource));
    }

    private static IResourceBuilder<EntraIdApplicationResource> ConfigureEntraIdResource(
        IResourceBuilder<EntraIdApplicationResource> resourceBuilder)
    {
        return resourceBuilder
            .WithIconName("ShieldKeyhole")
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "EntraIdApplication",
                State = KnownResourceStates.Running,
                Properties = []
            })
            .OnInitializeResource(async (resource, evt, ct) =>
            {
                var urls = ImmutableArray.CreateBuilder<UrlSnapshot>();

                var instance = resource.Instance.TrimEnd('/');
                var tenantId = resource.TenantId;
                var clientId = resource.ClientId;

                // OpenID Configuration endpoint
                if (tenantId is not null)
                {
                    urls.Add(new UrlSnapshot(
                        "OpenID Config",
                        $"{instance}/{tenantId}/v2.0/.well-known/openid-configuration",
                        IsInternal: false)
                    {
                        DisplayProperties = new UrlDisplayPropertiesSnapshot($"OIDC {instance}")
                    });
                }

                // Azure Portal app registration link
                if (clientId is not null)
                {
                    urls.Add(new UrlSnapshot(
                        "Azure Portal",
                        $"https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationMenuBlade/~/Overview/appId/{clientId}/isMSAApp~/false",
                        IsInternal: false)
                    {
                        DisplayProperties = new UrlDisplayPropertiesSnapshot("App registration")
                    });
                }

                await evt.Notifications.PublishUpdateAsync(resource, snapshot => snapshot with
                {
                    Urls = [.. urls],
                    State = KnownResourceStates.Running
                }).ConfigureAwait(false);
            });
    }

    /// <summary>
    /// Marks this Entra ID application as an existing resource, identified by its tenant ID and client ID.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="tenantId">A parameter containing the tenant ID (GUID) of the directory where the app is registered.</param>
    /// <param name="clientId">A parameter containing the client ID (application ID) of the existing app registration.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Both the tenant ID and client ID are required to identify an existing Entra ID application.
    /// Unlike ARM resources that can be identified by name and resource group, Entra ID app registrations
    /// live in a specific tenant directory and are uniquely identified by their client ID within that tenant.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> AsExisting(
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
    /// <param name="tenantId">The tenant ID (GUID) of the directory where the app is registered.</param>
    /// <param name="clientId">The client ID (application ID) of the existing app registration.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Both the tenant ID and client ID are required to identify an existing Entra ID application.
    /// Unlike ARM resources that can be identified by name and resource group, Entra ID app registrations
    /// live in a specific tenant directory and are uniquely identified by their client ID within that tenant.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> AsExisting(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string tenantId,
        string clientId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(tenantId);
        ArgumentException.ThrowIfNullOrEmpty(clientId);

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
    /// <remarks>
    /// <para>
    /// This adds an entry to the <c>ClientCredentials</c> array in the Microsoft.Identity.Web
    /// configuration with <c>SourceType</c> set to <c>"ClientSecret"</c>.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> WithClientSecret(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        IResourceBuilder<ParameterResource> clientSecret)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(clientSecret);

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
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
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
    /// Adds a managed certificate credential to this Entra ID application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This adds an entry to the <c>ClientCredentials</c> array in the Microsoft.Identity.Web
    /// configuration with <c>SourceType</c> set to <c>"ManagedCertificate"</c>.
    /// Azure manages the certificate lifecycle — no secrets or manual rotation required.
    /// </para>
    /// <para>
    /// This is currently a Microsoft-internal concept.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> WithManagedCertificate(
        this IResourceBuilder<EntraIdApplicationResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.ClientCredentials.Add(new EntraIdManagedCertificateCredential());

        return builder;
    }

    /// <summary>
    /// Adds a certificate credential from Azure Key Vault to this Entra ID application.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="keyVaultUrl">The URL of the Key Vault (e.g., <c>"https://myvault.vault.azure.net"</c>).</param>
    /// <param name="certificateName">The name of the certificate in Key Vault.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
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
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
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
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
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
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
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
    /// <param name="instance">The Entra ID instance URL (e.g., <c>https://login.microsoftonline.us/</c> for sovereign clouds).</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> WithInstance(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string instance)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(instance);

        builder.Resource.Instance = instance;
        return builder;
    }

    /// <summary>
    /// Configures the home tenant ID for this app registration.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="appHomeTenantId">The tenant ID where the app is registered.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// Useful for multi-tenant apps and for navigating to the Azure Portal app registration.
    /// </remarks>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> WithAppHomeTenantId(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string appHomeTenantId)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(appHomeTenantId);

        builder.Resource.AppHomeTenantId = appHomeTenantId;
        return builder;
    }

    /// <summary>
    /// Adds a client capability (e.g., <c>"cp1"</c> for Continuous Access Evaluation).
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="capability">The capability identifier.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
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
    /// Configures the Azure region for optimized token acquisition.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="azureRegion">The Azure region (e.g., <c>"westus2"</c>) or <c>"TryAutoDetect"</c>.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> WithAzureRegion(
        this IResourceBuilder<EntraIdApplicationResource> builder,
        string azureRegion)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(azureRegion);

        builder.Resource.AzureRegion = azureRegion;
        return builder;
    }

    /// <summary>
    /// Enables ACL-based authorization for daemon-to-API scenarios.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<EntraIdApplicationResource> WithAllowWebApiToBeAuthorizedByACL(
        this IResourceBuilder<EntraIdApplicationResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Resource.AllowWebApiToBeAuthorizedByACL = true;
        return builder;
    }

    /// <summary>
    /// Adds an extra query parameter to send to the identity provider.
    /// </summary>
    /// <param name="builder">The resource builder.</param>
    /// <param name="key">The query parameter key.</param>
    /// <param name="value">The query parameter value.</param>
    /// <returns>The resource builder for chaining.</returns>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
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
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
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
    /// Injects Entra ID authentication configuration into a consuming service as environment
    /// variables that map to the Microsoft.Identity.Web configuration section.
    /// </summary>
    /// <typeparam name="T">The type of the destination resource.</typeparam>
    /// <param name="builder">The resource that will receive the authentication configuration.</param>
    /// <param name="source">The Entra ID application resource to reference.</param>
    /// <returns>The resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method injects environment variables in the format <c>{ConfigSectionName}__{Key}</c>
    /// (e.g., <c>AzureAd__TenantId</c>, <c>AzureAd__ClientId</c>) which .NET's configuration
    /// system automatically maps to hierarchical configuration sections.
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
    ///     .AsExisting(
    ///         tenantId: tenantId,
    ///         clientId: clientId);
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithReference(entraApi);
    /// </code>
    /// </example>
    /// </remarks>
    [AspireExportIgnore(Reason = "Entra ID resources are not yet ATS-compatible for polyglot AppHosts.")]
    public static IResourceBuilder<T> WithReference<T>(
        this IResourceBuilder<T> builder,
        IResourceBuilder<EntraIdApplicationResource> source)
        where T : IResourceWithEnvironment
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);

        var entra = source.Resource;
        var prefix = entra.ConfigSectionName;

        // Create a reference relationship so the dashboard shows the connection
        builder.WithReferenceRelationship(entra);

        builder.WithEnvironment(context =>
        {
            // Core identity properties
            context.EnvironmentVariables[$"{prefix}__Instance"] = entra.Instance;

            if (entra.TenantIdParameter is not null)
            {
                context.EnvironmentVariables[$"{prefix}__TenantId"] = entra.TenantIdParameter;
            }
            else if (entra.TenantId is not null)
            {
                context.EnvironmentVariables[$"{prefix}__TenantId"] = entra.TenantId;
            }

            if (entra.ClientIdParameter is not null)
            {
                context.EnvironmentVariables[$"{prefix}__ClientId"] = entra.ClientIdParameter;
            }
            else if (entra.ClientId is not null)
            {
                context.EnvironmentVariables[$"{prefix}__ClientId"] = entra.ClientId;
            }

            if (entra.AppHomeTenantId is not null)
            {
                context.EnvironmentVariables[$"{prefix}__AppHomeTenantId"] = entra.AppHomeTenantId;
            }

            // Always send X5C for easy certificate rollover
            context.EnvironmentVariables[$"{prefix}__SendX5C"] = "true";

            // Token acquisition
            if (entra.AzureRegion is not null)
            {
                context.EnvironmentVariables[$"{prefix}__AzureRegion"] = entra.AzureRegion;
            }

            // Client credentials — each type emits its own env vars
            for (var i = 0; i < entra.ClientCredentials.Count; i++)
            {
                var credPrefix = $"{prefix}__ClientCredentials__{i}";
                entra.ClientCredentials[i].EmitEnvironmentVariables(context.EnvironmentVariables, credPrefix);
            }

            // Client capabilities (e.g., "cp1" for CAE)
            for (var i = 0; i < entra.ClientCapabilities.Count; i++)
            {
                context.EnvironmentVariables[$"{prefix}__ClientCapabilities__{i}"] = entra.ClientCapabilities[i];
            }

            // Audiences
            for (var i = 0; i < entra.Audiences.Count; i++)
            {
                context.EnvironmentVariables[$"{prefix}__Audiences__{i}"] = entra.Audiences[i];
            }

            // Web API authorization
            if (entra.AllowWebApiToBeAuthorizedByACL)
            {
                context.EnvironmentVariables[$"{prefix}__AllowWebApiToBeAuthorizedByACL"] = "true";
            }

            // Extra query parameters
            foreach (var kvp in entra.ExtraQueryParameters)
            {
                context.EnvironmentVariables[$"{prefix}__ExtraQueryParameters__{kvp.Key}"] = kvp.Value;
            }
        });

        return builder;
    }
}
