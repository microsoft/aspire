// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Configuration;

/// <summary>
/// Specifies how access to the dashboard frontend is authenticated.
/// </summary>
public enum FrontendAuthMode
{
    /// <summary>
    /// Allows anonymous access to the dashboard. See the
    /// <see href="https://aspire.dev/dashboard/security-considerations/">dashboard security considerations</see>.
    /// </summary>
    Unsecured,

    /// <summary>
    /// Authenticates users with OpenID Connect.
    /// </summary>
    OpenIdConnect,

    /// <summary>
    /// Authenticates users with a browser token. This is the default authentication mode.
    /// </summary>
    BrowserToken
}
