// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Backchannel;

namespace Aspire.Hosting.Tests.Utils;

internal static class UnixSocketHelper
{
    public static string GetBackchannelSocketPath()
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var socketPath = BackchannelConstants.ComputeCliSocketPath(homeDirectory, "cli.sock");
        var aspireCliPath = Path.GetDirectoryName(socketPath)!;

        if (!Directory.Exists(aspireCliPath))
        {
            Directory.CreateDirectory(aspireCliPath);
        }

        return socketPath;
    }

}
