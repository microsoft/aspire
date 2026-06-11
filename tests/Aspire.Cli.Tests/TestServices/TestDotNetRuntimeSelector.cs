// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestDotNetRuntimeSelector : IDotNetRuntimeSelector
{
    public bool InitializeResult { get; set; } = true;
    public DotNetRuntimeMode RuntimeMode { get; set; } = DotNetRuntimeMode.System;
    public string ExecutablePath { get; set; } = "dotnet";

    public string DotNetExecutablePath => ExecutablePath;

    public DotNetRuntimeMode Mode => RuntimeMode;

    public Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(InitializeResult);
    }

    public IDictionary<string, string> GetEnvironmentVariables()
    {
        return new Dictionary<string, string>();
    }
}
