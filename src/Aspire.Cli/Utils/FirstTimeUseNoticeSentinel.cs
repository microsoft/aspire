// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

/// <summary>
/// Manages a sentinel file in the user's .aspire/cli directory to track if the CLI first-time use notice has been displayed.
/// </summary>
internal sealed class FirstTimeUseNoticeSentinel : IFirstTimeUseNoticeSentinel
{
    private const string SentinelFileName = "cli.firstUseSentinel";
    private readonly string _sentinelFilePath;

    /// <summary>
    /// Initializes a new instance of the <see cref="FirstTimeUseNoticeSentinel"/> class.
    /// </summary>
    /// <param name="aspireUserDirectory">The path to the user's .aspire directory.</param>
    public FirstTimeUseNoticeSentinel(string aspireUserDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aspireUserDirectory);
        _sentinelFilePath = Path.Combine(aspireUserDirectory, "cli", SentinelFileName);
    }

    /// <inheritdoc />
    public bool Exists()
    {
        return File.Exists(_sentinelFilePath);
    }

    /// <inheritdoc />
    public void CreateIfNotExists()
    {
        if (Exists())
        {
            return;
        }

        var directory = Path.GetDirectoryName(_sentinelFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Create an empty sentinel file
        File.WriteAllText(_sentinelFilePath, string.Empty);
    }
}
