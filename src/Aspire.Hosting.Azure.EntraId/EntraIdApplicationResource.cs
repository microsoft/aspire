// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents a Microsoft Entra ID application registration.
/// </summary>
/// <remarks>
/// <para>
/// Entra ID application registrations define the identity configuration for services
/// in a distributed application. Each application registration includes a tenant ID,
/// client ID, and optionally client credentials and API scopes.
/// </para>
/// <para>
/// This resource injects configuration as environment variables that map to
/// <c>IConfiguration</c> sections compatible with Microsoft.Identity.Web.
/// For example, <c>AzureAd__TenantId</c>, <c>AzureAd__ClientId</c>, etc.
/// </para>
/// <para>
/// The properties align with <c>MicrosoftEntraApplicationOptions</c> from
/// <c>Microsoft.Identity.Abstractions</c>.
/// </para>
/// </remarks>
/// <param name="name">The name of the resource.</param>
/// <param name="configSectionName">
/// The configuration section name used for environment variable prefixes.
/// Defaults to <c>"AzureAd"</c>.
/// </param>
public class EntraIdApplicationResource(string name, string configSectionName = "AzureAd")
    : Resource(name), IResourceWithEnvironment
{
    private const string DefaultInstance = "https://login.microsoftonline.com/";

    /// <summary>
    /// Gets the configuration section name used as the prefix for environment variables.
    /// </summary>
    /// <remarks>
    /// Environment variables are injected as <c>{ConfigSectionName}__{Key}</c>, which
    /// .NET's configuration system maps to <c>{ConfigSectionName}:{Key}</c> in <c>IConfiguration</c>.
    /// </remarks>
    public string ConfigSectionName { get; } = configSectionName;

    // ── Core identity ──────────────────────────────────────────────────────

    /// <summary>
    /// Gets or sets the Entra ID instance URL.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>https://login.microsoftonline.com/</c>. Override for sovereign clouds
    /// (e.g., <c>https://login.microsoftonline.us/</c> for Azure Government).
    /// </remarks>
    public string Instance { get; set; } = DefaultInstance;

    /// <summary>
    /// Gets or sets the parameter resource for the tenant ID.
    /// </summary>
    public ParameterResource? TenantIdParameter { get; set; }

    /// <summary>
    /// Gets or sets a fixed tenant ID value.
    /// </summary>
    /// <remarks>
    /// When both <see cref="TenantIdParameter"/> and <see cref="TenantId"/> are set,
    /// <see cref="TenantIdParameter"/> takes precedence.
    /// </remarks>
    public string? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the parameter resource for the client ID.
    /// </summary>
    public ParameterResource? ClientIdParameter { get; set; }

    /// <summary>
    /// Gets or sets a fixed client ID value.
    /// </summary>
    /// <remarks>
    /// When both <see cref="ClientIdParameter"/> and <see cref="ClientId"/> are set,
    /// <see cref="ClientIdParameter"/> takes precedence.
    /// </remarks>
    public string? ClientId { get; set; }

    // ── Per-app properties from MicrosoftEntraApplicationOptions ────────

    /// <summary>
    /// Gets or sets the home tenant of the app registration.
    /// </summary>
    /// <remarks>
    /// Useful for multi-tenant apps that acquire tokens on behalf of themselves.
    /// Also provides a direct path to the Azure Portal app registration.
    /// </remarks>
    public string? AppHomeTenantId { get; set; }

    // ── Token acquisition ──────────────────────────────────────────────────

    /// <summary>
    /// Gets the list of client credentials configured for this application.
    /// </summary>
    /// <remarks>
    /// Supports multiple credential types used by Microsoft.Identity.Web:
    /// <list type="bullet">
    /// <item><description><c>ClientSecret</c> — application secret</description></item>
    /// <item><description><c>SignedAssertionFromManagedIdentity</c> — managed identity credential</description></item>
    /// <item><description><c>Certificate</c> — certificate-based credential</description></item>
    /// </list>
    /// </remarks>
    internal List<EntraIdClientCredential> ClientCredentials { get; } = [];

    /// <summary>
    /// Gets or sets the client capabilities (e.g., <c>"cp1"</c> for Continuous Access Evaluation).
    /// </summary>
    internal List<string> ClientCapabilities { get; } = [];

    /// <summary>
    /// Gets or sets the Azure region for optimized token acquisition.
    /// </summary>
    /// <remarks>
    /// Use <c>"TryAutoDetect"</c> to have the app attempt to detect the region automatically.
    /// Per deployment — typically the same for all apps in a deployment.
    /// </remarks>
    public string? AzureRegion { get; set; }

    // ── Web API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the list of audiences accepted by this application registration.
    /// </summary>
    internal List<string> Audiences { get; } = [];

    /// <summary>
    /// Gets or sets whether to allow ACL-based authorization for daemon-to-API calls.
    /// </summary>
    /// <remarks>
    /// When <see langword="true"/>, the web API will not require roles or scopes in the token,
    /// allowing client credentials flow callers to be authorized by an Access Control List.
    /// </remarks>
    public bool AllowWebApiToBeAuthorizedByACL { get; set; }

    // ── Query parameters ───────────────────────────────────────────────────

    /// <summary>
    /// Gets extra query parameters to send to the identity provider.
    /// </summary>
    /// <remarks>
    /// Useful for routing to specific test slices or data centers.
    /// </remarks>
    internal Dictionary<string, string> ExtraQueryParameters { get; } = [];
}

