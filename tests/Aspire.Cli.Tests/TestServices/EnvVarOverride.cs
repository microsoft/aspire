// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Restores an environment variable to its prior value on dispose. Used
/// in DiscoverAll tests to point the discovery walk at a controlled
/// <c>HOME</c> / <c>USERPROFILE</c> / <c>PATH</c> sandbox.
/// </summary>
internal sealed class EnvVarOverride : IDisposable
{
    private readonly string _name;
    private readonly string? _previous;

    public EnvVarOverride(string name, string? value)
    {
        _name = name;
        _previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _previous);
    }
}
