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

    public IEnumerable<(string Name, string? Value)> GetEnvironmentVariables()
    {
        return Variables.Select(pair => (pair.Key, pair.Value));
    }

    private bool ReportIsWindows { get; init; } = OperatingSystem.IsWindows();

    private bool ReportIsLinux { get; init; } = OperatingSystem.IsLinux();

    private bool ReportIsMacOS { get; init; } = OperatingSystem.IsMacOS();

    public bool IsWindows() => ReportIsWindows;

    public bool IsLinux() => ReportIsLinux;

    public bool IsMacOS() => ReportIsMacOS;

    public static TestEnvironment CreateWindows(IReadOnlyDictionary<string, string?>? variables = null)
        => new(variables) { ReportIsWindows = true, ReportIsLinux = false, ReportIsMacOS = false };

    public static TestEnvironment CreateLinux(IReadOnlyDictionary<string, string?>? variables = null)
        => new(variables) { ReportIsWindows = false, ReportIsLinux = true, ReportIsMacOS = false };

    public static TestEnvironment CreateMacOS(IReadOnlyDictionary<string, string?>? variables = null)
        => new(variables) { ReportIsWindows = false, ReportIsLinux = false, ReportIsMacOS = true };
}
