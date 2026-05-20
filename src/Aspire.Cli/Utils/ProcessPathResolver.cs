// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils;

internal static class ProcessPathResolver
{
    public static string ResolveSymlinks(string path, ILogger logger)
    {
        try
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return resolved is null ? path : resolved.FullName;
        }
        catch (Exception ex)
        {
            // Best-effort symlink resolution: any failure falls back to the raw
            // path. Sidecar and layout discovery using the raw path is still valid
            // in the non-link case.
            logger.LogDebug(ex, "Failed to resolve link target for {Path}; using raw path.", path);
            return path;
        }
    }
}
