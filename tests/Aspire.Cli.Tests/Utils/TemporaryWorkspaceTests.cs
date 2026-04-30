// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Utils;

public class TemporaryWorkspaceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ReleasePreservation_DeletesPreservedWorkspaceWhenRequested()
    {
        var workspace = TemporaryWorkspace.Create(outputHelper);
        var workspacePath = workspace.WorkspaceRoot.FullName;

        workspace.Preserve();
        workspace.Dispose();

        Assert.True(Directory.Exists(workspacePath));

        TemporaryWorkspace.ReleasePreservation(workspacePath);

        Assert.False(Directory.Exists(workspacePath));
    }

    [Fact]
    public void ReleasePreservation_LeavesPreservedWorkspaceWhenDeletionDisabled()
    {
        var workspace = TemporaryWorkspace.Create(outputHelper);
        var workspacePath = workspace.WorkspaceRoot.FullName;

        workspace.Preserve();
        workspace.Dispose();

        try
        {
            TemporaryWorkspace.ReleasePreservation(workspacePath, deleteDirectory: false);

            Assert.True(Directory.Exists(workspacePath));
        }
        finally
        {
            if (Directory.Exists(workspacePath))
            {
                Directory.Delete(workspacePath, recursive: true);
            }
        }
    }
}
