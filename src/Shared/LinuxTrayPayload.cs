// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;

namespace Aspire.Shared;

/// <summary>
/// Validates native Linux tray artifacts before packaging and launching.
/// </summary>
internal static class LinuxTrayPayload
{
    internal const string ExecutableName = "aspire-tray";
    internal const string IconName = "Aspire.png";
    internal const string ExecutablePath = "tray/aspire-tray";

    internal static bool IsNativeExecutable(string path, ushort? machine)
    {
        if (File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
        {
            return false;
        }
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[64];
        if (stream.Read(header) != header.Length)
        {
            return false;
        }
        // ELF64 little-endian ET_EXEC or PIE (ET_DYN), with a nonzero entry point.
        // https://refspecs.linuxfoundation.org/elf/gabi4+/ch4.eheader.html
        var actualMachine = BinaryPrimitives.ReadUInt16LittleEndian(header[18..]);
        return header[..7].SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1 })
            && BinaryPrimitives.ReadUInt16LittleEndian(header[16..]) is 2 or 3
            && actualMachine is 62 or 183 && (machine is null || actualMachine == machine)
            && BinaryPrimitives.ReadUInt64LittleEndian(header[24..]) != 0;
    }

    internal static void Validate(string directory, string rid)
    {
        ushort machine = rid switch
        {
            "linux-x64" => 62,
            "linux-arm64" => 183,
            _ => throw new InvalidDataException($"Unsupported Linux tray RID: {rid}.")
        };
        var executable = Path.Combine(directory, ExecutableName);
        if (!File.Exists(executable) || !IsNativeExecutable(executable, machine))
        {
            throw new InvalidDataException($"Linux tray payload must be a native executable for {rid}.");
        }
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(executable) & UnixFileMode.UserExecute) == 0)
        {
            throw new InvalidDataException("The Linux tray payload is not executable.");
        }
        using var icon = File.OpenRead(Path.Combine(directory, IconName));
        Span<byte> signature = stackalloc byte[8];
        icon.ReadExactly(signature);
        if (!signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            throw new InvalidDataException("The Linux tray icon must be a PNG.");
        }
    }
}
