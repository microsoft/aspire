// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Resolves the default home directory used by Aspire tools.
/// </summary>
internal static class AspireHomeDirectory
{
    /// <summary>
    /// The environment variable that overrides the default Aspire home directory.
    /// </summary>
    internal const string EnvironmentVariable = "ASPIRE_HOME";

    /// <summary>
    /// Gets the configured Aspire home directory, or the default directory in the current user's profile.
    /// </summary>
    internal static string GetDefault()
    {
        return GetDefault(
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// Gets the configured Aspire home directory, or the default directory in <paramref name="userProfileDirectory"/>.
    /// </summary>
    internal static string GetDefault(string? configuredAspireHome, string userProfileDirectory)
    {
        return string.IsNullOrWhiteSpace(configuredAspireHome)
            ? Path.Combine(userProfileDirectory, ".aspire")
            : configuredAspireHome;
    }
}