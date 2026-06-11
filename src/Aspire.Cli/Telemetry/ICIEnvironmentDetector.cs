// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Detects whether the CLI is running in a CI environment.
/// </summary>
internal interface ICIEnvironmentDetector
{
    /// <summary>
    /// Determines whether the CLI is running in a CI environment.
    /// </summary>
    /// <returns><see langword="true"/> if running in a CI environment; otherwise, <see langword="false"/>.</returns>
    bool IsCIEnvironment();
}
