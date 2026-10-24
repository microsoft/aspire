// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.Expressions;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Writes the Microsoft Graph resources that expose an application as an API and that give it access to other APIs.
/// </summary>
internal static partial class EntraIdApiProvisioning
{
    private const string DelegatedAccessType = "Scope";
    private const string ApplicationAccessType = "Role";

    /// <summary>
    /// Declares the permissions that the application requires from other APIs (<c>requiredResourceAccess</c>).
    /// </summary>
    /// <remarks>
    /// Declaring a permission only lists it under the application's API permissions. It grants nothing, and nobody is
    /// asked to consent to it until a grant (<see cref="ConfigureGrants"/>) or an interactive consent happens.
    /// </remarks>
    public static void ConfigureRequiredResourceAccess(
        EntraIdApplicationResource resource,
        AzureResourceInfrastructure infrastructure,
        MicrosoftGraphApplication application)
    {
        foreach (var access in resource.ApiAccess.Where(a => a.Declared.Count > 0))
        {
            var required = new MicrosoftGraphRequiredResourceAccess
            {
                ResourceAppId = GetApiApplicationId(access, infrastructure)
            };

            foreach (var permission in access.Declared)
            {
                required.ResourceAccess.Add(new MicrosoftGraphResourceAccess
                {
                    Id = permission.Id.ToString("D"),
                    Type = permission.Type == EntraIdPermissionType.Delegated ? DelegatedAccessType : ApplicationAccessType
                });
            }

            application.RequiredResourceAccess.Add(required);
        }
    }

    /// <summary>
    /// Adds the application ID URI, the exposed scopes and the pre-authorized clients, and returns the application ID URI output value.
    /// </summary>
    public static BicepValue<string>? ConfigureExposedApi(
        EntraIdApplicationResource resource,
        AzureResourceInfrastructure infrastructure,
        MicrosoftGraphApplication application,
        BicepValue<string> registrationName)
    {
        if (!resource.HasApplicationIdUri)
        {
            return null;
        }

        MicrosoftGraphApplication target = application;
        BicepValue<string> identifierUri;
        if (resource.ApplicationIdUri is { } customUri)
        {
            identifierUri = customUri;
        }
        else
        {
            // The default URI contains the client ID, which exists only after the application is created, and Graph
            // cannot read a property of the resource that is being created. Declaring the application a second time under the same
            // unique name updates the created application in place (PATCH semantics) with a value that refers to the first
            // declaration's appId, so no tenant policy needs to allow a name-based URI.
            target = new MicrosoftGraphApplication("applicationIdentity")
            {
                DisplayName = registrationName,
                UniqueName = registrationName
            };
            identifierUri = BicepFunction.Interpolate($"api://{application.AppId}").Compile();
        }

        target.IdentifierUris = [identifierUri];

        foreach (var scope in resource.Scopes)
        {
            var description = scope.Description;
            target.PermissionScopes.Add(new MicrosoftGraphPermissionScope
            {
                Id = resource.GetScopeId(scope).ToString("D"),
                Value = scope.Name,
                Type = scope.RequiresAdminConsent ? "Admin" : "User",
                IsEnabled = scope.IsEnabled,
                AdminConsentDisplayName = scope.DisplayName,
                AdminConsentDescription = description,
                UserConsentDisplayName = scope.UserConsentDisplayName ?? scope.DisplayName,
                UserConsentDescription = scope.UserConsentDescription ?? description
            });
        }

        foreach (var preAuthorized in resource.PreAuthorizedClients)
        {
            var preAuthorization = new MicrosoftGraphPreAuthorizedApplication
            {
                AppId = GetClientId(preAuthorized.Client, infrastructure, $"{ToIdentifier(preAuthorized.Client.Name)}ClientId")
            };

            foreach (var scope in preAuthorized.Scopes)
            {
                preAuthorization.DelegatedPermissionIds.Add(resource.GetScopeId(scope).ToString("D"));
            }

            target.PreAuthorizedApplications.Add(preAuthorization);
        }

        if (!ReferenceEquals(target, application))
        {
            infrastructure.Add(target);
        }

        return identifierUri;
    }

