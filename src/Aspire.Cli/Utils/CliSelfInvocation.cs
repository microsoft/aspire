// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Utils;

internal sealed record CliSelfInvocation(string ExecutablePath, string[] Arguments)
{
    internal static CliSelfInvocation Current() =>
        Create(
            Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the invoking Aspire CLI."),
            Environment.GetCommandLineArgs()[0]);

    internal static CliSelfInvocation Create(string executablePath, string entryPoint)
    {
        // Managed argv[0] is the DLL, not dotnet's command line. Replaying all user
        // arguments would recursively run the original command instead of the proxy.
        var isDotnet = Path.GetFileNameWithoutExtension(executablePath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        return new(executablePath, isDotnet ? [Path.GetFullPath(entryPoint)] : []);
    }
}
