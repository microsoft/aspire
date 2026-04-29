// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Utils;

public class CliTestHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ServiceProvider_CreatesLogFile_AndDisposalCleansUp()
    {
        string logFilePath;
        string workspacePath;

        using (var workspace = TemporaryWorkspace.Create(outputHelper))
        {
            workspacePath = workspace.WorkspaceRoot.FullName;
            var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
            using (var provider = services.BuildServiceProvider())
            {
                var fileLoggerProvider = provider.GetRequiredService<FileLoggerProvider>();
                logFilePath = Assert.IsType<string>(fileLoggerProvider.LogFilePath);

                Assert.True(File.Exists(logFilePath), $"Log file should exist at {logFilePath}");
                Assert.StartsWith(Path.Combine(workspacePath, ".aspire", "logs"), logFilePath);
            }
        }

        // After workspace disposal, the entire temp directory (including log file) should be cleaned up
        Assert.False(Directory.Exists(workspacePath), "Workspace directory should be deleted after disposal");
        Assert.False(File.Exists(logFilePath), "Log file should be deleted after workspace disposal");
    }
}
