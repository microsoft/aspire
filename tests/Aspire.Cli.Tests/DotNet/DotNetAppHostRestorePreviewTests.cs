// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.DotNet;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.DotNet;

public class DotNetAppHostRestorePreviewTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task CreateAsync_RejectsConfigOutsideAppHostHierarchyBeforeWritingDraft()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var appHost = workspace.CreateDirectory("apphost");
        var unrelated = workspace.CreateDirectory("unrelated");
        var project = new FileInfo(Path.Combine(appHost.FullName, "AppHost.csproj"));
        var candidate = new DotNetAppHostNuGetConfigMergerCandidate(
            new FileInfo(Path.Combine(unrelated.FullName, "NuGet.Config")), null,
            Encoding.UTF8.GetBytes("<configuration />"));
        var settings = new DotNetRestoreSettings(project.FullName, "NuGet.targets", []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DotNetAppHostRestorePreview.CreateAsync(
            NuGetTestHelper.CreateService(), candidate, settings, project,
            new Dictionary<string, string>(), TestContext.Current.CancellationToken));

        Assert.Empty(unrelated.EnumerateFileSystemInfos());
        Assert.Empty(appHost.EnumerateFileSystemInfos());
    }
}
