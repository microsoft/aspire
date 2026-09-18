// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection.PortableExecutable;

namespace Aspire.Tests.Utils;

internal static class WindowsTrayTestPayload
{
    internal static void Create(string directory, string rid)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Aspire.ico"), "test icon");
        using var stream = File.Create(Path.Combine(directory, "aspire-tray.exe"));
        using var writer = new BinaryWriter(stream);
        // Minimal PE32+ headers for payload validation, not an executable program.
        // e_lfanew=0x80, COFF optional header size=240, Windows GUI subsystem=2.
        stream.SetLength(512);
        writer.Write((ushort)0x5a4d);
        stream.Position = 0x3c;
        writer.Write(0x80);
        stream.Position = 0x80;
        writer.Write(0x4550);
        writer.Write((ushort)(rid == "win-arm64" ? Machine.Arm64 : Machine.Amd64));
        stream.Position = 0x80 + 20;
        writer.Write((ushort)240);
        writer.Write((ushort)Characteristics.ExecutableImage);
        writer.Write((ushort)PEMagic.PE32Plus);
        stream.Position = 0x80 + 24 + 68;
        writer.Write((ushort)Subsystem.WindowsGui);
        stream.Position = 0x80 + 24 + 108;
        writer.Write(16);
    }
}
