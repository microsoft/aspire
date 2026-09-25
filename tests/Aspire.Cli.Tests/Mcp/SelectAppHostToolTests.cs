// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting.Utils;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class SelectAppHostToolTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallToolAsync_PreservesSuppliedPathInResponse(bool hasMatchingConnection)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(),
            "Symlink path spelling test only runs on Unix-like platforms.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var appHostFile = new FileInfo(Path.Combine(realDirectory.FullName, "AppHost.csproj"));
        File.WriteAllText(appHostFile.FullName, "<Project />");

        var symlinkDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "link");
        TestSymlinkHelper.TryCreateSymlink(symlinkDirectory, realDirectory.FullName);
        var suppliedPath = Path.Combine("link", appHostFile.Name);
        var displayPath = Path.GetFullPath(Path.Combine(workspace.WorkspaceRoot.FullName, suppliedPath));
        var canonicalPath = PathNormalizer.ResolveToFilesystemPath(displayPath);
        Assert.NotEqual(displayPath, canonicalPath);

        var monitor = new TestAuxiliaryBackchannelMonitor();
        if (hasMatchingConnection)
        {
            monitor.AddConnection(
                "socket",
                new TestAppHostAuxiliaryBackchannel
                {
                    AppHostInfo = new AppHostInformation
                    {
                        AppHostPath = appHostFile.FullName,
                        ProcessId = Environment.ProcessId
                    }
                });
        }

        var tool = new SelectAppHostTool(
            monitor,
            TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot));
        var arguments = new Dictionary<string, JsonElement>
        {
            ["appHostPath"] = JsonSerializer.SerializeToElement(suppliedPath)
        };

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(arguments),
            TestContext.Current.CancellationToken);

        var content = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        if (hasMatchingConnection)
        {
            Assert.Null(result.IsError);
            Assert.Equal($"Selected AppHost: {displayPath}", content.Text);
            Assert.Equal(canonicalPath, monitor.SelectedAppHostPath);
        }
        else
        {
            Assert.True(result.IsError);
            Assert.Equal(
                $"No running AppHost found at path '{displayPath}'. No AppHosts are currently running.",
                content.Text);
            Assert.Null(monitor.SelectedAppHostPath);
        }
    }
}
