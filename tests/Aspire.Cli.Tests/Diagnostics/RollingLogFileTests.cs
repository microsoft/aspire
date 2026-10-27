// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Diagnostics;
using Microsoft.DotNet.RemoteExecutor;

namespace Aspire.Cli.Tests.Diagnostics;

public class RollingLogFileTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ReopeningPreservesPreviousEntries()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "shared.log");
        using (var first = new RollingLogFile(path, 256))
        {
            await first.WriteLineAsync("First");
        }
        using (var second = new RollingLogFile(path, 256))
        {
            await second.WriteLineAsync("Second");
        }

        Assert.Equal(["First", "Second"], File.ReadAllLines(path));
        Assert.Single(Directory.GetFiles(workspace.Path));
    }

    [Fact]
    public async Task RotationBoundsFileCountAndRetainsTheNewestEntries()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "shared.log");
        var entries = Enumerable.Range(0, 8).Select(index => $"{index}:" + new string('x', 200)).ToArray();
        using (var writer = new RollingLogFile(path, 256))
        {
            foreach (var entry in entries)
            {
                await writer.WriteLineAsync(entry);
            }
        }

        Assert.Equal(["shared.1.log", "shared.2.log", "shared.log"],
            Directory.GetFiles(workspace.Path).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal([entries[5], entries[6], entries[7]],
            File.ReadAllLines(Path.ChangeExtension(path, "2.log"))
                .Concat(File.ReadAllLines(Path.ChangeExtension(path, "1.log")))
                .Concat(File.ReadAllLines(path)));
        Assert.All(Directory.GetFiles(workspace.Path), file => Assert.InRange(new FileInfo(file).Length, 1, 256));
    }

    [Fact]
    public async Task ExactSizeBoundaryDoesNotRotateEarly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "shared.log");
        var entry = new string('x', 256 - Encoding.UTF8.GetByteCount(Environment.NewLine));
        using (var writer = new RollingLogFile(path, 256))
        {
            await writer.WriteLineAsync(entry);
            Assert.Equal(256, new FileInfo(path).Length);
            Assert.Single(Directory.GetFiles(workspace.Path));

            await writer.WriteLineAsync("Next entry");
        }

        Assert.Equal([entry], File.ReadAllLines(Path.ChangeExtension(path, "1.log")));
        Assert.Equal(["Next entry"], File.ReadAllLines(path));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("\u00e9")]
    [InlineData("\ud83d\ude00")]
    public async Task OversizedEntriesAreBoundedAndRemainValidUtf8(string character)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "shared.log");
        using (var writer = new RollingLogFile(path, 256))
        {
            await writer.WriteLineAsync(string.Concat(Enumerable.Repeat(character, 1024)));
        }

        var bytes = File.ReadAllBytes(path);
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        Assert.InRange(bytes.Length, 1, 256);
        Assert.EndsWith(RollingLogFile.TruncationMarker, text);
        Assert.StartsWith(character, text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentProcessesAppendCompleteEntriesAndCoordinateRotation(bool rotate)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "shared.log");
        var limit = rotate ? 256 : 1024 * 1024;
        using (RemoteExecutor.Invoke(static (path, limit) =>
        {
            using var writer = new RollingLogFile(path, int.Parse(limit, System.Globalization.CultureInfo.InvariantCulture));
            for (var index = 0; index < 200; index++)
            {
                writer.WriteLineAsync($"first:{index:D3}").GetAwaiter().GetResult();
            }
            return RemoteExecutor.SuccessExitCode;
        }, path, limit.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        using (RemoteExecutor.Invoke(static (path, limit) =>
        {
            using var writer = new RollingLogFile(path, int.Parse(limit, System.Globalization.CultureInfo.InvariantCulture));
            for (var index = 0; index < 200; index++)
            {
                writer.WriteLineAsync($"second:{index:D3}").GetAwaiter().GetResult();
            }
            return RemoteExecutor.SuccessExitCode;
        }, path, limit.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
        }

        var files = Directory.GetFiles(workspace.Path);
        Assert.InRange(files.Length, 1, 3);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, limit));
        var entries = files.SelectMany(File.ReadAllLines).ToArray();
        var expected = Enumerable.Range(0, 200).SelectMany(index => new[] { $"first:{index:D3}", $"second:{index:D3}" }).ToHashSet();
        Assert.NotEmpty(entries);
        Assert.Equal(entries.Length, entries.Distinct().Count());
        Assert.Subset(expected, entries.ToHashSet());
        if (!rotate)
        {
            Assert.Equal(expected.Order(), entries.Order());
        }
    }

    [Fact]
    public async Task SharedLogsAndArchivesRemainPrivateOnUnix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file permissions are not supported on Windows.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "shared.log");
        File.WriteAllText(path, "Earlier entry" + Environment.NewLine);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using (var writer = new RollingLogFile(path, 256))
        {
            await writer.WriteLineAsync(new string('x', 200));
            await writer.WriteLineAsync(new string('y', 200));
        }

        Assert.Equal(2, Directory.GetFiles(workspace.Path).Length);
        foreach (var file in Directory.GetFiles(workspace.Path))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
    }

    [Fact]
    public void LimitMustAllowAMessageCharacterAndTheTruncationMarker()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, "shared.log");
        var limit = Encoding.UTF8.GetByteCount(RollingLogFile.TruncationMarker);

        Assert.Throws<ArgumentOutOfRangeException>(() => new RollingLogFile(path, limit));
        Assert.Empty(Directory.GetFileSystemEntries(workspace.Path));
    }
}
