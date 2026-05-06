// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Packaging;

/// <summary>
/// Defines the standard channel names used by the Aspire CLI.
/// </summary>
internal static class PackageChannelNames
{
    /// <summary>
    /// The stable channel name, using official NuGet releases.
    /// </summary>
    public const string Stable = "stable";

    /// <summary>
    /// The staging channel name, using RC/preview builds.
    /// </summary>
    public const string Staging = "staging";

    /// <summary>
    /// The daily channel name, using the latest daily builds.
    /// </summary>
    public const string Daily = "daily";

    /// <summary>
    /// The default channel name, based on the user's NuGet configuration.
    /// </summary>
    public const string Default = "default";

    /// <summary>
    /// The local channel name, used when the CLI binary was built locally
    /// (i.e., with <c>AspireCliChannel=local</c>).
    /// </summary>
    public const string Local = "local";
}
