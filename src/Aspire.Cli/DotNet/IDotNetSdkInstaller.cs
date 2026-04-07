// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.DotNet;

/// <summary>
/// Service responsible for checking .NET SDK availability.
/// </summary>
internal interface IDotNetSdkInstaller
{
    /// <summary>
    /// Checks if the .NET SDK is available on the system PATH.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A tuple containing: success flag, highest detected version, and minimum required version.</returns>
    Task<(bool Success, string? HighestDetectedVersion, string MinimumRequiredVersion)> CheckAsync(CancellationToken cancellationToken = default);
}