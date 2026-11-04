// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using Aspire.Tray.Tests.Helpers;

namespace Aspire.Tray.Tests;

public class MacTrayLauncherTests
{
    public static bool SupportsMac => OperatingSystem.IsMacOS();

    [Fact(Skip = "Uses Darwin file descriptors and spawn.", SkipUnless = nameof(SupportsMac))]
    [SupportedOSPlatform("macos")]
    public async Task DetachedChildAppendsToProtectedLogAndHasItsOwnProcessGroup()
    {
        using var directory = new TestMacTrayLogDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(directory.LogPath)!);
        await File.WriteAllTextAsync(directory.LogPath, "existing\n");
        using var log = MacTrayLog.Open(directory.LogPath);
        var start = CreateCommand(directory.Root, "printf 'stdout\\n'; printf 'stderr\\n' >&2; /bin/ps -o pgid= -p $$");
        var child = MacDetachedProcess.Start(start, log);
        var id = child.ProcessId;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            await child.TerminateAndWaitAsync();
        }

        var lines = await File.ReadAllLinesAsync(directory.LogPath);
        Assert.Equal(4, lines.Length);
        Assert.Equal(["existing", "stdout", "stderr"], lines[..3]);
        Assert.Equal(id, int.Parse(lines[3], CultureInfo.InvariantCulture));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(directory.LogPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.GetDirectoryName(directory.LogPath)!));
    }

    [Theory(Skip = "Uses Darwin no-follow opens.", SkipUnless = nameof(SupportsMac))]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("macos")]
    public async Task ExistingFileAndParentSymlinksAreRejectedWithoutWritingTargets(bool parentLink)
    {
        using var directory = new TestMacTrayLogDirectory();
        var targetDirectory = Directory.CreateDirectory(Path.Combine(directory.Root, "target")).FullName;
        var target = Path.Combine(targetDirectory, "tray.log");
        await File.WriteAllTextAsync(target, "untouched");
        if (parentLink)
        {
            Directory.CreateSymbolicLink(Path.GetDirectoryName(directory.LogPath)!, targetDirectory);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(directory.LogPath)!);
            File.CreateSymbolicLink(directory.LogPath, target);
        }

        Assert.Throws<IOException>(() => MacTrayLog.Open(directory.LogPath));
        Assert.Equal("untouched", await File.ReadAllTextAsync(target));
    }

    [Fact(Skip = "Uses Darwin no-follow opens.", SkipUnless = nameof(SupportsMac))]
    [SupportedOSPlatform("macos")]
    public async Task HardLinkedLogIsRejectedWithoutChangingTheTarget()
    {
        using var directory = new TestMacTrayLogDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(directory.LogPath)!);
        var target = Path.Combine(directory.Root, "target");
        await File.WriteAllTextAsync(target, "untouched");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var result = await TestNativeCommand.RunAsync("/bin/ln", target, directory.LogPath);
        Assert.Equal(0, result.ExitCode);

        Assert.Throws<IOException>(() => MacTrayLog.Open(directory.LogPath));
        Assert.Equal("untouched", await File.ReadAllTextAsync(target));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(target));
    }

    [Fact(Skip = "Uses Darwin nonblocking opens.", SkipUnless = nameof(SupportsMac))]
    [SupportedOSPlatform("macos")]
    public async Task FifoLogIsRejectedWithoutWaitingForAReader()
    {
        using var directory = new TestMacTrayLogDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(directory.LogPath)!);
        var result = await TestNativeCommand.RunAsync("/usr/bin/mkfifo", directory.LogPath);
        Assert.Equal(0, result.ExitCode);

        Assert.Throws<IOException>(() => MacTrayLog.Open(directory.LogPath));
    }

    [Fact(Skip = "Uses Darwin descriptor inheritance.", SkipUnless = nameof(SupportsMac))]
    [SupportedOSPlatform("macos")]
    public async Task DetachedChildCannotInheritParentLeaseDescriptors()
    {
        using var directory = new TestMacTrayLogDirectory();
        var leasePath = Path.Combine(directory.Root, "parent.lease");
        var sentinel = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(leasePath, sentinel + "\n");
        using var lease = File.OpenHandle(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        using var inheritable = TestMacProcessInterop.DuplicateInheritable(lease);
        using var log = MacTrayLog.Open(directory.LogPath);
        var descriptor = inheritable.DangerousGetHandle().ToInt32();
        // Darwin /dev/fd entries have their own stat identity. Read a unique sentinel
        // instead, and prove the probe detects an open descriptor before testing spawn.
        var command = $"if {{ IFS= read -r value < /dev/fd/{descriptor}; }} 2>/dev/null && [ \"$value\" = \"$LEASE_SENTINEL\" ]; then printf inherited; else printf isolated; fi";
        var control = await TestNativeCommand.RunAsync("/bin/sh", "-c",
            $"LEASE_SENTINEL=$2; exec {descriptor}< \"$1\"; {command}", "--", leasePath, sentinel);
        Assert.Equal((0, "inherited", ""), control);
        var start = CreateCommand(directory.Root, command);
        start.Environment["LEASE_SENTINEL"] = sentinel;
        var child = MacDetachedProcess.Start(start, log);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            await child.TerminateAndWaitAsync();
        }
        Assert.Equal("isolated", await File.ReadAllTextAsync(directory.LogPath));
    }

    [Theory(Skip = "Uses Darwin descriptor inheritance.", SkipUnless = nameof(SupportsMac))]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("macos")]
    public async Task ReplacingLogOrParentAfterOpenCannotRedirectChildOutput(bool replaceParent)
    {
        using var directory = new TestMacTrayLogDirectory();
        var targetDirectory = Directory.CreateDirectory(Path.Combine(directory.Root, "target")).FullName;
        var target = Path.Combine(targetDirectory, "tray.log");
        await File.WriteAllTextAsync(target, "untouched");
        using var log = MacTrayLog.Open(directory.LogPath);
        string retained;
        if (replaceParent)
        {
            var moved = Path.Combine(directory.Root, "retained");
            Directory.Move(Path.GetDirectoryName(directory.LogPath)!, moved);
            retained = Path.Combine(moved, "tray.log");
            Directory.CreateSymbolicLink(Path.GetDirectoryName(directory.LogPath)!, targetDirectory);
        }
        else
        {
            retained = Path.Combine(directory.Root, "retained.log");
            File.Move(directory.LogPath, retained);
            File.CreateSymbolicLink(directory.LogPath, target);
        }

        var child = MacDetachedProcess.Start(CreateCommand(directory.Root, "printf stdout; printf stderr >&2"), log);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await child.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            await child.TerminateAndWaitAsync();
        }
        Assert.Equal("stdoutstderr", await File.ReadAllTextAsync(retained));
        Assert.Equal("untouched", await File.ReadAllTextAsync(target));
    }

    [Theory(Skip = "Uses Darwin child ownership.", SkipUnless = nameof(SupportsMac))]
    [InlineData(false)]
    [InlineData(true)]
    [SupportedOSPlatform("macos")]
    public async Task FailedReadinessTerminatesAndReapsOnlyTheExactChild(bool rejected)
    {
        using var directory = new TestMacTrayLogDirectory();
        using var log = MacTrayLog.Open(directory.LogPath);
        using var unrelated = Process.Start("/bin/sleep", "60")!;
        var child = MacDetachedProcess.Start(CreateCommand(directory.Root, "exec /bin/sleep 60"), log);
        Exception failure = rejected ? new InvalidOperationException("rejected") : new TimeoutException("timed out");
        try
        {
            Assert.Equal(child.ProcessId, TestMacProcessInterop.GetSessionId(child.ProcessId));
            var actual = await Assert.ThrowsAsync(failure.GetType(),
                () => child.CompleteStartupAsync(() => Task.FromException(failure)));
            Assert.Same(failure, actual);
            Assert.Equal(0, child.ProcessId);
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            await child.TerminateAndWaitAsync();
            await CliProcess.TerminateOwnedChildAsync(unrelated);
        }
    }

    [Fact(Skip = "Uses Darwin child ownership.", SkipUnless = nameof(SupportsMac))]
    [SupportedOSPlatform("macos")]
    public async Task FailedReadinessCanReapAnAlreadyExitedChild()
    {
        using var directory = new TestMacTrayLogDirectory();
        using var log = MacTrayLog.Open(directory.LogPath);
        var child = MacDetachedProcess.Start(CreateCommand(directory.Root, "exit 0"), log);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => child.CompleteStartupAsync(async () =>
            {
                // The callback models an IPC failure independent of whether exit preceded it.
                await Task.Yield();
                throw new IOException("disconnected");
            }));
            Assert.Equal(0, child.ProcessId);
        }
        finally
        {
            await child.TerminateAndWaitAsync();
        }
    }

    private static ProcessStartInfo CreateCommand(string workingDirectory, string command)
    {
        var start = new ProcessStartInfo("/bin/sh") { WorkingDirectory = workingDirectory };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(command);
        return start;
    }
}
