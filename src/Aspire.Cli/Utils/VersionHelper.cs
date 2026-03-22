// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Resources;
using Aspire.Shared;

namespace Aspire.Cli.Utils;

internal static class VersionHelper
{
    public static string GetDefaultTemplateVersion()
    {
        return PackageUpdateHelpers.GetCurrentAssemblyVersion() ?? throw new InvalidOperationException(ErrorStrings.UnableToRetrieveAssemblyVersion);
    }

    /// <summary>
    /// Gets the default Aspire SDK version based on the CLI version.
    /// The CLI version is the SDK version — the bundled server and packages must match.
    /// </summary>
    public static string GetDefaultSdkVersion()
    {
        var version = GetDefaultTemplateVersion();

        // Strip the commit SHA suffix (e.g., "9.2.0+abc123" -> "9.2.0")
        var plusIndex = version.IndexOf('+');
        if (plusIndex > 0)
        {
            version = version[..plusIndex];
        }

        return version;
    }
}
