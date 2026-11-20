// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Shared;

/// <summary>
/// Centralizes temporary storage allocation for Aspire.
/// </summary>
internal static class TemporaryStorage
{
    // This helper is the sole production boundary for system temporary storage APIs.
#pragma warning disable RS0030
    internal static string GetTempPath() => Path.GetTempPath();

    internal static string GetTempFileName() => Path.GetTempFileName();

    internal static DirectoryInfo CreateTempSubdirectory(string? prefix = null)
        => Directory.CreateTempSubdirectory(prefix);
#pragma warning restore RS0030
}
