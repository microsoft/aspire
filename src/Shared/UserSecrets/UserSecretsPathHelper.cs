// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Text;

namespace Aspire.Shared.UserSecrets;

// Copied from https://github.com/dotnet/runtime/blob/213833ea99b79a4b494b2935e1ccb10b93cd4cbc/src/libraries/Microsoft.Extensions.Configuration.UserSecrets/src/PathHelper.cs

/// <summary>
/// Helpers for resolving user secrets file paths.
/// </summary>
internal static class UserSecretsPathHelper
{
    internal const string SecretsFileName = "secrets.json";

    /// <summary>
    /// Returns the path to the JSON file that stores user secrets.
    /// </summary>
    public static string GetSecretsPathFromSecretsId(string userSecretsId)
    {
        return InternalGetSecretsPathFromSecretsId(userSecretsId, throwIfNoRoot: true);
    }

    /// <summary>
    /// Returns the path to the user secrets file, optionally throwing if no root is found.
    /// </summary>
    internal static string InternalGetSecretsPathFromSecretsId(string userSecretsId, bool throwIfNoRoot)
    {
        const string userSecretsFallbackDir = "DOTNET_USER_SECRETS_FALLBACK_DIR";

        // For backwards compat, this checks env vars first before using Env.GetFolderPath
        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        string? root = appData                                                               // On Windows it goes to %APPDATA%\Microsoft\UserSecrets\
                   ?? Environment.GetEnvironmentVariable("HOME")                             // On Mac/Linux it goes to ~/.microsoft/usersecrets/
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                   ?? Environment.GetEnvironmentVariable(userSecretsFallbackDir);            // this fallback is an escape hatch if everything else fails

        if (string.IsNullOrEmpty(root))
        {
            if (throwIfNoRoot)
            {
                throw new InvalidOperationException($"Missing user secrets location {userSecretsFallbackDir}");
            }

            return string.Empty;
        }

        return !string.IsNullOrEmpty(appData)
            ? Path.Combine(root, "Microsoft", "UserSecrets", userSecretsId, SecretsFileName)
            : Path.Combine(root, ".microsoft", "usersecrets", userSecretsId, SecretsFileName);
    }

    /// <summary>
    /// Computes a deterministic synthetic UserSecretsId from a file path.
    /// Used for polyglot AppHosts that don't have a csproj with UserSecretsId.
    /// </summary>
    public static string ComputeSyntheticUserSecretsId(string appHostPath)
    {
        var hashBytes = SHA256.HashData(
            Encoding.UTF8.GetBytes(appHostPath.ToLowerInvariant()));
        var hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        return $"aspire-{hash[..32]}";
    }
}
