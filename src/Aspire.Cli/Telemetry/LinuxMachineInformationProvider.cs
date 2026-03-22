// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Telemetry;

// This is copied from https://github.com/microsoft/mcp/tree/6bb4d76a63d24854efe0fa0bd96f5ab6f699ed3a/core/Azure.Mcp.Core/src/Services/Telemetry
// Keep in sync with updates there.

[SupportedOSPlatform("linux")]
internal class LinuxMachineInformationProvider(ILogger<LinuxMachineInformationProvider> logger) : UnixMachineInformationProvider(logger)
{
    private const string PrimaryPathEnvVar = "XDG_CACHE_HOME";
    private const string SecondaryPathSubDirectory = ".cache";

    /// <summary>
    /// Gets the base folder for the cache to be stored.
    /// The final path should be $HOME\.cache\Microsoft\DeveloperTools.
    /// </summary>
    public override string GetStoragePath()
    {
        var userDir = Environment.GetEnvironmentVariable(PrimaryPathEnvVar);

        // If this comes back as null or empty/whitespace, use user profile.
        if (string.IsNullOrWhiteSpace(userDir))
        {
            var rootPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // If the secondary path is still null/empty/whitespace, then throw as it will lead
            // to us caching the data in the wrong directory otherwise.
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                throw new InvalidOperationException("linux: Unable to get UserProfile or $HOME folder.");
            }

            userDir = Path.Combine(rootPath, SecondaryPathSubDirectory);
        }

        return userDir;
    }
}
