// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Aspire.Templates.Tests;

/// <summary>
/// Extension methods for Process class.
/// </summary>
internal static class ProcessExtensions
{
    public static bool TryGetHasExited(this Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException ie) when (ie.Message.Contains("No process is associated with this object"))
        {
            return true;
        }
    }

    public static void CloseAndKillProcessIfRunning(this Process process)
    {
        if (process is null || process.TryGetHasExited())
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            process.CloseMainWindow();
        }
        process.Kill(entireProcessTree: true);
        process.Dispose();
    }
}
