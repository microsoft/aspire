// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
}
