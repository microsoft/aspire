// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;

namespace Aspire.Tests.Utils;

internal static class LinuxTrayTestPayload
{
    internal static void Create(string directory, string rid)
    {
        Directory.CreateDirectory(directory);
        var header = new byte[64];
        new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18), rid == "linux-arm64" ? (ushort)183 : (ushort)62);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24), 0x1000);
        var executable = Path.Combine(directory, "aspire-tray");
        File.WriteAllBytes(executable, header);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        File.WriteAllBytes(Path.Combine(directory, "Aspire.png"),
            Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg=="));
    }
}
