// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Existing registrations remove the framework's provisioning annotations.
#pragma warning disable ASPIREAZURE003 // Tenant-scoped resource annotations are experimental.

using System.IO.Hashing;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.EntraId.Provisioning;
using Aspire.Hosting.Pipelines;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Primitives;

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
[AspireExport]
public class EntraIdApplicationResource : AzureProvisioningResource, IResourceWithCustomWithReference<EntraIdApplicationResource>
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
        // Existing registrations are never provisioned, so SetExistingApplication removes this along with the base annotations.
        Annotations.Add(new PipelineStepAnnotation(_ => new PipelineStep
        {
            Name = $"validate-{name}-managed-identities",
            Description = $"Validates the managed identities that Entra ID application {name} trusts.",
            // The Azure resource preparer attaches each compute resource's identity, so the identities can only be checked after it.
            Action = context =>
            {
                ValidateComputeTargets();
                if (context.ExecutionContext.IsPublishMode)
                {
                    ValidateManagedIdentities();
                }

                return Task.CompletedTask;
            },
            DependsOnSteps = [AzureEnvironmentResource.PrepareResourcesStepName],
            RequiredBySteps = [WellKnownPipelineSteps.BeforeStart]
        }));

        _provisioningAnnotations = [.. Annotations.Where(annotation => annotation is PipelineStepAnnotation or PipelineConfigurationAnnotation)];
    }

    private bool _hasApiAccessValidation;

    // Consumers and API permissions are only complete once the model is built, so the check runs before anything starts. The step is added
    // with the first declaration, not up front, so a registration that declares nothing keeps no pipeline steps, as an existing one must.
    internal void EnsureApiAccessValidation()
    {
        if (_hasApiAccessValidation)
        {
            return;
        }

        _hasApiAccessValidation = true;
        Annotations.Add(new PipelineStepAnnotation(_ => new PipelineStep
        {
            Name = $"validate-{Name}-api-access",
            Description = $"Validates the API permissions that Entra ID application {Name} declares.",
            Action = _ =>
            {
                ValidateApiAccess();
                return Task.CompletedTask;
            },
            RequiredBySteps = [WellKnownPipelineSteps.BeforeStart]
        }));
    }

    internal bool IsExistingApplication { get; private set; }

    static IResourceBuilder<TDestination>? IResourceWithCustomWithReference<EntraIdApplicationResource>.TryWithReference<TDestination>(
        IResourceBuilder<TDestination> builder,
        IResourceBuilder<IResource> source,
        string? connectionName,
        bool optional,
        string? name)
    {
        if (source is not IResourceBuilder<EntraIdApplicationResource> application)
        {
            return null;
        }

        if (optional)
        {
            throw new InvalidOperationException("Optional references are not supported for Entra ID applications.");
        }

        if (name is not null)
        {
            throw new InvalidOperationException("Named service references are not supported for Entra ID applications. Use connectionName to customize the configuration prefix.");
        }

        return EntraIdResourceExtensions.WithReference(builder, application, connectionName);
    }

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
        // The base class caches the first template it generates. The pipeline generates it before the Azure resource
        // preparer attaches the identities this registration trusts, so a cached template would lack the federated
        // credentials, and the deployment checksum computed from it would not change when federation is turned on.
        using var template = GetBicepTemplateFile();
        return File.ReadAllText(template.Path);
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

        if (ClientCredentials.OfType<EntraIdFederatedIdentityCredential>().Any(c => c.IsResourceBacked) &&
            EntraIdFederation.GetTokenExchangeAudience(Instance) is null)
        {
            throw new InvalidOperationException(
                $"The instance '{Instance}' has no known managed identity token exchange audience. " +
                "Managed identity federation is supported for the Azure public, US Government and China clouds.");
        }
    }

    // Consumers are recorded as they reference the registration so that each one's own identity can be trusted.
    internal List<IResource> Consumers { get; } = [];

    // A scope can still be disabled after WithApiPermissions or WithPreAuthorizedApplication accepted it, because EntraIdScope.IsEnabled is settable.
    // Emitting such a reference would leave a client requesting a scope that the API no longer lets anyone request, so the references are checked
    // again once the model is complete. Only the registration's own declarations are checked: each registration checks the ones it makes.
    internal void ValidateScopeReferences()
    {
        foreach (var access in ApiAccess.Where(static a => a.Application is not null))
        {
            foreach (var scope in access.Declared.Select(static p => p.Scope).OfType<EntraIdScope>().Where(static s => !s.IsEnabled))
            {
                throw new InvalidOperationException(
                    $"'{Name}' declares permissions to the scope '{scope.Name}' of '{access.DisplayName}', but the scope is disabled, so clients can't request it. " +
                    $"Remove it from WithApiPermissions (and WithAdminConsent) on '{Name}', or enable the scope.");
            }
        }

        foreach (var preAuthorized in PreAuthorizedClients)
        {
            foreach (var scope in preAuthorized.Scopes.Where(static s => !s.IsEnabled))
            {
                throw new InvalidOperationException(
                    $"'{Name}' pre-authorizes '{preAuthorized.Client.Name}' for the scope '{scope.Name}', but the scope is disabled, so clients can't request it. " +
                    $"Remove it from WithPreAuthorizedApplication on '{Name}', or enable the scope.");
            }
        }
    }

    // Checks that the DownstreamApis entries that the registration's consumers receive can be built and are distinct. Only the
    // resources that reference this registration get entries, and an entry is named after a resource that references the API, so
    // this can only be checked once the whole model exists. Both the startup pipeline and the consumers' environment run it.
    internal void ValidateApiAccess()
    {
        ValidateScopeReferences();

        if (Consumers.Count == 0)
        {
            return;
        }

        var entries = new Dictionary<string, EntraIdApiAccess>(StringComparer.OrdinalIgnoreCase);
        foreach (var access in ApiAccess)
        {
            if (access.Application is { IsExistingApplication: true, ApplicationIdUri: null } existingApi)
            {
                throw new InvalidOperationException(
                    $"'{Name}' declares permissions to the existing registration '{existingApi.Name}', but its application ID URI is not configured, " +
                    $"so the scope names to request can't be built. Call WithApplicationIdUri on '{existingApi.Name}' with the URI that it defines.");
            }

            foreach (var entry in access.GetDownstreamApis())
            {
                if (entries.TryAdd(entry.Name, access))
                {
                    continue;
                }

                var other = entries[entry.Name];
                throw new InvalidOperationException(access.Application is not null && other.Application is not null
                    ? $"'{Name}' declares permissions to the registrations '{other.DisplayName}' and '{access.DisplayName}', and both would be configured as the " +
                      $"DownstreamApis entry '{entry.Name}', the name of a resource that references both. An entry holds the scopes of one API, so the consumer " +
                      $"couldn't tell them apart, and one token request can't cover both. Reference '{other.DisplayName}' and '{access.DisplayName}' from different resources."
                    : $"'{Name}' declares permissions to '{other.DisplayName}' and '{access.DisplayName}', which would both be configured as the DownstreamApis entry " +
                      $"'{entry.Name}'. An entry is named after a resource that references the API's registration, or after the display name of an API created with " +
                      $"EntraIdApi.FromApplicationId. Rename the resource, or give the API a different display name.");
            }
        }

        foreach (var consumer in Consumers)
        {
            ValidateConsumerReferences(consumer);
        }
    }

    // A resource can reference several registrations. Entries that different registrations write to the same environment variable
    // would overwrite each other's scopes one by one, and leave behind the higher indexes of the longer list, mixing the permissions
    // of two identities. The check compares the names that the consumer receives, because a non-.NET resource gets encoded, upper snake
    // case names: the prefixes "A"/"B-C" and "A_B"/"C", or the entries "orders-api" and "ordersApi", become the same variable. A .NET
    // program's DownstreamApis section is the same whatever the connection name, so references clash there most easily.
    internal static void ValidateConsumerReferences(IResource consumer)
    {
        var owners = new Dictionary<string, (EntraIdApplicationResource Source, string Entry, string Prefix)>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in consumer.Annotations.OfType<EntraIdReferenceAnnotation>())
        {
            var separator = reference.IsDotnetProgram ? "__" : "_";
            foreach (var entry in reference.Source.ApiAccess.SelectMany(static a => a.GetDownstreamApis()))
            {
                // Every variable of an entry starts with this name, so a clash of the first scope is a clash of the entry.
                var key = reference.GetEnvironmentVariableName($"{reference.EnvironmentVariablePrefix}{entry.Name}{separator}Scopes{separator}0");
                if (!owners.TryGetValue(key, out var other) || (ReferenceEquals(other.Source, reference.Source) && other.Entry == entry.Name))
                {
                    owners[key] = (reference.Source, entry.Name, reference.DownstreamApisPrefix);
                    continue;
                }

                throw new InvalidOperationException(other.Entry == entry.Name
                    ? $"'{consumer.Name}' references the Entra ID applications '{other.Source.Name}' and '{reference.Source.Name}', and both declare permissions to an API that is configured as " +
                      $"the DownstreamApis entry '{entry.Name}' under '{reference.DownstreamApisPrefix}', so their scopes would be mixed. Pass a different downstreamApisPrefix " +
                      $"to WithReference for one of them."
                    : $"'{consumer.Name}' would receive the DownstreamApis entries '{other.Entry}' (of '{other.Source.Name}') and '{entry.Name}' (of '{reference.Source.Name}') as the same " +
                      $"environment variable, '{key}', so their scopes would be mixed. Rename a resource that references the API, give an API created with " +
                      $"EntraIdApi.FromApplicationId a different display name, or pass a different downstreamApisPrefix to WithReference for one of the references.");
            }
        }
    }

    internal void ValidateComputeTargets()
    {
        foreach (var target in ClientCredentials.OfType<EntraIdFederatedIdentityCredential>().Select(c => c.ComputeTarget).OfType<IResource>())
        {
            if (!Consumers.Contains(target))
            {
                throw new InvalidOperationException(
                    $"Resource '{target.Name}' is trusted by Entra ID application '{Name}' but does not reference it, so it would not receive the configuration. " +
                    $"Call WithReference on '{target.Name}' with the Entra ID application.");
            }
        }
    }

    // Every compute resource whose identity is trusted must have one by now. The Azure resource preparer attaches it
    // for resources that need one, but only environments that support managed identities do.
    internal void ValidateManagedIdentities()
    {
        foreach (var credential in ClientCredentials.OfType<EntraIdFederatedIdentityCredential>().Where(c => c.IsResourceBacked))
        {
            IEnumerable<IResource> targets = credential.ComputeTarget is { } target ? [target] : Consumers;
            foreach (var resource in targets)
            {
                // A resource excluded from the manifest is not deployed, so the Azure resource preparer never gives it an identity.
                if (resource.IsExcludedFromPublish())
                {
                    continue;
                }

                credential.ValidateConsumer(this, resource);
            }
        }
    }

    /// <summary>
    /// Gets the distinct user-assigned identities that the registration trusts.
    /// </summary>
    /// <remarks>
    /// A compute resource's identity is attached by the Azure resource preparer, so this must be read after it has run.
    /// </remarks>
    internal IReadOnlyList<AzureUserAssignedIdentityResource> GetFederatedIdentities()
    {
        var identities = new List<AzureUserAssignedIdentityResource>();
        foreach (var credential in ClientCredentials.OfType<EntraIdFederatedIdentityCredential>())
        {
            if (credential.ManagedIdentity is { } explicitIdentity)
            {
                identities.Add(explicitIdentity);
            }
            else if (credential.ComputeTarget is { } target)
            {
                // Resolved after the Azure resource preparer has attached the target's identity. It is absent when running locally.
                if (!target.IsExcludedFromPublish() && EntraIdFederatedIdentityCredential.GetConsumerIdentity(target) is { } targetIdentity)
                {
                    identities.Add(targetIdentity);
                }
            }
            else if (credential.UsesConsumerIdentity)
            {
                identities.AddRange(Consumers.Where(c => !c.IsExcludedFromPublish()).Select(EntraIdFederatedIdentityCredential.GetConsumerIdentity).OfType<AzureUserAssignedIdentityResource>());
            }
        }

        return [.. identities.Distinct()];
    }

    private static BicepList<string> CreateRedirectUris(EntraIdApplicationResource resource, AzureResourceInfrastructure infrastructure)
    {
        var values = resource._redirectUriValues;
        if (values.All(static v => v is string))
        {
            return [.. values.OfType<string>()];
        }

        // Parameter values are only known at deployment time, so each is split on commas in Bicep. Consecutive
        // literals and endpoint-derived URIs are grouped into one array to keep the call order:
        //   concat(['https://literal', endpointRedirectUri0], split(redirectUris, ','), split(otherRedirectUris, ','))
        var arguments = new List<BicepExpression>();
        var literals = new List<BicepExpression>();

        // Derived URIs get generated parameter names. Reserve the names of every user-supplied parameter first, in any
        // call order, so a generated name never overwrites a parameter such as one named "redirectUri0".
        var reservedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in values.Concat(resource.ApplicationOwners).Concat(resource.ServicePrincipalOwners).OfType<ParameterResource>())
        {
            reservedNames.Add(Infrastructure.NormalizeBicepIdentifier(parameter.Name));
        }

        var endpointIndex = 0;
        string NextDerivedParameterName()
        {
            string name;
            do
            {
                name = $"redirectUri{endpointIndex++}";
            }
            while (reservedNames.Contains(name));

            return name;
        }

        foreach (var value in values)
        {
            if (value is string literal)
            {
                literals.Add(new StringLiteralExpression(literal));
                continue;
            }

            if (value is IEntraIdDerivedRedirectUri derivedRedirectUri)
            {
                // The value is only known when the parameter is resolved: after endpoint allocation when running
                // locally, from the compute environment when deploying, or from the host parameter's value.
                var uriParameter = derivedRedirectUri
                    .CreateExpression()
                    .AsProvisioningParameter(infrastructure, NextDerivedParameterName());
                literals.Add(uriParameter.Value.Compile());
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
        if (resource.ApplicationOwners.Count > 0)
        {
            application.Owners = CreateOwnerList(infrastructure, resource.ApplicationOwners, "applicationOwner");
        }
        if (resource.AppRoles.Count > 0)
        {
            application.AppRoles = [.. resource.AppRoles.Select(role => CreateAppRole(resource, role))];
        }

        resource.ValidateScopeReferences();

        // Names for the API access settings are allocated against everything that the template already declares.
        var symbolNames = new EntraIdSymbolNames(infrastructure);
        EntraIdApiProvisioning.ConfigureRequiredResourceAccess(resource, infrastructure, symbolNames, application);
        infrastructure.Add(application);

        var applicationIdUri = EntraIdApiProvisioning.ConfigureExposedApi(resource, infrastructure, symbolNames, application, registrationName);

        var servicePrincipal = new MicrosoftGraphServicePrincipal("servicePrincipal")
        {
            AppId = application.AppId,
            Tags = [EntraIdBicepConstants.IntegratedAppTag]
        };
        if (resource.ServicePrincipalOwners.Count > 0)
        {
            servicePrincipal.Owners = CreateOwnerList(infrastructure, resource.ServicePrincipalOwners, "servicePrincipalOwner");
        }
        infrastructure.Add(servicePrincipal);

        EntraIdApiProvisioning.ConfigureGrants(resource, infrastructure, symbolNames, servicePrincipal);

        // A symbol name must not collide with another symbol in the template, such as a user-supplied redirect URI
        // parameter named "web-identity-principalId" and an identity named "web-identity". Parameters and resources share
        // one namespace, and the ARM deployment engine is not case sensitive, so Bicep rejects parameters that differ only
        // in casing (BCP353); one case-insensitive set covers both. Only this generation's symbols count:
        // resource.Parameters keeps the names from earlier generations, and reserving those would change the names
        // every time the template is generated.
        var usedSymbolNames = new HashSet<string>(
            infrastructure.GetProvisionableResources().OfType<ProvisioningParameter>().Select(p => p.BicepIdentifier)
                .Concat(infrastructure.GetProvisionableResources().OfType<ProvisionableResource>().Select(r => r.BicepIdentifier)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var identity in resource.GetFederatedIdentities())
        {
            // The subject is the identity's object (principal) ID; the client ID is what consumers pass to Microsoft.Identity.Web.
            var identifier = Infrastructure.NormalizeBicepIdentifier(identity.Name);
            var parameterName = AllocateSymbolName(usedSymbolNames, $"{identifier}_principalId");
            var principalId = identity.PrincipalId.AsProvisioningParameter(infrastructure, parameterName);
            var credentialIdentifier = AllocateSymbolName(usedSymbolNames, $"federatedCredential_{identifier}");

            // Graph requires a federated credential's name to be 3-120 characters. Aspire resource names can be 1-64
            // characters, so a fixed prefix keeps a short name above the minimum and, unlike one that also includes the
            // registration's name, a long name below the maximum. The identity's name is unique in the model.
            var credentialName = $"aspire-{identity.Name}";
            // The Graph extension names a child as '{applicationUniqueName}/{credentialName}'. Referencing the
            // application's property, rather than reusing its name expression, makes the child depend on the application.
            BicepValue<string> applicationUniqueName = application.UniqueName.ToBicepExpression();
            infrastructure.Add(new FederatedIdentityCredential(credentialIdentifier)
            {
                Name = BicepFunction.Interpolate($"{applicationUniqueName}/{credentialName}").Compile(),
                Issuer = BicepFunction.Interpolate($"{EntraIdFederation.GetIssuerPrefix(resource.Instance)}/{BicepFunction.GetTenant().TenantId}/v2.0").Compile(),
                Subject = principalId,
                Audiences = [EntraIdFederation.GetTokenExchangeAudience(resource.Instance)!]
            });
        }

        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.ClientIdOutputName, typeof(string)) { Value = application.AppId.ToBicepExpression() });
        // Compile the scope-function property directly; ToBicepExpression would reference an undeclared "tenant" resource.
        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.TenantIdOutputName, typeof(string)) { Value = BicepFunction.GetTenant().TenantId.Compile() });
        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.ObjectIdOutputName, typeof(string)) { Value = application.Id.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.ServicePrincipalIdOutputName, typeof(string)) { Value = servicePrincipal.Id.ToBicepExpression() });
        infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.UniqueNameOutputName, typeof(string)) { Value = application.UniqueName.ToBicepExpression() });
        if (applicationIdUri is not null)
        {
            infrastructure.Add(new ProvisioningOutput(EntraIdBicepConstants.ApplicationIdUriOutputName, typeof(string)) { Value = applicationIdUri });
        }
    }

    private static string AllocateSymbolName(HashSet<string> usedSymbolNames, string name)
    {
        var candidate = name;
        for (var suffix = 2; !usedSymbolNames.Add(candidate); suffix++)
        {
            candidate = $"{name}_{suffix}";
        }

        return candidate;
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

    internal void AddRedirectUri(IEntraIdDerivedRedirectUri redirectUri) => _redirectUriValues.Add(redirectUri);

    /// <summary>
    /// Gets the application roles to declare on an application created by Aspire.
    /// </summary>
    /// <remarks>
    /// Application roles are distinct from delegated scopes and from Azure RBAC roles. Declaring a role does not
    /// assign it to any user, group, or application, and existing registrations are not modified.
    /// </remarks>
    public IReadOnlyList<EntraIdAppRole> AppRoles => _appRoles.AsReadOnly();

    private readonly List<EntraIdAppRole> _appRoles = [];

    internal void AddAppRole(EntraIdAppRole role) => _appRoles.Add(role);

    // Each entry is either a literal directory object ID (string) or a ParameterResource holding one.
    internal List<object> ApplicationOwners { get; } = [];

    internal List<object> ServicePrincipalOwners { get; } = [];

    private static MicrosoftGraphAppRole CreateAppRole(EntraIdApplicationResource resource, EntraIdAppRole role)
    {
        var memberTypes = new BicepList<string>();
        foreach (var memberType in role.AllowedMemberTypes)
        {
            memberTypes.Add(memberType);
        }

        return new MicrosoftGraphAppRole
        {
            Id = role.GetStableId(resource.Name).ToString("D"),
            DisplayName = role.DisplayName,
            Description = role.Description,
            Value = role.Value,
            IsEnabled = role.IsEnabled,
            AllowedMemberTypes = memberTypes
        };
    }

    private static BicepList<string> CreateOwnerList(AzureResourceInfrastructure infrastructure, IEnumerable<object> owners, string prefix)
    {
        var list = new BicepList<string>();
        var index = 0;
        foreach (var owner in owners)
        {
            // Only parameters need a Bicep identifier, so number just those.
            BicepValue<string> value = owner switch
            {
                ParameterResource parameter => parameter.AsProvisioningParameter(infrastructure, $"{prefix}{index++}"),
                string id => id,
                _ => throw new InvalidOperationException("Unsupported owner type.")
            };
            list.Add(value);
        }
        return list;
    }

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

    // ── Exposed API ────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the application ID URI that identifies this registration as an API, when one was set explicitly.
    /// </summary>
    /// <remarks>
    /// This is <see langword="null"/> when the registration uses the default <c>api://{clientId}</c> URI, which is known only after
    /// the registration is created. Use <see cref="GetScopeUri(string)"/> to refer to a scope's full URI in either case.
    /// </remarks>
    public string? ApplicationIdUri { get; internal set; }

    internal bool UsesDefaultApplicationIdUri { get; set; }

    /// <summary>
    /// Gets the delegated permission scopes that this registration exposes.
    /// </summary>
    public IReadOnlyList<EntraIdScope> Scopes => _scopes;

    private readonly List<EntraIdScope> _scopes = [];

    internal List<EntraIdPreAuthorizedClient> PreAuthorizedClients { get; } = [];

    internal List<EntraIdApiAccess> ApiAccess { get; } = [];

    internal bool HasApplicationIdUri => ApplicationIdUri is not null || UsesDefaultApplicationIdUri || _scopes.Count > 0;

    /// <summary>
    /// Gets the application ID URI produced when the registration is provisioned with an application ID URI.
    /// </summary>
    public BicepOutputReference ApplicationIdUriOutputReference => new(EntraIdBicepConstants.ApplicationIdUriOutputName, this);

    internal BicepOutputReference ServicePrincipalIdOutputReference => new(EntraIdBicepConstants.ServicePrincipalIdOutputName, this);

    internal object? ApplicationIdUriValue => ApplicationIdUri is { } uri
        ? uri
        : IsExistingApplication
            ? null
            : ApplicationIdUriOutputReference;

    /// <summary>
    /// Gets an expression for the full URI of one of this registration's scopes, such as <c>api://{clientId}/access_as_user</c>.
    /// </summary>
    /// <param name="scopeName">The name of a scope added with <c>WithScope</c>.</param>
    /// <returns>An expression that resolves when the registration's application ID URI is known.</returns>
    /// <exception cref="ArgumentException">The registration does not expose a scope with that name.</exception>
    /// <exception cref="InvalidOperationException">The registration is an existing registration without an application ID URI.</exception>
    /// <remarks>
    /// Clients request the full URI as the token scope, for example in Microsoft.Identity.Web's <c>DownstreamApis</c> configuration.
    /// </remarks>
    public ReferenceExpression GetScopeUri(string scopeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(scopeName);

        if (!_scopes.Any(s => string.Equals(s.Name, scopeName, StringComparison.Ordinal)))
        {
            throw new ArgumentException($"'{Name}' does not expose a scope named '{scopeName}'. Add it with WithScope first.", nameof(scopeName));
        }

        return CreateScopeValue(scopeName);
    }

    internal void AddScope(EntraIdScope scope) => _scopes.Add(scope);

    // The scope's full name, as a client requests it in a token request. An existing registration can use any application ID URI,
    // so one that isn't configured can't be guessed: a made-up URI would only fail later, as a token error.
    internal ReferenceExpression CreateScopeValue(string scope) => ApplicationIdUriValue switch
    {
        string uri => ReferenceExpression.Create($"{EntraIdApi.CreateScope(uri, scope)}"),
        BicepOutputReference output => ReferenceExpression.Create($"{output}/{scope}"),
        _ => throw CreateMissingApplicationIdUriException()
    };

    internal InvalidOperationException CreateMissingApplicationIdUriException() =>
        new($"The application ID URI of '{Name}' is not configured. Call WithApplicationIdUri with the URI that the registration defines.");

    // Scope IDs must not change between deployments, and Graph rejects a request that drops an enabled scope, so a
    // random ID is not an option. Deriving the ID from the registration and scope names also gives every tenant the same IDs.
    internal Guid GetScopeId(EntraIdScope scope)
    {
        if (scope.Id is { } id)
        {
            return id;
        }

        if (IsExistingApplication)
        {
            throw new InvalidOperationException(
                $"The ID of scope '{scope.Name}' of the existing registration '{Name}' is unknown. Set EntraIdScope.Id to the ID that the registration defines.");
        }

        Span<byte> hash = stackalloc byte[16];
        XxHash128.Hash(Encoding.UTF8.GetBytes($"{Name}/{scope.Name}"), hash);

        // Mark the GUID as RFC 9562 version 8 (custom) with the standard variant.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash, bigEndian: true);
    }

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
    /// <param name="application">The Entra ID application that owns the credential.</param>
    /// <param name="consumer">The resource that receives the configuration.</param>
    /// <param name="executionContext">The execution context that the configuration is for.</param>
    /// <returns><see langword="true"/> when the credential was emitted, or <see langword="false"/> when it does not apply to the consumer.</returns>
    internal virtual bool EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator, EntraIdApplicationResource application, IResource consumer, DistributedApplicationExecutionContext executionContext)
    {
        envVars[$"{prefix}{separator}SourceType"] = SourceType;
        return true;
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
    internal override bool EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator, EntraIdApplicationResource application, IResource consumer, DistributedApplicationExecutionContext executionContext)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator, application, consumer, executionContext);
        envVars[$"{prefix}{separator}ClientSecret"] = ClientSecret;

        return true;
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
    /// <summary>
    /// Initializes a new instance of the <see cref="EntraIdFederatedIdentityCredential"/> class for a managed identity
    /// that Aspire does not manage.
    /// </summary>
    /// <param name="managedIdentityClientId">The client ID of the user-assigned managed identity that supplies the signed assertion.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="managedIdentityClientId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="managedIdentityClientId"/> is empty or is not a GUID.</exception>
    public EntraIdFederatedIdentityCredential(string managedIdentityClientId)
    {
        ArgumentNullException.ThrowIfNull(managedIdentityClientId);

        if (EntraIdValidation.ValidateManagedIdentityClientId(managedIdentityClientId) is { } error)
        {
            throw new ArgumentException(error, nameof(managedIdentityClientId));
        }

        ManagedIdentityClientId = managedIdentityClientId;
    }

    // Aspire provisions the trust for the managed identity of each referencing resource.
    internal EntraIdFederatedIdentityCredential()
    {
    }

    /// <inheritdoc />
    public override string SourceType => "SignedAssertionFromManagedIdentity";

    /// <summary>
    /// Gets the client ID of the user-assigned managed identity that supplies the signed assertion.
    /// </summary>
    /// <remarks>
    /// This is the identity's client ID, not the object (principal) ID used as the federated credential's subject.
    /// Microsoft Entra's managed-identity federation scenario supports user-assigned identities only.
    /// It is <see langword="null"/> when Aspire resolves the identity, in which case each consuming resource uses its own
    /// user-assigned managed identity or the one it was configured with.
    /// </remarks>
    public string? ManagedIdentityClientId { get; }

    /// <summary>
    /// Gets or sets the token exchange URL for federated identity credential scenarios.
    /// </summary>
    /// <remarks>
    /// If not specified, defaults to <c>api://AzureADTokenExchange</c>, or the audience of the application's cloud
    /// when the credential is backed by a managed identity resource.
    /// </remarks>
    public string? TokenExchangeUrl { get; set; }

    /// <summary>
    /// Gets or sets the token exchange authority URL.
    /// </summary>
    /// <remarks>
    /// Used when the issuer for token exchange differs from the application's authority.
    /// </remarks>
    public string? TokenExchangeAuthority { get; set; }

    // The identity's principal ID is the federated credential's subject, and its client ID is what consumers receive.
    internal AzureUserAssignedIdentityResource? ManagedIdentity { get; set; }

    // When set, the credential trusts and applies to only this compute resource, using the identity it has when published.
    internal IResource? ComputeTarget { get; set; }

    // No client ID, identity resource or compute resource was supplied, so each consumer's own identity is used.
    internal bool UsesConsumerIdentity => ManagedIdentityClientId is null && ManagedIdentity is null && ComputeTarget is null;

    // The credential trusts an Aspire-modeled identity, so Aspire can provision the trust.
    internal bool IsResourceBacked => ManagedIdentity is not null || ComputeTarget is not null || UsesConsumerIdentity;

    internal static AzureUserAssignedIdentityResource? GetConsumerIdentity(IResource consumer)
        => consumer.TryGetLastAnnotation<AppIdentityAnnotation>(out var annotation)
            ? annotation.IdentityResource as AzureUserAssignedIdentityResource
            : null;

    internal void ValidateConsumer(EntraIdApplicationResource application, IResource consumer)
    {
        var consumerIdentity = GetConsumerIdentity(consumer)
            ?? throw new InvalidOperationException(
                $"Resource '{consumer.Name}' references Entra ID application '{application.Name}', which uses a managed identity credential, " +
                "but the resource has no user-assigned managed identity. Use a compute environment that supports managed identities, such as AddAzureContainerAppEnvironment, or call WithAzureUserAssignedIdentity.");

        if (ManagedIdentity is not null && !ReferenceEquals(consumerIdentity, ManagedIdentity))
        {
            throw new InvalidOperationException(
                $"Resource '{consumer.Name}' references Entra ID application '{application.Name}', which trusts managed identity '{ManagedIdentity.Name}', " +
                $"but the resource does not use that identity. Call WithAzureUserAssignedIdentity on '{consumer.Name}' with that identity.");
        }
    }

    private sealed class ManagedIdentityClientIdValue(
        EntraIdFederatedIdentityCredential credential,
        EntraIdApplicationResource application,
        IResource consumer) : IValueProvider, IManifestExpressionProvider, IValueWithReferences, IBicepOutputReferenceProvider
    {
        private BicepOutputReference ClientId
        {
            get
            {
                credential.ValidateConsumer(application, consumer);
                return (credential.ManagedIdentity ?? GetConsumerIdentity(consumer))!.ClientId;
            }
        }

        public string ValueExpression => ClientId.ValueExpression;

        // While the preparer is discovering dependencies the identity does not exist yet, so there is nothing to reference.
        public BicepOutputReference? GetOutputReference()
            => credential.ManagedIdentity is not null || GetConsumerIdentity(consumer) is not null ? ClientId : null;

        public IEnumerable<object> References => GetOutputReference() is { } output ? [output] : [];

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
            => ((IValueProvider)ClientId).GetValueAsync(cancellationToken);
    }

    /// <inheritdoc />
    internal override bool EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator, EntraIdApplicationResource application, IResource consumer, DistributedApplicationExecutionContext executionContext)
    {
        if (ComputeTarget is not null && !ReferenceEquals(ComputeTarget, consumer))
        {
            return false;
        }

        // Managed identities exist only in Azure. Locally the credential has nothing to assert, so a resource-backed
        // credential is skipped and another credential, such as a client secret, is used.
        if (IsResourceBacked && executionContext.IsRunMode)
        {
            return false;
        }

        base.EmitEnvironmentVariables(envVars, prefix, separator, application, consumer, executionContext);

        var tokenExchangeUrl = TokenExchangeUrl;
        if (IsResourceBacked)
        {
            // An identity passed in is known now, so its output is used as is. The validation step that runs after the
            // Azure resource preparer reports a consumer that does not use it.
            if (ManagedIdentity is { } identity)
            {
                envVars[$"{prefix}{separator}ManagedIdentityClientId"] = identity.ClientId;
            }
            else
            {
                // A consumer's own identity does not exist yet: an environment callback runs once and its result is cached,
                // and the Azure resource preparer runs it to discover dependencies before it attaches the identity. A
                // ReferenceExpression takes its providers when it is created, so this looks the identity up when read.
                envVars[$"{prefix}{separator}ManagedIdentityClientId"] = new ManagedIdentityClientIdValue(this, application, consumer);
            }

            // Sovereign clouds use their own token exchange audience, which Microsoft.Identity.Web reads from TokenExchangeUrl.
            if (tokenExchangeUrl is null &&
                EntraIdFederation.GetTokenExchangeAudience(application.Instance) is { } audience &&
                audience != EntraIdFederation.DefaultTokenExchangeAudience)
            {
                tokenExchangeUrl = audience;
            }
        }
        else
        {
            envVars[$"{prefix}{separator}ManagedIdentityClientId"] = ManagedIdentityClientId
                ?? throw new InvalidOperationException("A managed identity client ID is required.");
        }

        if (tokenExchangeUrl is not null)
        {
            envVars[$"{prefix}{separator}TokenExchangeUrl"] = tokenExchangeUrl;
        }

        if (TokenExchangeAuthority is not null)
        {
            envVars[$"{prefix}{separator}TokenExchangeAuthority"] = TokenExchangeAuthority;
        }

        return true;
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
    internal override bool EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator, EntraIdApplicationResource application, IResource consumer, DistributedApplicationExecutionContext executionContext)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator, application, consumer, executionContext);
        envVars[$"{prefix}{separator}KeyVaultUrl"] = KeyVaultUrl;
        envVars[$"{prefix}{separator}KeyVaultCertificateName"] = CertificateNameInKeyVault;

        return true;
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
    internal override bool EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator, EntraIdApplicationResource application, IResource consumer, DistributedApplicationExecutionContext executionContext)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator, application, consumer, executionContext);
        envVars[$"{prefix}{separator}CertificateStorePath"] = StorePath;

        if (Thumbprint is not null)
        {
            envVars[$"{prefix}{separator}CertificateThumbprint"] = Thumbprint;
        }
        else if (DistinguishedName is not null)
        {
            envVars[$"{prefix}{separator}CertificateDistinguishedName"] = DistinguishedName;
        }

        return true;
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
    internal override bool EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator, EntraIdApplicationResource application, IResource consumer, DistributedApplicationExecutionContext executionContext)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator, application, consumer, executionContext);
        envVars[$"{prefix}{separator}CertificateDiskPath"] = FilePath;

        if (Password is not null)
        {
            envVars[$"{prefix}{separator}CertificatePassword"] = Password;
        }

        return true;
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
    internal override bool EmitEnvironmentVariables(IDictionary<string, object> envVars, string prefix, string separator, EntraIdApplicationResource application, IResource consumer, DistributedApplicationExecutionContext executionContext)
    {
        base.EmitEnvironmentVariables(envVars, prefix, separator, application, consumer, executionContext);

        if (FilePath is not null)
        {
            envVars[$"{prefix}{separator}SignedAssertionFileDiskPath"] = FilePath;
        }

        return true;
    }
}
