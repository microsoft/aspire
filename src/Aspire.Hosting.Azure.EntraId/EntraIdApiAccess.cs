// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A client application that an API registration pre-authorizes for some of its scopes.
/// </summary>
internal sealed record EntraIdPreAuthorizedClient(EntraIdApplicationResource Client, IReadOnlyList<EntraIdScope> Scopes);

/// <summary>
/// The permissions that an application declares, and optionally has consented, for one API.
/// </summary>
/// <remarks>
/// The API is either an application registration modeled in the AppHost or a well-known API such as Microsoft Graph.
/// Declaring a permission only lists it in the application's required permissions. Consent is tracked separately,
/// because it is what lets the application use the permission without prompting anyone.
/// </remarks>
internal sealed class EntraIdApiAccess
{
    private EntraIdApiAccess(EntraIdApplicationResource? application, EntraIdApi? api)
    {
        Application = application;
        Api = api;
    }

    public static EntraIdApiAccess ForApplication(EntraIdApplicationResource application) => new(application, null);

    public static EntraIdApiAccess ForApi(EntraIdApi api) => new(null, api);

    public EntraIdApplicationResource? Application { get; }

    public EntraIdApi? Api { get; }

    public List<EntraIdResolvedPermission> Declared { get; } = [];

    public List<EntraIdResolvedPermission> Consented { get; } = [];

    public string DisplayName => Application?.Name ?? Api!.DisplayName;

    public bool IsFor(EntraIdApplicationResource application) => ReferenceEquals(Application, application);

    public bool IsFor(EntraIdApi api) => Api is not null && string.Equals(Api.ApplicationId, api.ApplicationId, StringComparison.OrdinalIgnoreCase);

    // The names of the API's entries under Microsoft.Identity.Web's DownstreamApis section. Each is bound as one configuration key.
    // For an API registration in the AppHost these are the resources that reference it, as with service discovery, because
    // those are the resources that the caller sends requests to and that name their API in code, such as the host of
    // an HttpClient's BaseAddress. They are read when the environment is resolved, so references added later are included.
    private IEnumerable<string> GetConfigurationNames()
    {
        if (Application is { } application)
        {
            // A registration that nothing references has no resource to name the entry after.
            return application.Consumers.Count > 0
                ? application.Consumers.Select(static c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                : [EnvironmentVariableNameEncoder.EncodeConnectionStringName(application.Name)];
        }

        return [new string([.. Api!.DisplayName.Where(char.IsAsciiLetterOrDigit)]) is { Length: > 0 } displayName
            ? displayName
            : EnvironmentVariableNameEncoder.EncodeConnectionStringName(Api.ApplicationId)];
    }

    /// <summary>
    /// Describes the token requests that the caller makes for this API, as Microsoft.Identity.Web's <c>DownstreamApis</c> entries.
    /// </summary>
    /// <remarks>
    /// A token request covers one resource, and the delegated and application flows differ, so they are separate entries:
    /// delegated permissions are the scopes of <c>{name}</c> (acting for a signed-in user), and application permissions
    /// are the <c>.default</c> scope of <c>{name}AppOnly</c> (acting as the application itself).
    /// </remarks>
    public IEnumerable<EntraIdDownstreamApi> GetDownstreamApis()
    {
        var delegatedScopes = Declared
            .Where(p => p.Type == EntraIdPermissionType.Delegated && !(Api?.IsOpenIdScope(p.Name) ?? false))
            .Select(p => CreateScopeValue(p.Name))
            .ToArray();
        var hasApplicationPermissions = Declared.Any(p => p.Type == EntraIdPermissionType.Application);

        foreach (var name in GetConfigurationNames())
        {
            if (delegatedScopes.Length > 0)
            {
                yield return new EntraIdDownstreamApi(name, Api?.BaseUrl, RequestAppToken: false, delegatedScopes);
            }

            if (hasApplicationPermissions)
            {
                yield return new EntraIdDownstreamApi($"{name}AppOnly", Api?.BaseUrl, RequestAppToken: true, [CreateScopeValue(".default")]);
            }
        }
    }

    private object CreateScopeValue(string scope) => Application is { } application
        ? application.CreateScopeValue(scope)
        : EntraIdApi.CreateScope(Api!.ResourceUri, scope);
}

/// <summary>
/// One entry of Microsoft.Identity.Web's <c>DownstreamApis</c> section that Aspire configures for a caller.
/// </summary>
/// <param name="Name">The entry's name.</param>
/// <param name="BaseUrl">The API's root URL when it is the same for every caller; otherwise <see langword="null"/>.</param>
/// <param name="RequestAppToken">Whether the call is made as the application, not on behalf of a user.</param>
/// <param name="Scopes">The scopes to request: each a string or a <see cref="ApplicationModel.ReferenceExpression"/>.</param>
internal sealed record EntraIdDownstreamApi(string Name, string? BaseUrl, bool RequestAppToken, IReadOnlyList<object> Scopes);
