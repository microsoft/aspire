// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

namespace Aspire.Hosting;

// Starts Chromium with a private CDP pipe. This cannot use ProcessStartInfo today because the repo's target frameworks
// do not expose a supported way to map arbitrary child handles/fds. Chromium's pipe protocol has platform-specific
// launch contracts:
// - POSIX: the child must see the browser-input pipe at fd 3 and browser-output pipe at fd 4.
// - Windows: Chromium can adopt explicit inherited handles supplied through --remote-debugging-io-pipes=<read>,<write>.
internal static partial class BrowserLogsPipeBrowserProcessLauncher
{
    private const string RemoteDebuggingPipeArgument = "--remote-debugging-pipe";
    private static readonly TimeSpan s_processExitTimeout = TimeSpan.FromSeconds(5);

    public static IBrowserLogsPipeBrowserProcess Start(
        string executablePath,
        IReadOnlyList<string> browserArguments)
    {
        return OperatingSystem.IsWindows()
            ? StartWindows(executablePath, browserArguments)
            : StartPosix(executablePath, browserArguments);
    }

    internal static List<string> CreatePipeArguments(IReadOnlyList<string> browserArguments)
    {
        return [.. browserArguments, RemoteDebuggingPipeArgument];
    }
}
