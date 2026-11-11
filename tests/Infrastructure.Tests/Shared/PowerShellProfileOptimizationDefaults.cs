// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Infrastructure.Tests;

/// <summary>
/// Stops every <c>pwsh</c> process launched by this test assembly from writing the shared
/// multicore-JIT startup profile.
///
/// pwsh calls <c>ProfileOptimization.StartProfile("StartupProfileData-NonInteractive")</c>, which
/// reads and rewrites a single file under the user's cache directory
/// (<c>~/.cache/powershell</c> on Linux). xUnit runs test classes in parallel, so several pwsh
/// processes can rewrite that file while another pwsh is replaying it. The reader then parses a
/// torn assembly name and pwsh aborts with
/// <c>System.IO.FileLoadException: The given assembly name was invalid</c> (exit code 134). See
/// https://github.com/microsoft/aspire/issues/19992, PowerShell/PowerShell#26528 and
/// dotnet/runtime#121977.
///
/// Child processes inherit this environment variable, so the setting covers every way the tests
/// launch pwsh, including scripts that start nested pwsh processes. The runtime still replays an
/// existing profile, but nothing rewrites it, so concurrent readers can't see a partial write.
/// </summary>
internal static class PowerShellProfileOptimizationDefaults
{
    internal const string NoProfileGatherVariable = "DOTNET_MultiCoreJitNoProfileGather";

    [ModuleInitializer]
    internal static void DisableSharedStartupProfileWrites()
    {
        Environment.SetEnvironmentVariable(NoProfileGatherVariable, "1");
    }
}
