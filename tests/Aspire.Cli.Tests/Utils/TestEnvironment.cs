// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Utils;

/// <summary>
/// Test <see cref="IEnvironment"/> backed by a dictionary so tests can inject
/// specific environment variable values without touching the process environment.
/// </summary>
internal sealed class TestEnvironment : IEnvironment
{
    public IReadOnlyDictionary<string, string?> Variables { get; }

    public TestEnvironment(IReadOnlyDictionary<string, string?>? variables = null)
    {
        Variables = variables ?? new Dictionary<string, string?>();
    }

    public string? GetEnvironmentVariable(string variable)
    {
        return Variables.TryGetValue(variable, out var value) ? value : null;
    }

    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    public bool IsLinux { get; init; } = OperatingSystem.IsLinux();

    public bool IsMacOS { get; init; } = OperatingSystem.IsMacOS();
}