/// <summary>
/// Base class for client credential entries in an Entra ID application registration.
/// </summary>
/// <remarks>
/// <para>
/// Each subclass corresponds to a specific <c>SourceType</c> in the Microsoft.Identity.Web
/// <c>ClientCredentials</c> configuration array. Use one of the concrete types:
/// </para>
/// <list type="bullet">
/// <item><description><see cref="EntraIdClientSecretCredential"/> — application secret.</description></item>
/// <item><description><see cref="EntraIdFederatedIdentityCredential"/> — FIC with managed identity.</description></item>
/// <item><description><see cref="EntraIdKeyVaultCertificateCredential"/> — certificate from Azure Key Vault.</description></item>
/// <item><description><see cref="EntraIdStoreCertificateCredential"/> — certificate from the certificate store.</description></item>
/// <item><description><see cref="EntraIdFileCertificateCredential"/> — certificate from a file on disk.</description></item>
/// <item><description><see cref="EntraIdSignedAssertionFileCredential"/> — signed assertion file (AKS workload identity).</description></item>
/// <item><description><see cref="EntraIdManagedCertificateCredential"/> — managed certificate (Microsoft-internal).</description></item>
/// </list>
/// </remarks>
public abstract class EntraIdClientCredential
{
    /// <summary>
    /// Gets the <c>SourceType</c> string used in the Microsoft.Identity.Web configuration.
    /// </summary>
    public abstract string SourceType { get; }

    /// <summary>
    /// Emits environment variables for this credential into the given context.
    /// </summary>
    /// <param name="envVars">The environment variable dictionary to populate.</param>
    /// <param name="prefix">The environment variable prefix (e.g., <c>"AzureAd__ClientCredentials__0"</c>).</param>
    internal virtual void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix)
    {
        envVars[$"{prefix}__SourceType"] = SourceType;
    }
}

/// <summary>
/// A client secret credential for an Entra ID application.
/// </summary>
/// <remarks>
/// Maps to <c>SourceType = "ClientSecret"</c> in Microsoft.Identity.Web configuration.
/// </remarks>
public sealed class EntraIdClientSecretCredential : EntraIdClientCredential
{
    /// <inheritdoc />
    public override string SourceType => "ClientSecret";

    /// <summary>
    /// Gets the client secret as a parameter resource.
    /// </summary>
    public required ParameterResource ClientSecret { get; init; }

    /// <inheritdoc />
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix)
    {
        base.EmitEnvironmentVariables(envVars, prefix);
        envVars[$"{prefix}__ClientSecret"] = ClientSecret;
    }
}

