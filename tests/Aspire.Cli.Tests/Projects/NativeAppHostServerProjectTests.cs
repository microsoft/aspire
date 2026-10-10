// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.TestServices;
using Aspire.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Projects;

public class NativeAppHostServerProjectTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void RejectsMissingExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        Assert.Throws<ArgumentException>(() => new NativeAppHostServerProject(workspace.Path,
            Path.Combine(workspace.Path, "missing"), new TestProcessExecutionFactory(), NullLogger.Instance));
    }

    [Fact]
    public async Task RejectsManagedIntegrationPackages()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        using var project = new NativeAppHostServerProject(workspace.Path, executable, new TestProcessExecutionFactory(), NullLogger.Instance);
        await Assert.ThrowsAsync<NotSupportedException>(() => project.PrepareAsync("13.6", [IntegrationReference.FromPackage("Aspire.Hosting.Redis", "13.6")]));
        Assert.True((await project.PrepareAsync("13.6", [
            IntegrationReference.FromPackage("Aspire.Hosting", "13.6"),
            IntegrationReference.FromPackage("Aspire.Hosting.CodeGeneration.TypeScript", "13.6")
        ])).NeedsCodeGeneration);
    }

    [Fact]
    public async Task UsesProcessFactoryAuthenticationPrivateSocketAndRunLifetime()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executable = Path.Combine(workspace.Path, "server");
        await File.WriteAllTextAsync(executable, "");
        var factory = new TestProcessExecutionFactory();
        string? directory = null;
        factory.CreateExecutionFromStartInfoCallback = (start, options) =>
        {
            Assert.Equal(executable, start.FileName);
            Assert.Equal(workspace.Path, start.WorkingDirectory);
            Assert.Equal("test-token", start.Environment["ASPIRE_REMOTE_APPHOST_TOKEN"]);
            Assert.Equal("1", start.Environment["NATIVE_HOSTING_CLI"]);
            directory = Path.GetDirectoryName(start.Environment["REMOTE_APP_HOST_SOCKET_PATH"])!;
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            }
            Assert.Equal(ChildProcessLifetime.AppHost, options.Lifetime);
            Assert.True(options.IsolateConsole);
            Assert.True(options.KillEntireProcessTreeOnCancel);
            return new TestProcessExecution(start.FileName, [], null, options,
                (_, _, _) => Task.FromResult((0, (string?)null)), () => 1);
        };
        using (var project = new NativeAppHostServerProject(workspace.Path, executable, factory, NullLogger.Instance))
        {
            var result = await project.RunAsync(42, new Dictionary<string, string> { ["ASPIRE_REMOTE_APPHOST_TOKEN"] = "test-token" },
                [], false, new AppHostServerRunControl(IsolateConsole: true));
            Assert.Equal(Path.Combine(directory!, "apphost.sock"), result.SocketPath);
            await result.Execution.DisposeAsync();
        }

        Assert.False(Directory.Exists(directory));
    }
}
