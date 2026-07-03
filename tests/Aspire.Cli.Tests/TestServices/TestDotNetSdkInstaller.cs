// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDotNetSdkInstaller : IDotNetSdkInstaller
{
    public Func<CancellationToken, bool>? CheckAsyncCallback { get; set; }
    public Func<string, CancellationToken, bool>? CheckAsyncWithVersionCallback { get; set; }
    public Func<CancellationToken, Task>? InstallAsyncCallback { get; set; }
    public string DotNetExecutablePath { get; set; } = "dotnet";

    public Task<bool> CheckAsync(CancellationToken cancellationToken = default)
    {
        return CheckAsyncCallback != null
            ? Task.FromResult(CheckAsyncCallback(cancellationToken))
            : Task.FromResult(true); // Default to SDK available
    }

    public Task<bool> CheckAsync(string minimumVersion, CancellationToken cancellationToken = default)
    {
        return CheckAsyncWithVersionCallback != null
            ? Task.FromResult(CheckAsyncWithVersionCallback(minimumVersion, cancellationToken))
            : Task.FromResult(true); // Default to SDK available
    }

    public Task InstallAsync(CancellationToken cancellationToken = default)
    {
        return InstallAsyncCallback != null
            ? InstallAsyncCallback(cancellationToken)
            : throw new NotImplementedException();
    }
}