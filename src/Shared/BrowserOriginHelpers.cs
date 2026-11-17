// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Utils;

/// <summary>
/// Normalizes browser origins without navigation paths or user information.
/// </summary>
internal static class BrowserOriginHelpers
{
    public static string GetOrigin(Uri uri) =>
        new UriBuilder(uri) { Host = uri.HostNameType == UriHostNameType.Dns ? uri.IdnHost : uri.Host }.Uri
            .GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
}