    /// <summary>
    /// Writes the consents that the application's creator asked for: tenant-wide delegated grants and application permission assignments.
    /// </summary>
    public static void ConfigureGrants(
        EntraIdApplicationResource resource,
        AzureResourceInfrastructure infrastructure,
        MicrosoftGraphServicePrincipal servicePrincipal)
    {
        foreach (var access in resource.ApiAccess.Where(a => a.Consented.Count > 0))
        {
            var key = ToIdentifier(access.DisplayName);
            var apiServicePrincipalId = GetApiServicePrincipalId(access, infrastructure, key);

            var delegated = access.Consented.Where(p => p.Type == EntraIdPermissionType.Delegated).ToArray();
            if (delegated.Length > 0)
            {
                // Graph stores the granted scopes as one space-separated string of scope names.
                infrastructure.Add(new MicrosoftGraphOAuth2PermissionGrant($"{key}DelegatedGrant")
                {
                    ClientId = servicePrincipal.Id,
                    ConsentType = "AllPrincipals",
                    ResourceId = apiServicePrincipalId,
                    Scope = string.Join(' ', delegated.Select(p => p.Name))
                });
            }

            foreach (var role in access.Consented.Where(p => p.Type == EntraIdPermissionType.Application))
            {
                infrastructure.Add(new MicrosoftGraphAppRoleAssignedTo(ToIdentifier(key, role.Name, "RoleAssignment"))
                {
                    AppRoleId = role.Id.ToString("D"),
                    PrincipalId = servicePrincipal.Id,
                    ResourceId = apiServicePrincipalId
                });
            }
        }
    }

    private static BicepValue<string> GetApiApplicationId(EntraIdApiAccess access, AzureResourceInfrastructure infrastructure) => access switch
    {
        { Api: { } api } => api.ApplicationId,
        { Application: { } application } => GetClientId(application, infrastructure, $"{ToIdentifier(application.Name)}ClientId"),
        _ => throw new InvalidOperationException("The API is not configured.")
    };

    private static BicepValue<string> GetApiServicePrincipalId(EntraIdApiAccess access, AzureResourceInfrastructure infrastructure, string key)
    {
        // The service principal of an API created by Aspire is a deployment output, which is available as soon as the API is deployed.
        if (access.Application is { IsExistingApplication: false } created)
        {
            return created.ServicePrincipalIdOutputReference.AsProvisioningParameter(infrastructure, $"{key}ServicePrincipalId");
        }

        // First-party APIs and registrations that already exist have a service principal in the tenant that is found by the application ID.
        var existing = MicrosoftGraphServicePrincipal.FromExisting($"{key}ServicePrincipal");
        existing.Name = GetApiApplicationId(access, infrastructure);
        infrastructure.Add(existing);
        return existing.Id;
    }

    private static BicepValue<string> GetClientId(EntraIdApplicationResource application, AzureResourceInfrastructure infrastructure, string parameterName) => application.ClientIdValue switch
    {
        BicepOutputReference output => output.AsProvisioningParameter(infrastructure, parameterName),
        ParameterResource parameter => parameter.AsProvisioningParameter(infrastructure, parameterName),
        string clientId => clientId,
        _ => throw new InvalidOperationException($"The client ID of '{application.Name}' is not configured. Call AddExistingEntraApplication with the registration's tenant ID and client ID.")
    };

    // Builds a camelCase Bicep identifier from names such as "Microsoft Graph", "entra-api" or "User.Read.All".
    private static string ToIdentifier(params string[] names)
    {
        var words = names
            .SelectMany(static name => NonAlphanumeric().Split(name))
            .Where(static word => word.Length > 0);
        return string.Concat(words.Select(static (word, index) =>
            (index == 0 ? char.ToLowerInvariant(word[0]) : char.ToUpperInvariant(word[0])) + word[1..]));
    }

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}