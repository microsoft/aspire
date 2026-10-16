// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Existing registrations remove the framework's provisioning annotations.
#pragma warning disable ASPIREAZURE003 // Tenant-scoped resource annotations are experimental.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.EntraId.Provisioning;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;

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
/// Aspire creates new registrations using a resource-group-scoped Microsoft Graph Bicep deployment.
/// Use <c>AddExistingEntraApplication</c> to reference an existing registration without deploying infrastructure.
/// </para>
/// <para>
/// References to this resource inject configuration as environment variables.
/// .NET program references default to names such as <c>AzureAd__TenantId</c> and <c>AzureAd__ClientId</c>,
/// compatible with Microsoft.Identity.Web. Other references default to an uppercase resource-name prefix
/// and a single underscore separator, and can configure their own prefix and separator.
/// </para>
/// <para>
/// The properties align with <c>MicrosoftEntraApplicationOptions</c> from
/// <c>Microsoft.Identity.Abstractions</c>.
/// </para>
/// </remarks>
public class EntraIdApplicationResource : AzureProvisioningResource
{
    private const string DefaultInstance = "https://login.microsoftonline.com/";
    private readonly IResourceAnnotation[] _provisioningAnnotations;

    /// <summary>
    /// Initializes a new instance of the <see cref="EntraIdApplicationResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    public EntraIdApplicationResource(string name)
        : base(name, ConfigureApplicationInfrastructure)
    {
        Annotations.Add(new TenantScopedResourceAnnotation(new Dictionary<string, string>
        {
            [EntraIdBicepConstants.ApplicationResourceType] = EntraIdBicepConstants.ObjectIdOutputName,
            [EntraIdBicepConstants.ServicePrincipalResourceType] = EntraIdBicepConstants.ServicePrincipalIdOutputName
        }));
        Parameters[KnownParameters.TenantResourceSuffix] = null;
        _provisioningAnnotations = [.. Annotations.Where(annotation => annotation is PipelineStepAnnotation or PipelineConfigurationAnnotation)];
    }

    internal bool IsExistingApplication { get; private set; }

    internal void SetExistingApplication()
    {
        IsExistingApplication = true;
        ProvisioningTaskCompletionSource = null;

        // Configuration-only registrations must not generate Bicep or provisioning tasks. Remove only the base
        // resource's annotations so any pipeline steps added by the AppHost are preserved.
        foreach (var annotation in _provisioningAnnotations)
        {
            Annotations.Remove(annotation);
        }
    }

    /// <summary>
    /// Gets the application (client) ID produced when the registration is provisioned.
    /// </summary>
    /// <remarks>
    /// This output is available for applications created by Aspire. Existing applications use
    /// <see cref="ClientIdParameter"/> or <see cref="ClientId"/> instead.
    /// </remarks>
    public BicepOutputReference ClientIdOutputReference => new(EntraIdBicepConstants.ClientIdOutputName, this);

    /// <summary>
    /// Gets the home tenant ID produced when the registration is provisioned.
    /// </summary>
    /// <remarks>
    /// Registrations are created in the tenant of the Azure subscription used for provisioning.
    /// Existing applications use <see cref="TenantIdParameter"/> or <see cref="TenantId"/> instead.
    /// </remarks>
    public BicepOutputReference TenantIdOutputReference => new(EntraIdBicepConstants.TenantIdOutputName, this);

    internal object? HomeTenantIdValue => IsExistingApplication
        ? (object?)TenantIdParameter ?? TenantId
        : TenantIdOutputReference;

    internal object? ClientIdValue => IsExistingApplication
        ? (object?)ClientIdParameter ?? ClientId
        : ClientIdOutputReference;

    /// <inheritdoc />
    public override string GetBicepTemplateString()
    {
        ValidateProvisioningConfiguration();
        return base.GetBicepTemplateString();
    }

    /// <inheritdoc />
    public override BicepTemplateFile GetBicepTemplateFile(string? directory = null, bool deleteTemporaryFileOnDispose = true)
    {
        ValidateProvisioningConfiguration();
        return base.GetBicepTemplateFile(directory, deleteTemporaryFileOnDispose);
    }

