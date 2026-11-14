// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.TestServices;
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
        var path = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(path, workspace.Path, errorWriter).Dispose();

        Assert.Equal(oldPaths.Skip(6).Append(path).Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.True(File.Exists(unrelatedPath));
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_LimitsLogFilesPrunedPerRun()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 300);
        var path = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(path, workspace.Path, errorWriter).Dispose();

        Assert.Equal(oldPaths.Skip(100).Append(path).Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_DoesNotPruneLogFileInUse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 100);
        var errorWriter = new TestStartupErrorWriter();

        // Hold the oldest file open the same way an active CLI writer does, without changing its
        // last write time, so it remains the only prune candidate.
        using (new FileStream(oldPaths[0], FileMode.Open, FileAccess.Write, FileShare.Read))
        using (new FileLoggerProvider(workspace.Path, TimeProvider.System, errorWriter))
        {
            Assert.All(oldPaths, path => Assert.True(File.Exists(path)));
        }

        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_LogFilesInUseDoNotCountTowardPruneLimit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 200);
        var path = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        // Hold the 100 oldest logs open the same way active CLI writers do. They fill a whole run's
        // prune limit, so the inactive log behind them is only pruned if skipped files are not counted.
        var activeLogs = new List<FileStream>();
        try
        {
            foreach (var activePath in oldPaths.Take(100))
            {
                activeLogs.Add(new FileStream(activePath, FileMode.Open, FileAccess.Write, FileShare.Read));
            }

            new FileLoggerProvider(path, workspace.Path, errorWriter).Dispose();
        }
        finally
        {
            foreach (var activeLog in activeLogs)
            {
                activeLog.Dispose();
            }
        }

        Assert.Equal(oldPaths.Take(100).Concat(oldPaths.Skip(101)).Append(path).Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void CustomLogFile_OutsideLogsDirectory_DoesNotPruneLogsDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldPaths = CreateOldLogFiles(workspace.Path, 105);
        var customPath = FileLoggerProvider.GenerateLogFilePath(workspace.CreateDirectory("custom").FullName, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(customPath, workspace.Path, errorWriter).Dispose();

        Assert.Equal(oldPaths.Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
        Assert.Empty(errorWriter.MarkupLines);
    }

    [Fact]
    public void Constructor_PrunesOnlyGeneratedLogFileNames()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var oldestTime = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var customPath = Path.Combine(workspace.Path, "cli_support.log");
        File.WriteAllText(customPath, "custom");
        File.SetLastWriteTimeUtc(customPath, oldestTime.UtcDateTime);
        var detachChildTime = oldestTime.AddMinutes(1);
        var detachChildPath = AppHostLauncher.GenerateChildLogFilePath(workspace.Path, new FakeTimeProvider(detachChildTime));
        File.WriteAllText(detachChildPath, "old");
        File.SetLastWriteTimeUtc(detachChildPath, detachChildTime.UtcDateTime);
        var oldPaths = CreateOldLogFiles(workspace.Path, 99);
        var path = FileLoggerProvider.GenerateLogFilePath(workspace.Path, TimeProvider.System);
        var errorWriter = new TestStartupErrorWriter();

        new FileLoggerProvider(path, workspace.Path, errorWriter).Dispose();

        // The custom file is the oldest, but it is neither deleted nor counted, so only the
        // detach-child log is pruned to make room for the new file.
        Assert.Equal(oldPaths.Append(path).Append(customPath).Order(), Directory.GetFiles(workspace.Path, "cli_*.log").Order());
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
        var options = new Program.CliLoggingOptions(null, false, logsDirectory, logPath);
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
    [InlineData(false, null, true)]
    [InlineData(true, null, false)]
    [InlineData(true, LogLevel.Information, false)]
    [InlineData(true, LogLevel.Warning, true)]
    [InlineData(true, LogLevel.Error, true)]
    public void Dispose_DeletesMarkedLogOnlyWhenNoWarningsWereLogged(bool deleteOnDispose, LogLevel? loggedLevel, bool expectedExists)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var logFilePath = Path.Combine(workspace.WorkspaceRoot.FullName, "test.log");
        var errorWriter = new TestStartupErrorWriter();

        using (var provider = new FileLoggerProvider(logFilePath, errorWriter))
        {
            if (deleteOnDispose)
            {
                provider.DeleteOnDisposeIfNoWarnings();
            }

            if (loggedLevel is { } level)
            {
                provider.CreateLogger("Test").Log(level, "Log entry");
            }
        }

        Assert.Equal(expectedExists, File.Exists(logFilePath));
        Assert.Empty(errorWriter.MarkupLines);
    }

    private static List<string> CreateOldLogFiles(string directory, int count)
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var paths = new List<string>(count);
        for (var index = 0; index < count; index++)
        {
            var path = FileLoggerProvider.GenerateLogFilePath(directory, timeProvider);
            File.WriteAllText(path, "old");
            File.SetLastWriteTimeUtc(path, timeProvider.GetUtcNow().UtcDateTime);
            paths.Add(path);
            timeProvider.Advance(TimeSpan.FromMinutes(1));
        }

        return paths;
    }
}
