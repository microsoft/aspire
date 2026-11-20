// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.TestServices;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Diagnostics;

public class FileLoggerProviderTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Constructor_PrunesOldestLogFilesInLogsDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 105);
        var unrelatedPath = Path.Combine(workspace.Path, "unrelated.log");
        File.WriteAllText(unrelatedPath, "unrelated");
        var path = ManagedLogFiles.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(path, workspace.Path, errorWriter, isManagedLog: true).Dispose();

        Assert.Equal(oldPaths.Skip(6).Append(path).Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.True(File.Exists(unrelatedPath));
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_LimitsLogFilesPrunedPerRun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 300);
        var path = ManagedLogFiles.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(path, workspace.Path, errorWriter, isManagedLog: true).Dispose();

        Assert.Equal(oldPaths.Skip(100).Append(path).Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_DoesNotPruneLogFileInUse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 100);
        var errorWriter = new TestStartupErrorWriter();

        // The oldest file is active, so the next inactive file must be pruned instead.
        using (new FileStream(oldPaths[0], FileMode.Open, FileAccess.Write, FileShare.Read))
        using (var provider = new FileLoggerProvider(workspace.Path, TimeProvider.System, errorWriter))
        {
            Assert.Equal(oldPaths.Take(1).Concat(oldPaths.Skip(2)).Append(provider.LogFilePath).Order(),
                Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        }

        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_LogFilesInUseDoNotCountTowardPruneLimit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 200);
        var path = ManagedLogFiles.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        // Locked logs must not consume the deletion budget or prevent newer inactive logs from being pruned.
        var activeLogs = new List<FileStream>();
        try
        {
            foreach (var activePath in oldPaths.Take(100))
            {
                activeLogs.Add(new FileStream(activePath, FileMode.Open, FileAccess.Write, FileShare.Read));
            }

            new FileLoggerProvider(path, workspace.Path, errorWriter, isManagedLog: true).Dispose();
        }
        finally
        {
            foreach (var activeLog in activeLogs)
            {
                activeLog.Dispose();
            }
        }

        Assert.Equal(oldPaths.Take(100).Append(path).Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void CustomLogFile_OutsideLogsDirectory_DoesNotPruneLogsDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 105);
        var customPath = ManagedLogFiles.GenerateLogFilePath(workspace.CreateDirectory("custom").FullName, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(customPath, workspace.Path, errorWriter, isManagedLog: false).Dispose();

        Assert.Equal(oldPaths.Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void CustomLogFile_InLogsDirectory_DoesNotReserveTrackedLogSlot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 100);
        var customPath = Path.Combine(workspace.Path, "custom.log");
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(customPath, workspace.Path, errorWriter, isManagedLog: false).Dispose();

        Assert.Equal(oldPaths.Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.True(File.Exists(customPath));
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_PrunesOnlyTrackedLogFiles()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldestTime = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var customPath = ManagedLogFiles.GenerateLogFilePath(workspace.Path, new FakeTimeProvider(oldestTime));
        File.WriteAllText(customPath, "custom");
        File.SetLastWriteTimeUtc(customPath, oldestTime.UtcDateTime);
        var legacyPath = Path.Combine(workspace.Path, "cli_19990101T000000_12345678.log");
        File.WriteAllText(legacyPath, "legacy");
        File.SetLastWriteTimeUtc(legacyPath, oldestTime.UtcDateTime);
        var detachChildTime = oldestTime.AddMinutes(1);
        var detachChildPath = AppHostLauncher.GenerateChildLogFilePath(workspace.Path, new FakeTimeProvider(detachChildTime));
        new FileLoggerProvider(detachChildPath, logsDirectory: null, new TestStartupErrorWriter(), isManagedLog: true).Dispose();
        File.SetLastWriteTimeUtc(detachChildPath, detachChildTime.UtcDateTime);
        var copiedPath = ManagedLogFiles.GenerateLogFilePath(workspace.Path, new FakeTimeProvider(oldestTime));
        File.Copy(detachChildPath, copiedPath);
        File.SetLastWriteTimeUtc(copiedPath, oldestTime.UtcDateTime);
        var oldPaths = CreateOldLogFiles(workspace.Path, 99);
        var path = ManagedLogFiles.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(path, workspace.Path, errorWriter, isManagedLog: true).Dispose();

        // A copied log has the original filename in its marker, so it is not tracked under its new name.
        Assert.Equal(oldPaths.Append(path).Append(customPath).Append(legacyPath).Append(copiedPath).Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_ManagedLogDoesNotOverwriteExistingFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = ManagedLogFiles.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        File.WriteAllText(path, "custom");
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(path, workspace.Path, errorWriter, isManagedLog: true).Dispose();

        Assert.Equal("custom", File.ReadAllText(path));
        Assert.StartsWith("Could not create log file at ", Assert.Single(errorWriter.MarkupLines));
    }

    [Fact]
    public void Constructor_PreservesManagedLogReusedAsCustomFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 100);
        var customPath = oldPaths[0];
        using (var customLog = new FileLoggerProvider(customPath, new TestStartupErrorWriter()))
        {
            customLog.WriteLog("custom");
        }

        var path = ManagedLogFiles.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        new FileLoggerProvider(path, workspace.Path, new TestStartupErrorWriter(), isManagedLog: true).Dispose();

        Assert.Equal("custom" + Environment.NewLine, File.ReadAllText(customPath));
        Assert.Equal(oldPaths.Append(path).Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
    }

    [Fact]
    public void Constructor_DoesNotPruneLogFileHeldByAnotherProcess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 100);
        var readyPath = Path.Combine(workspace.Path, "ready");
        var releasePath = Path.Combine(workspace.Path, "release");

        using var result = RemoteExecutor.Invoke(static (activePath, readyPath, releasePath) =>
        {
            using var activeLog = new FileStream(activePath, FileMode.Open, FileAccess.Write, FileShare.Read);
            File.WriteAllText(readyPath, string.Empty);

            if (!SpinWait.SpinUntil(() => File.Exists(releasePath), TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("Timed out waiting for the parent process to release the log holder.");
            }
        }, oldPaths[0], readyPath, releasePath);

        try
        {
            Assert.True(
                SpinWait.SpinUntil(() => File.Exists(readyPath), TimeSpan.FromSeconds(30)),
                "Timed out waiting for the child process to open the log.");

            var path = ManagedLogFiles.GenerateLogFilePath(workspace.Path, TimeProvider.System);
            var errorWriter = new TestStartupErrorWriter();
            new FileLoggerProvider(path, workspace.Path, errorWriter, isManagedLog: true).Dispose();

            Assert.Equal(oldPaths.Take(1).Concat(oldPaths.Skip(2)).Append(path).Order(),
                Directory.GetFiles(workspace.Path, "cli_*.log").Order());
            Assert.Empty(errorWriter.MarkupLines);
        }
        finally
        {
            File.WriteAllText(releasePath, string.Empty);
        }
    }

    [Fact]
    public void Constructor_DoesNotPruneOrFollowSymbolicLinks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logsDirectory = workspace.CreateDirectory("logs").FullName;
        var oldestTime = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var linkPath = ManagedLogFiles.GenerateLogFilePath(logsDirectory, new FakeTimeProvider(oldestTime));
        // The target carries a valid marker for the link's name, so only the reparse-point check protects it.
        var targetPath = Path.Combine(workspace.Path, "outside.log");
        File.WriteAllText(targetPath, $"# Aspire CLI managed diagnostic log: {Path.GetFileName(linkPath)}{Environment.NewLine}sentinel");
        File.SetLastWriteTimeUtc(targetPath, oldestTime.UtcDateTime);
        TestSymlinkHelper.TryCreateSymlink(linkPath, targetPath, isDirectory: false);
        var oldPaths = CreateOldLogFiles(logsDirectory, 100);
        var path = ManagedLogFiles.GenerateLogFilePath(logsDirectory, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(path, logsDirectory, errorWriter, isManagedLog: true).Dispose();

        Assert.Equal(oldPaths.Skip(1).Append(linkPath).Append(path).Order(), Directory.GetFiles(logsDirectory, "cli_*.log").Order());
        Assert.EndsWith("sentinel", File.ReadAllText(targetPath));
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_AccessDenied_WarnsAndDisablesLogging()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = workspace.CreateDirectory("not-a-log-file").FullName;
        var errorWriter = new TestStartupErrorWriter();

        using (var provider = new FileLoggerProvider(path, errorWriter))
        {
            provider.WriteLog("Must not be persisted");
        }

        Assert.StartsWith("Could not create log file at ", Assert.Single(errorWriter.MarkupLines));
        Assert.Empty(Directory.GetFileSystemEntries(path));
    }

    [Fact]
    public void Constructor_UnwritableExistingFile_PreservesContentsAndWarns()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file permissions are not supported on Windows.");
        Assert.SkipWhen(Environment.UserName == "root", "Root bypasses Unix write permissions.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "test.log");
        const string contents = "Previous log contents";
        File.WriteAllText(path, contents);
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        try
        {
            var errorWriter = new TestStartupErrorWriter();
            using (var provider = new FileLoggerProvider(path, errorWriter))
            {
                provider.WriteLog("Must not be persisted");
            }

            Assert.StartsWith("Could not create log file at ", Assert.Single(errorWriter.MarkupLines));
            Assert.Equal(contents, File.ReadAllText(path));
            Assert.Equal(UnixFileMode.UserRead, File.GetUnixFileMode(path));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_CreatesPrivateLogFilesAndDirectories(bool explicitLogFilePath)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file permissions are not supported on Windows.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logsDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "logs");
        var errorWriter = new TestStartupErrorWriter();
        using var provider = explicitLogFilePath
            ? new FileLoggerProvider(Path.Combine(logsDirectory, "test.log"), errorWriter)
            : new FileLoggerProvider(logsDirectory, TimeProvider.System, errorWriter);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(logsDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(provider.LogFilePath));
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute)]
    public void Constructor_RestrictsExistingLogFileBeforeWriting(UnixFileMode existingMode)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file permissions are not supported on Windows.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "test.log");
        File.WriteAllText(path, "Previous log contents");
        File.SetUnixFileMode(path, existingMode);
        var errorWriter = new TestStartupErrorWriter();
        using (var provider = new FileLoggerProvider(path, errorWriter))
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            provider.WriteLog("New log entry");
        }

        Assert.Equal("New log entry" + Environment.NewLine, File.ReadAllText(path));
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Constructor_RepairsOwnedDirectoryAndPreservesCustomDirectoryPermissions(bool explicitLogFilePath)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file permissions are not supported on Windows.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logsDirectory = workspace.CreateDirectory("logs").FullName;
        var existingMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(logsDirectory, existingMode);

        using var provider = explicitLogFilePath
            ? new FileLoggerProvider(Path.Combine(logsDirectory, "test.log"), new TestStartupErrorWriter())
            : new FileLoggerProvider(logsDirectory, TimeProvider.System, new TestStartupErrorWriter());

        var expectedMode = explicitLogFilePath
            ? existingMode
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Assert.Equal(expectedMode, File.GetUnixFileMode(logsDirectory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(provider.LogFilePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateLoggerFactory_RepairsOnlyDedicatedLogDirectory(bool customDirectory)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file permissions are not supported on Windows.");
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logsDirectory = Path.Combine(workspace.Path, "logs");
        var parentDirectory = workspace.CreateDirectory(customDirectory ? "custom" : "logs").FullName;
        var originalMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(parentDirectory, originalMode);
        var logPath = Path.Combine(parentDirectory, "test.log");
        var options = new Program.CliLoggingOptions(null, false, logsDirectory, logPath, IsManagedLogFile: false);
        var errorWriter = new TestStartupErrorWriter();

        var (loggerFactory, provider) = Program.CreateLoggerFactory([], options, errorWriter, new ConsoleLogBufferContext());
        using (loggerFactory)
        using (provider)
        {
            var expectedMode = customDirectory
                ? originalMode
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(expectedMode, File.GetUnixFileMode(parentDirectory));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(logPath));
            Assert.Empty(errorWriter.MarkupLines);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteLog_FlushesOnDispose(bool existingFile)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test.log");
        if (existingFile)
        {
            File.WriteAllText(logFilePath, "Previous log contents");
        }

        var errorWriter = new TestStartupErrorWriter();
        using (var provider = new FileLoggerProvider(logFilePath, errorWriter))
        {
            provider.WriteLog("First log entry");
            provider.WriteLog("Second log entry");
        }

        Assert.Equal(["First log entry", "Second log entry"], File.ReadAllLines(logFilePath));
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Theory]
    [InlineData(false, false, null, false, false, true)]
    [InlineData(true, false, null, false, false, false)]
    [InlineData(true, false, LogLevel.Information, false, false, false)]
    [InlineData(true, false, LogLevel.Warning, false, false, true)]
    [InlineData(true, false, LogLevel.Error, false, false, true)]
    [InlineData(true, false, LogLevel.Debug, true, false, true)]
    [InlineData(true, false, null, false, true, true)]
    [InlineData(true, true, null, false, false, true)]
    public void Dispose_DiscardsManagedLogOnlyWhenNotRetainedOrCanceled(bool discardOnDispose, bool cancelAfterMarking, LogLevel? loggedLevel, bool logException, bool retain, bool expectedExists)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logFilePath = ManagedLogFiles.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();
        using var cts = new CancellationTokenSource();

        using (var provider = new FileLoggerProvider(logFilePath, logsDirectory: null, errorWriter, isManagedLog: true))
        {
            if (discardOnDispose)
            {
                provider.DiscardLogFileOnDisposeUnlessRetained(cts.Token);
            }

            if (cancelAfterMarking)
            {
                cts.Cancel();
            }

            if (loggedLevel is { } level)
            {
                provider.CreateLogger("Test").Log(level, logException ? new InvalidOperationException("Handled") : null, "Log entry");
            }

            if (retain)
            {
                provider.RetainLogFile();
            }
        }

        Assert.Equal(expectedExists, File.Exists(logFilePath));
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Dispose_DoesNotDiscardCustomLogFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test.log");
        var errorWriter = new TestStartupErrorWriter();

        using (var provider = new FileLoggerProvider(logFilePath, errorWriter))
        {
            provider.DiscardLogFileOnDisposeUnlessRetained(CancellationToken.None);
        }

        Assert.True(File.Exists(logFilePath));
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Dispose_DoesNotDiscardManagedLogPathItFailedToCreate()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logFilePath = ManagedLogFiles.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        File.WriteAllText(logFilePath, "Existing file");
        var errorWriter = new TestStartupErrorWriter();

        using (var provider = new FileLoggerProvider(logFilePath, logsDirectory: null, errorWriter, isManagedLog: true))
        {
            provider.DiscardLogFileOnDisposeUnlessRetained(CancellationToken.None);
        }

        Assert.Equal("Existing file", File.ReadAllText(logFilePath));
        Assert.StartsWith("Could not create log file at ", Assert.Single(errorWriter.MarkupLines));
    }

    private static List<string> CreateOldLogFiles(string directory, int count)
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var paths = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var path = ManagedLogFiles.GenerateLogFilePath(directory, timeProvider);
            new FileLoggerProvider(path, logsDirectory: null, new TestStartupErrorWriter(), isManagedLog: true).Dispose();
            File.SetLastWriteTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime);
            paths.Add(path);
            timeProvider.Advance(TimeSpan.FromMinutes(1));
        }

        return paths;
    }
}
