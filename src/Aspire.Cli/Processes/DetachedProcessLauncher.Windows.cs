// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Aspire.Cli.Processes;

internal static partial class DetachedProcessLauncher
{
    /// <summary>
    /// Windows implementation using <see cref="WindowsProcessInterop.SpawnConsoleIsolatedProcess"/>
    /// with NUL bound to stdout and stderr (stdin is left unset). The detached child is NOT
    /// assigned to the CLI's kill-on-close job — the entire point of <c>aspire start</c> is
    /// that the AppHost outlives the launching CLI.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static Process StartWindows(string fileName, IReadOnlyList<string> arguments, string workingDirectory, Func<string, bool>? shouldRemoveEnvironmentVariable, IReadOnlyDictionary<string, string>? additionalEnvironmentVariables)
    {
        // Open NUL for the child's stdout/stderr — child writes go nowhere. The handle must be
        // inheritable (PROC_THREAD_ATTRIBUTE_HANDLE_LIST whitelists but does NOT promote
        // non-inheritable handles).
        using var nulHandle = WindowsProcessInterop.CreateFileW(
            "NUL",
            WindowsProcessInterop.GenericWrite,
            WindowsProcessInterop.FileShareWrite,
            nint.Zero,
            WindowsProcessInterop.OpenExisting,
            0,
            nint.Zero);

        if (nulHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to open NUL device");
        }

        if (!WindowsProcessInterop.SetHandleInformation(nulHandle, WindowsProcessInterop.HandleFlagInherit, WindowsProcessInterop.HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to set NUL handle inheritance");
        }

        var nulRawHandle = nulHandle.DangerousGetHandle();
        var stdio = new WindowsProcessInterop.StdioHandles(
            Stdin: nint.Zero,
            Stdout: nulRawHandle,
            Stderr: nulRawHandle);

        // The detached launcher's caller-facing surface takes (predicate, additional) — translate
        // here into the single-dict shape that SpawnConsoleIsolatedProcess now expects. When the
        // caller passes neither, leave environment null so the child inherits the parent env
        // verbatim (no allocation).
        IReadOnlyDictionary<string, string?>? environment = null;
        if (shouldRemoveEnvironmentVariable is not null || additionalEnvironmentVariables is not null)
        {
            var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var key = (string)entry.Key;
                if (shouldRemoveEnvironmentVariable is null || !shouldRemoveEnvironmentVariable(key))
                {
                    resolved[key] = entry.Value as string ?? string.Empty;
                }
            }

            if (additionalEnvironmentVariables is not null)
            {
                foreach (var (key, value) in additionalEnvironmentVariables)
                {
                    resolved[key] = value;
                }
            }

            environment = resolved;
        }

        // jobHandle: null — detached children must survive a CLI crash. Anything assigned to
        // the CLI's kill-on-close job dies with the CLI, which is the opposite of what
        // `aspire start` wants.
        var pi = WindowsProcessInterop.SpawnConsoleIsolatedProcess(
            fileName,
            arguments,
            workingDirectory,
            stdio,
            environment,
            jobHandle: null);

        Process detachedProcess;
        try
        {
            detachedProcess = Process.GetProcessById(pi.dwProcessId);
        }
        finally
        {
            WindowsProcessInterop.CloseHandle(pi.hProcess);
            WindowsProcessInterop.CloseHandle(pi.hThread);
        }

        return detachedProcess;
    }
}
