// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Acquisition.Tests.Scripts;

internal static class ScriptPaths
{
    private static readonly string s_scriptsDirectory = Path.Combine("eng", "scripts");

    public static readonly string ReleaseShell = Path.Combine(s_scriptsDirectory, "get-aspire-cli.sh");
    public static readonly string ReleasePowerShell = Path.Combine(s_scriptsDirectory, "get-aspire-cli.ps1");
    public static readonly string PRShell = Path.Combine(s_scriptsDirectory, "get-aspire-cli-pr.sh");
    public static readonly string PRPowerShell = Path.Combine(s_scriptsDirectory, "get-aspire-cli-pr.ps1");
}
