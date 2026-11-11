// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Detects CI environments by checking for known CI system environment variables.
/// </summary>
internal sealed class CIEnvironmentDetector : ICIEnvironmentDetector
{
    private readonly IEnvironment _environment;

    public CIEnvironmentDetector(IEnvironment environment)
    {
        // Like dotnet, inspect the process environment rather than allowing CLI settings to override CI detection.
        _environment = environment;
    }

    /// <inheritdoc />
    public bool IsCIEnvironment() => IsCIEnvironment(_environment);

    public static bool IsCIEnvironment(IEnvironment environment)
        => Aspire.Shared.Telemetry.CIEnvironmentDetector.IsCIEnvironment(environment.GetEnvironmentVariable);
}