/// <summary>
/// A federated identity credential (FIC) with managed identity for an Entra ID application.
/// </summary>
/// <remarks>
/// Maps to <c>SourceType = "SignedAssertionFromManagedIdentity"</c> in Microsoft.Identity.Web configuration.
/// For system-assigned managed identity, leave <see cref="ManagedIdentityClientId"/> as <see langword="null"/>.
/// </remarks>
public sealed class EntraIdFederatedIdentityCredential : EntraIdClientCredential
{
    /// <inheritdoc />
    public override string SourceType => "SignedAssertionFromManagedIdentity";

    /// <summary>
    /// Gets or sets the client ID of a user-assigned managed identity.
    /// </summary>
    /// <remarks>
    /// Leave <see langword="null"/> for system-assigned managed identity.
    /// </remarks>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>
    /// Gets or sets the token exchange URL for federated identity credential scenarios.
    /// </summary>
    /// <remarks>
    /// If not specified, defaults to <c>api://AzureADTokenExchange</c>.
    /// </remarks>
    public string? TokenExchangeUrl { get; set; }

    /// <summary>
    /// Gets or sets the token exchange authority URL.
    /// </summary>
    /// <remarks>
    /// Used when the issuer for token exchange differs from the application's authority.
    /// </remarks>
    public string? TokenExchangeAuthority { get; set; }

    /// <inheritdoc />
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix)
    {
        base.EmitEnvironmentVariables(envVars, prefix);

        if (ManagedIdentityClientId is not null)
        {
            envVars[$"{prefix}__ManagedIdentityClientId"] = ManagedIdentityClientId;
        }

        if (TokenExchangeUrl is not null)
        {
            envVars[$"{prefix}__TokenExchangeUrl"] = TokenExchangeUrl;
        }

        if (TokenExchangeAuthority is not null)
        {
            envVars[$"{prefix}__TokenExchangeAuthority"] = TokenExchangeAuthority;
        }
    }
}

/// <summary>
/// A certificate credential from Azure Key Vault for an Entra ID application.
/// </summary>
/// <remarks>
/// Maps to <c>SourceType = "KeyVault"</c> in Microsoft.Identity.Web configuration.
/// </remarks>
public sealed class EntraIdKeyVaultCertificateCredential : EntraIdClientCredential
{
    /// <inheritdoc />
    public override string SourceType => "KeyVault";

    /// <summary>
    /// Gets the URL of the Azure Key Vault (e.g., <c>"https://myvault.vault.azure.net"</c>).
    /// </summary>
    public required string KeyVaultUrl { get; init; }

    /// <summary>
    /// Gets the name of the certificate in Azure Key Vault.
    /// </summary>
    public required string CertificateNameInKeyVault { get; init; }

    /// <inheritdoc />
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix)
    {
        base.EmitEnvironmentVariables(envVars, prefix);
        envVars[$"{prefix}__KeyVaultUrl"] = KeyVaultUrl;
        envVars[$"{prefix}__KeyVaultCertificateName"] = CertificateNameInKeyVault;
    }
}

/// <summary>
/// A certificate credential from the certificate store for an Entra ID application.
/// </summary>
/// <remarks>
/// <para>
/// Maps to <c>SourceType = "StoreWithThumbprint"</c> or <c>"StoreWithDistinguishedName"</c>
/// in Microsoft.Identity.Web configuration, depending on which identifier is provided.
/// </para>
/// <para>
/// Set either <see cref="Thumbprint"/> or <see cref="DistinguishedName"/> (not both).
/// </para>
/// </remarks>
public sealed class EntraIdStoreCertificateCredential : EntraIdClientCredential
{
    /// <inheritdoc />
    public override string SourceType
    {
        get
        {
            if (Thumbprint is null && DistinguishedName is null)
            {
                throw new InvalidOperationException(
                    $"Either {nameof(Thumbprint)} or {nameof(DistinguishedName)} must be set on {nameof(EntraIdStoreCertificateCredential)}.");
            }

            return Thumbprint is not null ? "StoreWithThumbprint" : "StoreWithDistinguishedName";
        }
    }

