// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Diagnostics;
using Aspire.Cli.Interaction;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Diagnostics;

public class FileLoggerProviderTests(ITestOutputHelper outputHelper)
{
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
}
