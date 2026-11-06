// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Diagnostics;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests.Diagnostics;

public class RollingLogFileTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Constructor_PrunesOldestAndOversizedLogsButPreservesCustomFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        for (var index = 0; index < 8; index++)
        {
            var oldPath = Path.Combine(workspace.Path, $"cli_20000101T000000_{index:x8}.log");
            File.WriteAllText(oldPath, $"old-{index}");
            File.SetLastWriteTimeUtc(oldPath, DateTime.UnixEpoch.AddSeconds(index));
        }
        var oversizedPath = Path.Combine(workspace.Path, "cli_19990101T000000_00000000.log");
        File.WriteAllText(oversizedPath, new string('x', 129));
        var customPath = Path.Combine(workspace.Path, "cli_custom.log");
        File.WriteAllText(customPath, "User-managed diagnostic file");
        var path = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);

        using (var file = new RollingLogFile(path, fileSizeLimit: 128, retainedFileCount: 5))
        {
            file.WriteLine("current");
        }

        Assert.Equal(
            Enumerable.Range(4, 4).Select(index => $"cli_20000101T000000_{index:x8}.log")
                .Append(Path.GetFileName(path)).Append("cli_custom.log").Order(),
            Directory.GetFiles(workspace.Path, "*.log").Select(Path.GetFileName).Order());
        Assert.Equal("User-managed diagnostic file", File.ReadAllText(customPath));
        Assert.All(Directory.GetFiles(workspace.Path, "*.log"), file => Assert.InRange(new FileInfo(file).Length, 0, 128));
    }

    [Fact]
    public void WriteLine_RotatesAndKeepsOnlyTwoArchives()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        using (var file = new RollingLogFile(path, fileSizeLimit: 128, retainedFileCount: 5))
        {
            for (var index = 0; index < 10; index++)
            {
                file.WriteLine($"{index}:{new string('x', 90)}");
            }
        }

        Assert.Equal(
            new[] { path, Path.ChangeExtension(path, "1.log"), Path.ChangeExtension(path, "2.log") }.Order(),
            Directory.GetFiles(workspace.Path, "*.log").Order());
        Assert.Equal($"9:{new string('x', 90)}{Environment.NewLine}", File.ReadAllText(path));
        Assert.Equal($"8:{new string('x', 90)}{Environment.NewLine}", File.ReadAllText(Path.ChangeExtension(path, "1.log")));
        Assert.Equal($"7:{new string('x', 90)}{Environment.NewLine}", File.ReadAllText(Path.ChangeExtension(path, "2.log")));
        Assert.All(Directory.GetFiles(workspace.Path, "*.log"), file => Assert.InRange(new FileInfo(file).Length, 0, 128));
    }

    [Fact]
    public void WriteLine_RotatesOnlyAfterExactByteLimit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var message = new string('x', 128 - Encoding.UTF8.Preamble.Length - Encoding.UTF8.GetByteCount(Environment.NewLine));
        using (var file = new RollingLogFile(path, fileSizeLimit: 128, retainedFileCount: 5))
        {
            file.WriteLine(message);
            Assert.Equal(128, new FileInfo(path).Length);
            Assert.Equal([path], Directory.GetFiles(workspace.Path, "*.log"));
            file.WriteLine("next");
        }

        Assert.Equal(message + Environment.NewLine, File.ReadAllText(Path.ChangeExtension(path, "1.log")));
        Assert.Equal("next" + Environment.NewLine, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("\u00e9")]
    [InlineData("\ud83d\ude00")]
    public void WriteLine_BoundsOversizedEntriesWithoutSplittingUtf8(string character)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        using (var file = new RollingLogFile(path, fileSizeLimit: 128, retainedFileCount: 5))
        {
            file.WriteLine(string.Concat(Enumerable.Repeat(character, 200)));
        }

        var bytes = File.ReadAllBytes(path);
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true).GetString(bytes);
        Assert.InRange(bytes.Length, 1, 128);
        Assert.EndsWith(Environment.NewLine + RollingLogFile.TruncationMarker + Environment.NewLine, text);
        Assert.False(text.Contains('\ufffd'));
    }

    [Fact]
    public void Constructor_NeverDeletesActiveLogsAndFailsWhenAllSlotsAreInUse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var firstPath = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var secondPath = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var nextPath = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        using var first = new RollingLogFile(firstPath, fileSizeLimit: 128, retainedFileCount: 2);
        using var second = new RollingLogFile(secondPath, fileSizeLimit: 128, retainedFileCount: 2);
        first.WriteLine("first");
        second.WriteLine("second");

        Assert.Throws<IOException>(() => new RollingLogFile(nextPath, fileSizeLimit: 128, retainedFileCount: 2));
        Assert.Equal(new[] { firstPath, secondPath }.Order(), Directory.GetFiles(workspace.Path, "*.log").Order());
        first.Dispose();
        Assert.Equal("first" + Environment.NewLine, File.ReadAllText(firstPath));
        using var next = new RollingLogFile(nextPath, fileSizeLimit: 128, retainedFileCount: 2);
        next.WriteLine("next");

        Assert.Equal(new[] { secondPath, nextPath }.Order(), Directory.GetFiles(workspace.Path, "*.log").Order());
        second.Dispose();
        Assert.Equal("second" + Environment.NewLine, File.ReadAllText(secondPath));
    }

    [Fact]
    public void ConcurrentProcesses_KeepCountAndSizeBoundedDuringCreationAndRotation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using (var first = RemoteExecutor.Invoke(WriteLogs, workspace.Path, "first"))
        using (var second = RemoteExecutor.Invoke(WriteLogs, workspace.Path, "second"))
        {
            Assert.True(SpinWait.SpinUntil(
                () => File.Exists(Path.Combine(workspace.Path, "first.ready")) && File.Exists(Path.Combine(workspace.Path, "second.ready")),
                TimeSpan.FromSeconds(30)), "Both child processes must reach the start barrier.");
            File.WriteAllText(Path.Combine(workspace.Path, "go"), string.Empty);
        }

        var files = Directory.GetFiles(workspace.Path, "*.log");
        Assert.InRange(files.Length, 1, 5);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 128));
        Assert.InRange(files.Sum(file => new FileInfo(file).Length), 1, 5 * 128);

        static void WriteLogs(string directory, string identity)
        {
            File.WriteAllText(Path.Combine(directory, $"{identity}.ready"), string.Empty);
            if (!SpinWait.SpinUntil(() => File.Exists(Path.Combine(directory, "go")), TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Timed out waiting for the concurrent log writers.");
            }
            for (var invocation = 0; invocation < 20; invocation++)
            {
                var path = FileLoggerProvider.GenerateLogFilePath(directory, TimeProvider.System);
                using var file = new RollingLogFile(path, fileSizeLimit: 128, retainedFileCount: 5);
                for (var entry = 0; entry < 4; entry++)
                {
                    file.WriteLine($"{identity}:{invocation}:{entry}:{new string('x', 80)}");
                }
            }
        }
    }

    [Fact]
    public void Rotation_PreservesOwnerOnlyUnixPermissions()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file permissions are not supported on Windows.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        File.WriteAllText(path, "existing");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using (var file = new RollingLogFile(path, fileSizeLimit: 128, retainedFileCount: 5))
        {
            file.WriteLine(new string('x', 90));
            file.WriteLine(new string('y', 90));
        }

        foreach (var file in Directory.GetFiles(workspace.Path, "*.log"))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }
}
