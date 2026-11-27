// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Maui.Utilities;

namespace Aspire.Hosting.Maui.Tests;

public class MauiAndroidLaunchSignalTests
{
    [Fact]
    public void TryRead_ReadsDeviceAndPackage()
    {
        using var directory = new SignalDirectory();
        var path = Path.Combine(directory.Path, "launch-signal.txt");
        File.WriteAllLines(path,
        [
            "adb-tool-path=/sdk/platform-tools",
            "adb-tool-exe=adb",
            "adb-target=-s emulator-5554",
            "package=com.example.app"
        ]);

        var signal = MauiAndroidLaunchSignal.TryRead(path);

        Assert.NotNull(signal);
        Assert.Equal(Path.Combine("/sdk/platform-tools", "adb"), signal.AdbPath);
        Assert.Equal(["-s", "emulator-5554"], signal.AdbTargetArguments);
        Assert.Equal("com.example.app", signal.PackageName);
        Assert.Equal(["-s", "emulator-5554", "shell", "am", "force-stop", "com.example.app"], signal.GetForceStopArguments());
    }

    [Fact]
    public void TryRead_WithoutAdbToolValuesOrTarget_UsesAdbFromPath()
    {
        using var directory = new SignalDirectory();
        var path = Path.Combine(directory.Path, "launch-signal.txt");
        File.WriteAllLines(path,
        [
            "adb-tool-path=",
            "adb-tool-exe=",
            "adb-target=",
            "package=com.example.app"
        ]);

        var signal = MauiAndroidLaunchSignal.TryRead(path);

        Assert.NotNull(signal);
        Assert.Equal(OperatingSystem.IsWindows() ? "adb.exe" : "adb", signal.AdbPath);
        Assert.Empty(signal.AdbTargetArguments);
        Assert.Equal(["shell", "am", "force-stop", "com.example.app"], signal.GetForceStopArguments());
    }

    [Fact]
    public void TryRead_WithoutPackage_ReturnsNull()
    {
        using var directory = new SignalDirectory();
        var path = Path.Combine(directory.Path, "launch-signal.txt");
        File.WriteAllLines(path, ["adb-target=-e", "package="]);

        Assert.Null(MauiAndroidLaunchSignal.TryRead(path));
    }

    [Fact]
    public void TryRead_MissingFile_ReturnsNull()
    {
        using var directory = new SignalDirectory();

        Assert.Null(MauiAndroidLaunchSignal.TryRead(Path.Combine(directory.Path, "launch-signal.txt")));
    }

    private sealed class SignalDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("aspire-maui-launch-signal").FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
