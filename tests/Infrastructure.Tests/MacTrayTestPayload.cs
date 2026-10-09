// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Infrastructure.Tests;

internal static class MacTrayTestPayload
{
    internal static string Create(string workspace)
    {
        var source = Path.Combine(workspace, "source with spaces", "Aspire Tray.app");
        foreach (var file in new[]
        {
            "Contents/MacOS/aspire-tray",
            "Contents/Info.plist",
            "Contents/Resources/Aspire.icns",
            "Contents/Resources/.hidden",
            "Contents/_CodeSignature/CodeResources"
        })
        {
            var path = Path.Combine(source, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(source, "Contents/MacOS/aspire-tray"),
                UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return source;
    }
}
