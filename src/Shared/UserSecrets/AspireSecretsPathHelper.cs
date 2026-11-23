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

        ValidateEnvironmentName(environmentName);
        var directory = GetSecretsDirectoryPath(homeDirectory, appHostId);
        if (Directory.Exists(directory))
        {
            // Environment identity must not depend on the filesystem's case sensitivity.
            // Retain the existing filename spelling so list --all reports the original name.
            var existingPaths = Directory.EnumerateFiles(directory)
                .Where(path => string.Equals(Path.GetFileName(path), $"{environmentName}.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (existingPaths.Length > 1)
            {
                throw new InvalidOperationException($"Multiple Aspire secrets files exist for environment '{environmentName}' in '{directory}'. Consolidate the files before continuing.");
            }

            if (existingPaths.Length == 1)
            {
                return existingPaths[0];
            }
        }

        return Path.Combine(directory, $"{environmentName}.json");
    }

    /// <summary>
    /// Returns whether an environment name can be used as a portable secrets filename.
    /// </summary>
    public static bool IsValidEnvironmentName(string? environmentName)
    {
        if (environmentName is null || environmentName.Length is < 1 or > 80 ||
            !char.IsAsciiLetterOrDigit(environmentName[0]) ||
            environmentName.Any(ch => !char.IsAsciiLetterOrDigit(ch) && ch is not '-' and not '_'))
        {
            return false;
        }

        return !IsWindowsReservedDeviceName(environmentName);
    }

    /// <summary>
    /// Rejects environment names that cannot be used as portable secrets filenames.
    /// </summary>
    public static void ValidateEnvironmentName(string environmentName)
    {
        if (!IsValidEnvironmentName(environmentName))
        {
            throw new ArgumentException("Environment names must contain 1-80 ASCII letters, digits, hyphens, or underscores, start with a letter or digit, and must not be Windows reserved device names.", nameof(environmentName));
        }
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

        // Match platform path comparison without merging distinct Unix AppHosts.
        var normalizedPath = OperatingSystem.IsWindows() ? appHostPath.ToLowerInvariant() : appHostPath;
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

        var segment = builder.ToString();
        var sanitized = segment.Trim('.');
        changed |= sanitized.Length != segment.Length;
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "value";
            changed = true;
        }

        if (IsWindowsReservedDeviceName(sanitized.Split('.')[0]))
        {
            sanitized = $"_{sanitized}";
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

    // Windows reserves these basenames even when a file extension is present.
    // https://learn.microsoft.com/windows/win32/fileio/naming-a-file#naming-conventions
    private static bool IsWindowsReservedDeviceName(string name) =>
        name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        (name.Length == 4 && name[3] is >= '1' and <= '9' &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));

    private static string ComputeStableHash(string value)
    {
        var hashBytes = XxHash3.Hash(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
