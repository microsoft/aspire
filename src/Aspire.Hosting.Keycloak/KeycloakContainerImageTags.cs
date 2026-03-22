// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Keycloak;

internal static class KeycloakContainerImageTags
{
    /// <remarks>quay.io</remarks>
    public const string Registry = "quay.io";

    /// <remarks>keycloak/keycloak</remarks>
    public const string Image = "keycloak/keycloak";

    /// <remarks>26.5</remarks>
    public const string Tag = "26.5";

    // <remarks>1000</remarks>>
    public const int ContainerUser = 1000;

    // <remarks>1000</remarks>
    public const int ContainerGroup = 1000;
}

