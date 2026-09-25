// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Shared helpers for preparing E2E test directory fixtures.
/// </summary>
internal static class TestDirectoryHelpers
{
    /// <summary>
    /// Copies a directory tree into a new destination.
    /// </summary>
    internal static void CopyDirectory(string source, string destination)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
