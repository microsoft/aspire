// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Small file-system helpers shared across test classes.
/// </summary>
internal static class FileHelper
{
    /// <summary>
    /// Marks a file as user-executable on Unix; no-op on Windows.
    /// </summary>
    internal static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
