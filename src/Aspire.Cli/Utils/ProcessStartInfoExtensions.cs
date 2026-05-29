// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Cli.Utils;

internal static class ProcessStartInfoExtensions
{
    public static ProcessStartInfo CreateNewProcessGroupOnWindows(this ProcessStartInfo startInfo)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows can only target CTRL_BREAK_EVENT at a child process group when the child is
            // started with CREATE_NEW_PROCESS_GROUP. Ctrl+C is not usable for that case, so Aspire
            // creates groups for guest AppHosts and AppHost servers that it needs to stop gracefully.
            startInfo.CreateNewProcessGroup = true;
        }

        return startInfo;
    }
}
