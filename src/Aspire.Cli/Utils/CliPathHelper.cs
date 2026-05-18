// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Backchannel;

namespace Aspire.Cli.Utils;

internal static class CliPathHelper
{
    internal static string GetAspireHomeDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire");

    /// <summary>
    /// Creates a randomized CLI-managed socket path.
    /// </summary>
    /// <param name="socketPrefix">The socket file prefix.</param>
    internal static string CreateUnixDomainSocketPath(string socketPrefix)
        => CreateSocketPath(socketPrefix);

    internal static string CreateGuestAppHostSocketPath(string socketPrefix)
        => CreateSocketPath(socketPrefix);

    private static string CreateSocketPath(string socketPrefix)
    {
        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var socketPath = BackchannelConstants.ComputeCliSocketPath(homeDirectory, socketPrefix);
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        return socketPath;
    }
}
