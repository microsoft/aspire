// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.JavaScript.Internal.Workspace;

namespace Aspire.Hosting.JavaScript.Tests.Internal;

public class WorkspaceCommandFactoriesTests
{
    [Fact]
    public void NpmPlacesWorkspaceSelectorAfterScriptAndArgsAfterSeparator()
    {
        var argv = WorkspaceCommandFactories.Npm("web", "build", ["--mode", "x"]);
        Assert.Equal(["npm", "run", "build", "--workspace=web", "--", "--mode", "x"], argv);
    }

    [Fact]
    public void NpmWithoutArgsOmitsTrailingSeparator()
    {
        var argv = WorkspaceCommandFactories.Npm("web", "build", []);
        Assert.Equal(["npm", "run", "build", "--workspace=web"], argv);
    }

    [Fact]
    public void PnpmUsesTopologicalFilterSuffix()
    {
        var argv = WorkspaceCommandFactories.Pnpm("web", "build", []);
        Assert.Equal(["pnpm", "--filter", "web...", "run", "build"], argv);
    }

    [Fact]
    public void YarnUsesWorkspaceSelectorBeforeRun()
    {
        var argv = WorkspaceCommandFactories.Yarn("web", "build", ["a"]);
        Assert.Equal(["yarn", "workspace", "web", "run", "build", "a"], argv);
    }

    [Fact]
    public void BunUsesAttachedFilterSelectorBeforeRun()
    {
        var argv = WorkspaceCommandFactories.Bun("web", "dev", []);
        // bun requires the attached "--filter=<name>" form; the space-separated form fails to match
        // the member once "run" follows (observed on bun 1.3.14).
        Assert.Equal(["bun", "--filter=web", "run", "dev"], argv);
    }
}
