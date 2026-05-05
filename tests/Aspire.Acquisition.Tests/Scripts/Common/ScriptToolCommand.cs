// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Templates.Tests;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Executes CLI acquisition scripts with an isolated test environment.
/// </summary>
public class ScriptToolCommand : ScriptCommand
{
    /// <summary>
    /// Creates a new command to execute a script.
    /// </summary>
    /// <param name="scriptPath">Relative path to the script from repo root (e.g., "eng/scripts/get-aspire-cli.sh")</param>
    /// <param name="testEnvironment">Test environment providing isolated temp directories</param>
    /// <param name="testOutput">xUnit test output helper</param>
    public ScriptToolCommand(
        string scriptPath,
        TestEnvironment testEnvironment,
        ITestOutputHelper testOutput)
        : base(scriptPath, testOutput, label: Path.GetFileName(scriptPath))
    {
        // Set mock HOME to prevent any accidental user directory access
        WithEnvironmentVariable("HOME", testEnvironment.MockHome);
        WithEnvironmentVariable("USERPROFILE", testEnvironment.MockHome);
        // Override XDG_CONFIG_HOME to prevent scripts that consult
        // ${XDG_CONFIG_HOME:-$HOME/.config} from reading a real profile
        // outside the temp home when the developer has XDG_CONFIG_HOME set.
        WithEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(testEnvironment.MockHome, ".config"));

        // Disable any real PATH modifications during tests
        WithEnvironmentVariable("ASPIRE_TEST_MODE", "true");

        // Default timeout to prevent hanging tests — individual tests can override via WithTimeout()
        WithTimeout(TimeSpan.FromSeconds(60));
    }
}