    private void ValidateProvisioningConfiguration()
    {
        if (IsExistingApplication || this.HasAnnotationOfType<ExistingAzureResourceAnnotation>())
        {
            throw new InvalidOperationException(
                "Entra ID applications are identified by tenant ID and client ID, not by an Azure resource name or resource group. " +
                "Use AddExistingEntraApplication(name, tenantId, clientId) to reference an existing registration without provisioning it.");
        }

        if (Scope is { HasResourceGroup: false })
        {
            throw new InvalidOperationException("Entra ID application templates must be deployed at resource-group scope.");
        }

        if (!Enum.IsDefined(SignInAudience))
        {
            throw new InvalidOperationException($"'{SignInAudience}' is not a valid {nameof(EntraIdSignInAudience)} value.");
        }

        if (EntraIdValidation.ValidateInstance(Instance) is { } instanceError)
        {
            throw new InvalidOperationException($"The instance '{Instance}' is not valid. {instanceError}");
        }

        // Provisioning and resource initialization complete independently. Reject invalid credentials before
        // the Azure controller can report Running, rather than leaving a consumer to fail when it resolves them.
        foreach (var credential in ClientCredentials)
        {
            _ = credential.SourceType;
        }
    }

    private static BicepList<string> CreateRedirectUris(EntraIdApplicationResource resource, AzureResourceInfrastructure infrastructure)
    {
        var values = resource._redirectUriValues;
        if (!values.OfType<ParameterResource>().Any())
        {
            return [.. values.OfType<string>()];
        }

        // Parameter values are only known at deployment time, so each is split on commas in Bicep. Consecutive
        // literals are grouped into one array to keep the call order:
        //   concat(['https://literal'], split(redirectUris, ','), split(otherRedirectUris, ','))
        var arguments = new List<BicepExpression>();
        var literals = new List<BicepExpression>();
        foreach (var value in values)
        {
            if (value is string literal)
            {
                literals.Add(new StringLiteralExpression(literal));
                continue;
            }

            if (literals.Count > 0)
            {
                arguments.Add(new ArrayExpression([.. literals]));
                literals.Clear();
            }

            arguments.Add(new FunctionCallExpression(
                new IdentifierExpression("split"),
                ((ParameterResource)value).AsProvisioningParameter(infrastructure).Value.Compile(),
                new StringLiteralExpression(",")));
        }

        if (literals.Count > 0)
        {
            arguments.Add(new ArrayExpression([.. literals]));
        }

        BicepExpression expression = arguments.Count == 1
            ? arguments[0]
            : new FunctionCallExpression(new IdentifierExpression("concat"), [.. arguments]);
        return BicepList<string>.FromExpression(static _ => string.Empty, expression);
    }

