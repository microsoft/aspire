// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.EntraId.Provisioning;

/// <summary>
/// Allocates the Bicep symbol names (parameters and resources) that the API access settings add to a registration's template.
/// </summary>
/// <remarks>
/// <para>
/// Names come from the identity of the API, never from text alone: a registration in the AppHost by its resource name, which Aspire
/// requires to be unique, and another API by its application ID. Display text can't identify an API, because <c>orders-api</c> and
/// <c>ordersApi</c>, or two custom APIs that share a display name, would otherwise produce the same symbol, and a parameter that
/// is bound twice silently keeps only the last binding.
/// </para>
/// <para>
/// A name that is already taken, by this allocator or by anything else in the template, gets a numeric suffix. Names are allocated
/// in model order, so a given model produces the same names every time. The same identity and purpose always return the same name.
/// </para>
/// </remarks>
internal sealed partial class EntraIdSymbolNames(AzureResourceInfrastructure infrastructure)
{
    // The symbols that the registration's own template always declares, which are added after the API access settings are written.
    private static readonly HashSet<string> s_fixedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "application", "servicePrincipal", "tenantResourceSuffix"
    };

    private readonly HashSet<string> _allocated = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(object Identity, string Purpose), string> _names = [];

    /// <summary>Gets the symbol of the second declaration of a registration, which sets the default application ID URI.</summary>
    public string ApplicationIdentity(EntraIdApplicationResource application) => Get(application, "ApplicationIdentity", "applicationIdentity");

    /// <summary>Gets the parameter that carries the application (client) ID of a registration in the AppHost.</summary>
    public string ClientId(EntraIdApplicationResource application) => Get(application, "ClientId", $"{Base(application)}ClientId");

    /// <summary>Gets the parameter that carries the service principal's object ID of an API registration created by Aspire.</summary>
    public string ServicePrincipalId(EntraIdApiAccess access) => Get(access, "ServicePrincipalId", $"{Base(access)}ServicePrincipalId");

    /// <summary>Gets the symbol of the existing service principal that an API is looked up by.</summary>
    public string ExistingServicePrincipal(EntraIdApiAccess access) => Get(access, "ServicePrincipal", $"{Base(access)}ServicePrincipal");

    /// <summary>Gets the symbol of the delegated permission grant to an API.</summary>
    public string DelegatedGrant(EntraIdApiAccess access) => Get(access, "DelegatedGrant", $"{Base(access)}DelegatedGrant");

    /// <summary>Gets the symbol of the assignment of one application permission of an API.</summary>
    public string RoleAssignment(EntraIdApiAccess access, EntraIdResolvedPermission role) =>
        Get(access, $"RoleAssignment:{role.Id:N}", $"{Base(access)}{ToPascalCase(role.Name)}RoleAssignment");

    // A registration in the AppHost has a unique resource name; Aspire's normalization maps '-' to '_' and resource names can't contain
    // '_', so two names never normalize to the same text. Another API is named after its display text, and made unique by allocation.
    private static string Base(EntraIdApplicationResource application) => StartWithLetter(Infrastructure.NormalizeBicepIdentifier(application.Name));

    private static string Base(EntraIdApiAccess access) => access.Application is { } application
        ? Base(application)
        : StartWithLetter(ToCamelCase(access.Api!.DisplayName));

    // A Bicep identifier can't start with a digit, and an API whose display text has no letters or digits would have no name at all.
    private static string StartWithLetter(string name) => name.Length > 0 && char.IsAsciiLetter(name[0]) ? name : $"api{name}";

    private string Get(object owner, string purpose, string name)
    {
        // Names are cached by the API's identity rather than by the access entry, so the same API is always given the same parameter.
        var identity = owner is EntraIdApiAccess access ? GetIdentity(access) : owner;
        if (_names.TryGetValue((identity, purpose), out var existing))
        {
            return existing;
        }

        var candidate = name;
        for (var suffix = 2; IsTaken(candidate); suffix++)
        {
            candidate = $"{name}_{suffix}";
        }

        _allocated.Add(candidate);
        _names[(identity, purpose)] = candidate;
        return candidate;
    }

    private static object GetIdentity(EntraIdApiAccess access) =>
        access.Application is { } application ? application : access.Api!.ApplicationId.ToLowerInvariant();

    // Bicep treats parameters and resources as one namespace, and the deployment engine isn't case sensitive, so one
    // case-insensitive check covers everything that is already in the template and the names that other code adds afterwards.
    private bool IsTaken(string name) =>
        _allocated.Contains(name) ||
        s_fixedNames.Contains(name) ||
        FixedParameterPattern().IsMatch(name) ||
        infrastructure.GetProvisionableResources().Any(r =>
            (r is ProvisioningParameter p && string.Equals(p.BicepIdentifier, name, StringComparison.OrdinalIgnoreCase)) ||
            (r is ProvisionableResource pr && string.Equals(pr.BicepIdentifier, name, StringComparison.OrdinalIgnoreCase)));

    // Builds a camelCase name from text such as "Microsoft Graph" or "User.Read.All".
    private static string ToCamelCase(string text)
    {
        var words = NonAlphanumeric().Split(text).Where(static word => word.Length > 0);
        return string.Concat(words.Select(static (word, index) =>
            (index == 0 ? char.ToLowerInvariant(word[0]) : char.ToUpperInvariant(word[0])) + word[1..]));
    }

    // The part of a name that follows another, such as the role in "{api}{Role}RoleAssignment".
    private static string ToPascalCase(string text)
    {
        var name = ToCamelCase(text);
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : name;
    }

    // Owner and redirect URI parameters are named by their position and added to the template without a check.
    [GeneratedRegex("^(applicationOwner|servicePrincipalOwner|redirectUri)[0-9]+$", RegexOptions.IgnoreCase)]
    private static partial Regex FixedParameterPattern();

    [GeneratedRegex("[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
