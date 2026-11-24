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

        try
        {
            var directory = Path.GetDirectoryName(_sentinelFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Several CLI processes can run for the first time at once, for example parallel AppHost builds that each
            // run `aspire setup` on a fresh CI agent (https://github.com/microsoft/aspire/issues/22490). Only the file's
            // existence matters, so open it in a way that lets a racing process open it too instead of failing with a
            // sharing violation.
            using var _ = new FileStream(_sentinelFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to record the notice only means it may be shown again; it must never stop the command.
        }
    }
}
