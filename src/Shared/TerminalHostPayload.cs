// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;

namespace Aspire.Shared;

/// <summary>
/// Required runtime assets for the standalone terminal host.
/// </summary>
internal static class TerminalHostPayload
{
    public static string[] GetRequiredFiles(string rid) => rid switch
    {
        "win-x64" or "win-arm64" => ["Aspire.TerminalHost.exe", .. GetWindowsPtyFiles(rid)],
        "osx-x64" or "osx-arm64" => ["Aspire.TerminalHost", "libhex1binterop.dylib"],
        "linux-x64" or "linux-arm64" or "linux-musl-x64" => ["Aspire.TerminalHost", "libhex1binterop.so"],
        _ => throw new ArgumentException($"Unsupported terminal host runtime '{rid}'.", nameof(rid))
    };

    public static string[] GetWindowsPtyFiles(string rid) => rid switch
    {
        // ConPTY selects OpenConsole by OS architecture, including x64 processes on ARM64 Windows.
        "win-x64" => ["hex1bpty.exe", "conpty.dll", "x64/OpenConsole.exe", "arm64/OpenConsole.exe"],
        "win-arm64" => ["hex1bpty.exe", "conpty.dll", "arm64/OpenConsole.exe"],
        _ => []
    };

    public static bool IsValid(string directory)
        => IsValid(directory, RuntimeInformation.RuntimeIdentifier);

    public static bool IsValid(string directory, string rid)
        => GetRequiredFiles(rid).All(file => File.Exists(Path.Combine(directory, file)) && new FileInfo(Path.Combine(directory, file)).Length > 0);
}
