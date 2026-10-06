// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection.PortableExecutable;

namespace Aspire.Shared;

/// <summary>
/// Validates the Windows tray payload before packaging and before launching it.
/// </summary>
internal static class WindowsTrayPayload
{
    internal const string ExecutableName = "aspire-tray.exe";
    internal const string IconName = "Aspire.ico";
    internal const string ExecutablePath = "tray/aspire-tray.exe";

    internal static void Validate(string directory, string rid)
    {
        var machine = rid switch
        {
            "win-x64" => Machine.Amd64,
            "win-arm64" => Machine.Arm64,
            _ => throw new InvalidDataException($"Unsupported Windows tray RID: {rid}.")
        };

        foreach (var name in new[] { ExecutableName, IconName })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path) || new FileInfo(path).Length == 0)
            {
                throw new InvalidDataException($"Required Windows tray payload is missing or empty: {path}.");
            }
        }

        // Read the PE/COFF machine and optional header, not the directory name: a
        // stale or overridden publish directory can contain another architecture.
        // https://learn.microsoft.com/windows/win32/debug/pe-format
        using var stream = File.OpenRead(Path.Combine(directory, ExecutableName));
        using var reader = new PEReader(stream);
        var headers = reader.PEHeaders;
        if (headers.CoffHeader.Machine != machine ||
            headers.PEHeader?.Magic != PEMagic.PE32Plus ||
            headers.PEHeader.Subsystem != Subsystem.WindowsGui ||
            headers.CorHeader is not null ||
            (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0)
        {
            throw new InvalidDataException($"Windows tray payload must be a native WinExe for {rid}.");
        }
    }
}