    private static void ConfigureApplicationInfrastructure(AzureResourceInfrastructure infrastructure)
    {
        var resource = (EntraIdApplicationResource)infrastructure.AspireResource;
        resource.ValidateProvisioningConfiguration();
        // Directory objects have no Azure location, so omit the default location parameter.
        infrastructure.Remove(infrastructure.GetProvisionableResources().OfType<ProvisioningParameter>()
            .Single(parameter => parameter.BicepIdentifier == KnownParameters.Location));
        infrastructure.BicepExtensions["microsoftGraphV1"] = "br:mcr.microsoft.com/bicep/extensions/microsoftgraph/v1.0:1.0.0";

        var tenantResourceSuffix = new ProvisioningParameter(KnownParameters.TenantResourceSuffix, typeof(string))
        {
            Value = string.Empty
        };
        BicepValue<string> registrationName = new ConditionalExpression(
            new BinaryExpression(new IdentifierExpression(tenantResourceSuffix.BicepIdentifier), BinaryBicepOperator.Equal, new StringLiteralExpression(string.Empty)),
            new StringLiteralExpression(resource.Name),
            BicepFunction.Interpolate($"{resource.Name}-{tenantResourceSuffix}").Compile());
        infrastructure.Add(tenantResourceSuffix);

        var application = new MicrosoftGraphApplication("application")
        {
            DisplayName = registrationName,
            UniqueName = registrationName,
            SignInAudience = resource.SignInAudience.ToString(),
            RequestedAccessTokenVersion = 2,
            RedirectUris = CreateRedirectUris(resource, infrastructure),
            EnableIdTokenIssuance = resource.EnableIdTokenIssuance
        };
        infrastructure.Add(application);

        var servicePrincipal = new MicrosoftGraphServicePrincipal("servicePrincipal")
        {
            AppId = application.AppId
        };
        infrastructure.Add(servicePrincipal);

        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.ClientIdOutputName, typeof(string)) { Value = application.AppId.ToBicepExpression() });
        // Compile the scope-function property directly; ToBicepExpression would reference an undeclared "tenant" resource.
        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.TenantIdOutputName, typeof(string)) { Value = BicepFunction.GetTenant().TenantId.Compile() });
        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.ObjectIdOutputName, typeof(string)) { Value = application.Id.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.ServicePrincipalIdOutputName, typeof(string)) { Value = servicePrincipal.Id.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.UniqueNameOutputName, typeof(string)) { Value = application.UniqueName.ToBicepExpression() });
    }

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
    /// Gets or sets the parameter resource for the ID of the app's home tenant.
    /// </summary>
    /// <remarks>
    /// The home tenant is the directory where the app is registered, shown as "Directory (tenant) ID"
    /// on the app registration's Overview page.
    /// </remarks>
    public ParameterResource? TenantIdParameter { get; set; }

    /// <summary>
    /// Gets or sets a fixed ID of the app's home tenant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The home tenant is the directory where the app is registered, shown as "Directory (tenant) ID"
    /// on the app registration's Overview page. It can be a GUID or a domain such as <c>contoso.onmicrosoft.com</c>.
    /// </para>
    /// <para>
    /// When both <see cref="TenantIdParameter"/> and <see cref="TenantId"/> are set,
    /// <see cref="TenantIdParameter"/> takes precedence.
    /// </para>
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

    /// <summary>
    /// Gets or sets which accounts can sign in to the application.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="EntraIdSignInAudience.AzureADMyOrg"/>, which allows only accounts in the home tenant.
    /// The value must match the "Supported account types" setting of the app registration.
    /// </remarks>
    public EntraIdSignInAudience SignInAudience { get; set; } = EntraIdSignInAudience.AzureADMyOrg;

    /// <summary>
    /// Gets the literal web redirect URIs to register for an application created by Aspire.
    /// </summary>
    /// <remarks>
    /// These URIs are written directly into the generated infrastructure. Endpoints are not discovered automatically,
    /// and existing registrations are not modified. Use <see cref="RedirectUriParameters"/> for URIs supplied at deployment time.
    /// </remarks>
    public IReadOnlyList<string> RedirectUris => [.. _redirectUriValues.OfType<string>()];

    /// <summary>
    /// Gets the parameters whose values are comma-separated lists of web redirect URIs to register for an application created by Aspire.
    /// </summary>
    /// <remarks>
    /// The values are supplied at deployment time and split on commas in the generated infrastructure.
    /// </remarks>
    public IReadOnlyList<ParameterResource> RedirectUriParameters => [.. _redirectUriValues.OfType<ParameterResource>()];

    // Holds string or ParameterResource entries in the order they were added, so registration order matches call order.
    private readonly List<object> _redirectUriValues = [];

    internal void AddRedirectUri(string redirectUri) => _redirectUriValues.Add(redirectUri);

    internal void AddRedirectUri(ParameterResource redirectUri) => _redirectUriValues.Add(redirectUri);

    /// <summary>
    /// Gets or sets whether a created registration can issue ID tokens through implicit and hybrid flows.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. This does not enable implicit access-token issuance and is not required
    /// for authorization-code-only flows. Existing registrations are not modified.
    /// </remarks>
    public bool EnableIdTokenIssuance { get; set; }

    /// <summary>
    /// Gets the keyword that Microsoft.Identity.Web expects as <c>TenantId</c> for <see cref="SignInAudience"/>,
    /// or <see langword="null"/> when the app is single-tenant and uses its home tenant instead.
    /// </summary>
    /// <remarks>
    /// For more information about the keywords, see
    /// <a href="https://learn.microsoft.com/entra/identity-platform/v2-protocols#endpoints">Microsoft identity platform endpoints</a>.
    /// </remarks>
    internal string? SignInTenantKeyword => SignInAudience switch
    {
        EntraIdSignInAudience.AzureADMyOrg => null,
        EntraIdSignInAudience.AzureADMultipleOrgs => "organizations",
        EntraIdSignInAudience.AzureADandPersonalMicrosoftAccount => "common",
        EntraIdSignInAudience.PersonalMicrosoftAccount => "consumers",
        _ => throw new InvalidOperationException($"'{SignInAudience}' is not a valid {nameof(EntraIdSignInAudience)} value.")
    };

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
    /// Gets the client capabilities (e.g., <c>"cp1"</c> for Continuous Access Evaluation).
    /// </summary>
    internal List<string> ClientCapabilities { get; } = [];

    /// <summary>
    /// Gets or sets whether to send the <c>x5c</c> claim (the public key of the certificate) with the token request.
    /// </summary>
    /// <remarks>
    /// Sending <c>x5c</c> enables easy certificate rollover, but it is only meaningful for certificate-based
    /// credentials and increases request size, so it is opt-in via <c>WithSendX5C()</c>.
    /// </remarks>
    public bool SendX5C { get; set; }

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
    /// <param name="separator">The separator between configuration keys.</param>
    internal virtual void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator)
    {
        envVars[$"{prefix}{separator}SourceType"] = SourceType;
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
    public required ParameterResource ClientSecret
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!value.Secret)
            {
                throw new ArgumentException("The client secret parameter must be marked as secret. Use AddParameter with secret: true when creating the parameter.", nameof(ClientSecret));
            }

            field = value;
        }
    }

    /// <inheritdoc />
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator);
        envVars[$"{prefix}{separator}ClientSecret"] = ClientSecret;
    }
}

