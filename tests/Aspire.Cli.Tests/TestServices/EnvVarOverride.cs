// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

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

    /// <summary>
    /// Sets the variable to the PID of a process that has already exited, so code watching that
    /// process observes the parent as gone immediately.
    /// </summary>
    public static EnvVarOverride CreateForExitedProcess(string name)
    {
        using var exited = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            ArgumentList = { "--info" },
            // Not redirected: this file is under test, so inheriting the test host's pipes would keep
            // WriteLineAsync waiting on a pipe the exited child no longer reads.
            UseShellExecute = false,
        })!;
        exited.WaitForExit();

        return new EnvVarOverride(name, exited.Id.ToString(CultureInfo.InvariantCulture));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_name, _previous);
    }
}
