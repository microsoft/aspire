// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared.UserSecrets;

/// <summary>
/// Helper class for working with user secrets in isolation mode.
/// </summary>
internal static class IsolatedUserSecretsHelper
{
    /// <summary>
    /// Creates an isolated copy of user secrets with a new random ID.
    /// </summary>
    /// <param name="originalUserSecretsId">The original user secrets ID from the project.</param>
    /// <returns>The new isolated user secrets ID, or null if no secrets exist to copy.</returns>
    public static string? CreateIsolatedUserSecrets(string? originalUserSecretsId)
    {
        if (string.IsNullOrWhiteSpace(originalUserSecretsId))
        {
            return null;
        }

        var originalSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(originalUserSecretsId);

        // If the original secrets file doesn't exist, there's nothing to copy
        if (!File.Exists(originalSecretsPath))
        {
            return null;
        }

        // Generate a new random user secrets ID
        var isolatedUserSecretsId = Guid.NewGuid().ToString();
        var isolatedSecretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(isolatedUserSecretsId);

        // Ensure the directory exists
        var isolatedSecretsDir = Path.GetDirectoryName(isolatedSecretsPath);
        if (!string.IsNullOrEmpty(isolatedSecretsDir) && !Directory.Exists(isolatedSecretsDir))
        {
            Directory.CreateDirectory(isolatedSecretsDir);
        }

        // Copy the secrets file
        File.Copy(originalSecretsPath, isolatedSecretsPath, overwrite: true);

        return isolatedUserSecretsId;
    }

    /// <summary>
    /// Cleans up isolated user secrets by deleting the secrets file and directory.
    /// </summary>
    /// <param name="isolatedUserSecretsId">The isolated user secrets ID to clean up.</param>
    public static void CleanupIsolatedUserSecrets(string? isolatedUserSecretsId)
    {
        if (string.IsNullOrWhiteSpace(isolatedUserSecretsId))
        {
            return;
        }

        try
        {
            var secretsPath = UserSecretsPathHelper.GetSecretsPathFromSecretsId(isolatedUserSecretsId);
            var secretsDir = Path.GetDirectoryName(secretsPath);

            if (File.Exists(secretsPath))
            {
                File.Delete(secretsPath);
            }

            // Only delete the directory if it's empty
            if (!string.IsNullOrEmpty(secretsDir) && Directory.Exists(secretsDir))
            {
                var remainingFiles = Directory.GetFiles(secretsDir);
                if (remainingFiles.Length == 0)
                {
                    Directory.Delete(secretsDir);
                }
            }
        }
        catch
        {
            // Best-effort cleanup - don't fail if we can't delete
        }
    }
}
