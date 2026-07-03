// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.DotNet;

/// <summary>
/// Service responsible for checking and installing the .NET SDK.
/// </summary>
internal interface IDotNetSdkInstaller
{
    /// <summary>
    /// Gets or sets the path to the dotnet executable to use for SDK checks.
    /// Defaults to "dotnet" (system PATH). Can be updated by <see cref="IDotNetRuntimeSelector"/>
    /// after it selects a private or custom SDK to ensure checks use the correct executable.
    /// </summary>
    string DotNetExecutablePath { get; set; }

    /// <summary>
    /// Checks if the .NET SDK is available on the system PATH.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the SDK is available, false otherwise.</returns>
    Task<bool> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the .NET SDK is available on the system PATH and meets the minimum version requirement.
    /// </summary>
    /// <param name="minimumVersion">The minimum SDK version required.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the SDK is available and meets the minimum version, false otherwise.</returns>
    Task<bool> CheckAsync(string minimumVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs the .NET SDK. This method is reserved for future extensibility.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    Task InstallAsync(CancellationToken cancellationToken = default);
}