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
        => CreateSocketPath(socketPrefix, isGuestAppHost: false);

    internal static string CreateGuestAppHostSocketPath(string socketPrefix)
        => CreateSocketPath(socketPrefix, isGuestAppHost: true);

    private static string CreateSocketPath(string socketPrefix, bool isGuestAppHost)
    {
        var socketName = $"{socketPrefix}.{BackchannelConstants.CreateRandomIdentifier()}";

        if (isGuestAppHost && OperatingSystem.IsWindows())
        {
            return socketName;
        }

        var socketDirectory = GetCliSocketDirectory();
        Directory.CreateDirectory(socketDirectory);
        return Path.Combine(socketDirectory, socketName);
    }

    private static string GetCliHomeDirectory()
        => Path.Combine(GetAspireHomeDirectory(), "cli");

    private static string GetCliRuntimeDirectory()
        => Path.Combine(GetCliHomeDirectory(), "runtime");

    private static string GetCliSocketDirectory()
        => Path.Combine(GetCliRuntimeDirectory(), "sockets");
}
