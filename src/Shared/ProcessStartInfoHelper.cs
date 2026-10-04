// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

internal static class ProcessStartInfoHelper
{
    /// <summary>
    /// Configures an executable and its arguments, including Windows batch shims.
    /// </summary>
    public static void SetCommand(ProcessStartInfo startInfo, string command, IEnumerable<string> args, bool isWindows)
    {
        // cmd.exe /c strips the outer quotes, so a batch command needs the shape:
        // /c ""C:\Program Files\nodejs\npx.cmd" "--no-install" "tsx" "C:\my app\host.mts""
        // ArgumentList's per-argument quoting cannot supply this outer command boundary.
        // https://learn.microsoft.com/windows-server/administration/windows-commands/cmd
        if (isWindows && (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                          command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = @$"/c """"{command}"" {string.Join(" ", args.Select(a => @$"""{a}"""))}""";
        }
        else
        {
            startInfo.FileName = command;
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }
    }
}
