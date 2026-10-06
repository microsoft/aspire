// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;

namespace Aspire.Shared.UserSecrets;

/// <summary>
/// Helpers for resolving Aspire environment secrets file paths.
/// </summary>
internal static class AspireSecretsPathHelper
{
    internal const string SecretsDirectoryName = "secrets";

    /// <summary>
    /// Returns the Aspire secrets file path for an AppHost/environment pair.
    /// </summary>
    public static string GetSecretsFilePath(DirectoryInfo homeDirectory, string appHostId, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostId);
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);

        var safeEnvironmentName = CreateSafePathSegment(environmentName);
        return Path.Combine(GetSecretsDirectoryPath(homeDirectory, appHostId), $"{safeEnvironmentName}.json");
    }

    /// <summary>
    /// Returns the Aspire secrets directory path for an AppHost.
    /// </summary>
    public static string GetSecretsDirectoryPath(DirectoryInfo homeDirectory, string appHostId)
    {
        ArgumentNullException.ThrowIfNull(homeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostId);

        var safeAppHostId = CreateSafePathSegment(appHostId);
        return Path.Combine(homeDirectory.FullName, ".aspire", SecretsDirectoryName, safeAppHostId);
    }

    /// <summary>
    /// Returns the current user's home directory used for Aspire secrets.
    /// </summary>
    public static DirectoryInfo GetDefaultHomeDirectory()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            throw new InvalidOperationException("Unable to determine the user profile directory for Aspire secrets.");
        }

        return new DirectoryInfo(homeDirectory);
    }

    /// <summary>
    /// Computes a deterministic synthetic AppHost ID from a file path.
    /// </summary>
    public static string ComputeSyntheticAppHostId(string appHostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appHostPath);

        var normalizedPath = appHostPath.ToLowerInvariant();
        return $"apphost-{ComputeStableHash(normalizedPath)}";
    }

    internal static string CreateSafePathSegment(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var builder = new StringBuilder(value.Length);
        var changed = false;

        foreach (var ch in value)
        {
            if (IsSafePathSegmentCharacter(ch))
            {
                builder.Append(ch);
            }
            else
            {
                builder.Append('_');
                changed = true;
            }
        }

        var sanitized = builder.ToString().Trim('.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "value";
            changed = true;
        }

        if (!changed && sanitized.Length <= 80)
        {
            return sanitized;
        }

        var readablePrefix = sanitized.Length <= 48 ? sanitized : sanitized[..48];
        return $"{readablePrefix}-{ComputeStableHash(value)}";
    }

    private static bool IsSafePathSegmentCharacter(char ch) =>
        ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.';

    private static string ComputeStableHash(string value)
    {
        var hashBytes = XxHash3.Hash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
