// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDotNetRuntimeSelector : IDotNetRuntimeSelector
{
    public Func<CancellationToken, bool>? InitializeAsyncCallback { get; set; }

    public string DotNetExecutablePath => "dotnet";

    public DotNetRuntimeMode Mode => DotNetRuntimeMode.System;

    public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        return InitializeAsyncCallback != null
            ? Task.FromResult(InitializeAsyncCallback(cancellationToken))
            : Task.FromResult(true); // Default to SDK available
    }

    public IDictionary<string, string> GetEnvironmentVariables()
    {
        return new Dictionary<string, string>();
    }
}
