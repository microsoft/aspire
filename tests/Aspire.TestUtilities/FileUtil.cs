// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.TestUtilities;

internal static class FileUtil
{
    public static string? FindFullPathFromPath(string command) => PathLookupHelper.FindFullPathFromPath(command);
}