    /// <summary>
    /// Gets the certificate store path (e.g., <c>"CurrentUser/My"</c> or <c>"LocalMachine/My"</c>).
    /// </summary>
    public required string StorePath { get; init; }

    /// <summary>
    /// Gets or sets the certificate thumbprint.
    /// </summary>
    public string? Thumbprint { get; set; }

    /// <summary>
    /// Gets or sets the certificate distinguished name (e.g., <c>"CN=MyCert"</c>).
    /// </summary>
    public string? DistinguishedName { get; set; }

    /// <inheritdoc />
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix)
    {
        base.EmitEnvironmentVariables(envVars, prefix);
        envVars[$"{prefix}__CertificateStorePath"] = StorePath;

        if (Thumbprint is not null)
        {
            envVars[$"{prefix}__CertificateThumbprint"] = Thumbprint;
        }

        if (DistinguishedName is not null)
        {
            envVars[$"{prefix}__CertificateDistinguishedName"] = DistinguishedName;
        }
    }
}

/// <summary>
/// A certificate credential from a file on disk for an Entra ID application.
/// </summary>
/// <remarks>
/// Maps to <c>SourceType = "Path"</c> in Microsoft.Identity.Web configuration.
/// Not recommended for production use.
/// </remarks>
public sealed class EntraIdFileCertificateCredential : EntraIdClientCredential
{
    /// <inheritdoc />
    public override string SourceType => "Path";

    /// <summary>
    /// Gets the path to the certificate file (e.g., a PFX file).
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Gets or sets the password for the certificate file, if password-protected.
    /// </summary>
    public string? Password { get; set; }

    /// <inheritdoc />
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix)
    {
        base.EmitEnvironmentVariables(envVars, prefix);
        envVars[$"{prefix}__CertificateDiskPath"] = FilePath;

        if (Password is not null)
        {
            envVars[$"{prefix}__CertificatePassword"] = Password;
        }
    }
}

/// <summary>
/// A signed assertion file credential for an Entra ID application (e.g., AKS workload identity).
/// </summary>
/// <remarks>
/// Maps to <c>SourceType = "SignedAssertionFilePath"</c> in Microsoft.Identity.Web configuration.
/// If <see cref="FilePath"/> is not provided, the <c>AZURE_FEDERATED_TOKEN_FILE</c>
/// environment variable is used.
/// </remarks>
public sealed class EntraIdSignedAssertionFileCredential : EntraIdClientCredential
{
    /// <inheritdoc />
    public override string SourceType => "SignedAssertionFilePath";

    /// <summary>
    /// Gets or sets the path to the signed assertion file on disk.
    /// </summary>
    /// <remarks>
    /// If not specified, defaults to the <c>AZURE_FEDERATED_TOKEN_FILE</c> environment variable.
    /// </remarks>
    public string? FilePath { get; set; }

    /// <inheritdoc />
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix)
    {
        base.EmitEnvironmentVariables(envVars, prefix);

        if (FilePath is not null)
        {
            envVars[$"{prefix}__SignedAssertionFileDiskPath"] = FilePath;
        }
    }
}

/// <summary>
/// A managed certificate credential for an Entra ID application.
/// </summary>
/// <remarks>
/// <para>
/// Maps to <c>SourceType = "ManagedCertificate"</c> in Microsoft.Identity.Web configuration.
/// Azure manages the certificate lifecycle — no secrets, no manual rotation.
/// </para>
/// <para>
/// This is currently a Microsoft-internal concept. The managed certificate infrastructure
/// handles discovery, provisioning, and rotation automatically.
/// </para>
/// </remarks>
public sealed class EntraIdManagedCertificateCredential : EntraIdClientCredential
{
    /// <inheritdoc />
    public override string SourceType => "ManagedCertificate";
}
