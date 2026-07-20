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
    public void PnpmScopesRunToSingleMember()
    {
        var argv = WorkspaceCommandFactories.Pnpm("web", "dev", ["--port", "5173"]);
        // The topological "..." suffix must never appear on the script command: it would run the
        // script in every workspace dependency AND forward the trailing args to all of them
        // (ERR_PNPM_RECURSIVE_RUN_FIRST_FAIL when a dependency's script rejects them).
        Assert.Equal(["pnpm", "--filter", "web", "run", "dev", "--port", "5173"], argv);
    }

    [Fact]
    public void PnpmBuildDependenciesSelectsDependenciesOnlyInTopologicalOrder()
    {
        var argv = WorkspaceCommandFactories.PnpmBuildDependencies("web", "build");
        // "<name>^..." selects the member's workspace dependencies EXCLUDING the member itself,
        // and carries no user args, so dependency scripts never see the member's build args.
        // "--if-present" prevents ERR_PNPM_RECURSIVE_RUN_NO_SCRIPT when none of the selected
        // dependencies defines the script (not every workspace package needs a build script).
        Assert.Equal(["pnpm", "--filter", "web^...", "run", "--if-present", "build"], argv);
    }

    [Fact]
    public void PnpmDeployFiltersTheMemberAndPrunesToProduction()
    {
        var argv = WorkspaceCommandFactories.PnpmDeploy("web", "/deploy");
        // "pnpm --filter <name> deploy --prod <target>" produces a self-contained, production-only
        // copy of the member (workspace deps injected) at <target>. pnpm 10 requires
        // injectWorkspacePackages: true for this; the workspace validator enforces that.
        Assert.Equal(["pnpm", "--filter", "web", "deploy", "--prod", "/deploy"], argv);
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
