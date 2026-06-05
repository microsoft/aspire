// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure.AppConfiguration;

internal static class AppConfigurationEmulatorContainerImageTags
{
    /// <remarks>mcr.microsoft.com</remarks>
    public const string Registry = "mcr.microsoft.com";

    /// <remarks>azure-app-configuration/app-configuration-emulator</remarks>
    public const string Image = "azure-app-configuration/app-configuration-emulator";

    /// <remarks>1.0.2</remarks>
    public const string Tag = "1.0.2";
}