/// <summary>
/// A federated identity credential (FIC) with managed identity for an Entra ID application.
/// </summary>
/// <remarks>
/// Maps to <c>SourceType = "SignedAssertionFromManagedIdentity"</c> in Microsoft.Identity.Web configuration.
/// The registration must trust a user-assigned managed identity in the same tenant, and the identity must
/// be attached to the workload. This configuration does not create that trust or assign the identity.
/// See <see href="https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity"/>.
/// </remarks>
public sealed class EntraIdFederatedIdentityCredential : EntraIdClientCredential
{
    /// <inheritdoc />
    public override string SourceType => "SignedAssertionFromManagedIdentity";

    /// <summary>
    /// Gets or sets the client ID of the user-assigned managed identity that supplies the signed assertion.
    /// </summary>
    /// <remarks>
    /// Use the identity's client ID, not the object (principal) ID used as the federated credential's subject.
    /// Microsoft Entra's managed-identity federation scenario supports user-assigned identities only.
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown when the value is empty or is not a GUID.</exception>
    public required string ManagedIdentityClientId
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (EntraIdValidation.ValidateManagedIdentityClientId(value) is { } error)
            {
                throw new ArgumentException(error, nameof(ManagedIdentityClientId));
            }

            field = value;
        }
    }

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
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator);

        envVars[$"{prefix}{separator}ManagedIdentityClientId"] = ManagedIdentityClientId;

        if (TokenExchangeUrl is not null)
        {
            envVars[$"{prefix}{separator}TokenExchangeUrl"] = TokenExchangeUrl;
        }

        if (TokenExchangeAuthority is not null)
        {
            envVars[$"{prefix}{separator}TokenExchangeAuthority"] = TokenExchangeAuthority;
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
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator);
        envVars[$"{prefix}{separator}KeyVaultUrl"] = KeyVaultUrl;
        envVars[$"{prefix}{separator}KeyVaultCertificateName"] = CertificateNameInKeyVault;
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
/// Set exactly one of <see cref="Thumbprint"/> or <see cref="DistinguishedName"/>. Setting both,
/// or neither, causes <see cref="SourceType"/> to throw an <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
public sealed class EntraIdStoreCertificateCredential : EntraIdClientCredential
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// Thrown when neither or both of <see cref="Thumbprint"/> and <see cref="DistinguishedName"/> are set.
    /// </exception>
    public override string SourceType => (Thumbprint, DistinguishedName) switch
    {
        (not null, null) => "StoreWithThumbprint",
        (null, not null) => "StoreWithDistinguishedName",
        (not null, not null) => throw new InvalidOperationException(
            $"Only one of {nameof(Thumbprint)} or {nameof(DistinguishedName)} can be set on {nameof(EntraIdStoreCertificateCredential)}."),
        _ => throw new InvalidOperationException(
            $"Either {nameof(Thumbprint)} or {nameof(DistinguishedName)} must be set on {nameof(EntraIdStoreCertificateCredential)}.")
    };

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
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator);
        envVars[$"{prefix}{separator}CertificateStorePath"] = StorePath;

        if (Thumbprint is not null)
        {
            envVars[$"{prefix}{separator}CertificateThumbprint"] = Thumbprint;
        }
        else if (DistinguishedName is not null)
        {
            envVars[$"{prefix}{separator}CertificateDistinguishedName"] = DistinguishedName;
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
    /// <remarks>
    /// This is a <see cref="ParameterResource"/> rather than a <see cref="string"/> so the value flows through
    /// Aspire's parameter pipeline and is redacted in logs and the dashboard like any other secret.
    /// The parameter must be created with <c>secret: true</c>.
    /// </remarks>
    public ParameterResource? Password
    {
        get;
        set
        {
            if (value is { Secret: false })
            {
                throw new ArgumentException("The certificate password parameter must be marked as secret. Use AddParameter with secret: true when creating the parameter.", nameof(Password));
            }

            field = value;
        }
    }

    /// <inheritdoc />
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator);
        envVars[$"{prefix}{separator}CertificateDiskPath"] = FilePath;

        if (Password is not null)
        {
            envVars[$"{prefix}{separator}CertificatePassword"] = Password;
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
    internal override void EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator);

        if (FilePath is not null)
        {
            envVars[$"{prefix}{separator}SignedAssertionFileDiskPath"] = FilePath;
        }
    }
}
