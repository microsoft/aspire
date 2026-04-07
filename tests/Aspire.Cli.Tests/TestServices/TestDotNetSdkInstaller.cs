// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDotNetSdkInstaller : IDotNetSdkInstaller
{
    public Func<CancellationToken, (bool Success, string? HighestDetectedVersion, string MinimumRequiredVersion)>? CheckAsyncCallback { get; set; }

    public Task<(bool Success, string? HighestDetectedVersion, string MinimumRequiredVersion)> CheckAsync(CancellationToken cancellationToken = default)
    {
        return CheckAsyncCallback != null
            ? Task.FromResult(CheckAsyncCallback(cancellationToken))
            : Task.FromResult<(bool Success, string? HighestDetectedVersion, string MinimumRequiredVersion)>((true, "9.0.302", "9.0.302")); // Default to SDK available
    }
}