// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Spike.Tests.Helpers;

internal sealed class TestTrayBundle : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-tray-bundle-");

    public string Root { get; }
    public string CliPath { get; }
    public string AppPath => Path.Combine(Root, "tray", "Aspire Tray.app");

    public TestTrayBundle()
    {
        Root = Path.Combine(_directory.FullName, "layout with spaces");
        CliPath = Path.Combine(_directory.FullName, "aspire cli");
        Directory.CreateDirectory(Path.Combine(AppPath, "Contents", "MacOS"));
        File.WriteAllText(CliPath, "");
        File.WriteAllText(Path.Combine(AppPath, "Contents", "Info.plist"), "");
        File.WriteAllText(Path.Combine(AppPath, "Contents", "MacOS", "aspire-tray"), "");
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
