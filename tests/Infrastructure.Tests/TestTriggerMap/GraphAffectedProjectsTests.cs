// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.SelectTests;
using Xunit;

namespace Infrastructure.Tests.TestTriggerMap;

/// <summary>
/// Behavioral tests for <see cref="GraphAffectedProjects"/> — the Layer 1 affected-projects graph
/// that replaced <c>dotnet-affected</c>. Each test builds a tiny synthetic MSBuild project graph in
/// a temp directory and asserts which projects a given change maps to, naming the failure mode it
/// guards against.
/// </summary>
/// <remarks>
/// The fixture graph is: <c>Core</c> &lt;- <c>Mid</c> &lt;- <c>AppTests</c> (a ProjectReference
/// chain), plus an independent <c>Other</c>. A shared file <c>Shared/Linked.cs</c> is link-compiled
/// into both <c>Core</c> and <c>Other</c>. <c>EnableDefaultCompileItems</c> is off so each project's
/// inputs are exactly the files it declares — no surprise globbing.
/// </remarks>
[Collection("GraphAffectedProjects")] // MSBuildLocator registers process-wide; keep these serialized.
public sealed class GraphAffectedProjectsTests
{
    public GraphAffectedProjectsTests()
    {
        // Must run before any GraphAffectedProjects engine method is JITted (see EnsureMSBuildRegistered).
        GraphAffectedProjects.EnsureMSBuildRegistered();
    }

    // Failure mode: a change to a depended-on project does not propagate to its downstream dependents,
    // so the dependents' tests are silently skipped. The reverse ProjectReference closure must carry
    // Core -> Mid -> AppTests.
    [Fact]
    public void SourceChangePropagatesToReverseDependents()
    {
        using var repo = new GraphFixture();

        var affected = repo.Compute("Core/Core.cs");

        Assert.Contains("Core", affected);
        Assert.Contains("Mid", affected);
        Assert.Contains("AppTests", affected);
        // Other neither references nor is referenced by Core.
        Assert.DoesNotContain("Other", affected);
    }

    // Failure mode: a cross-project linked/shared file (compiled into several projects via
    // <Compile Include="..\Shared\X" Link="..."/>) is attributed to only one consumer (or none),
    // silently skipping the other consumers' tests. The item FullPath must map the shared file to
    // every linking project.
    [Fact]
    public void LinkedSharedFileMapsToAllLinkingProjects()
    {
        using var repo = new GraphFixture();

        var affected = repo.Compute("Shared/Linked.cs");

        // Linked into both Core and Other.
        Assert.Contains("Core", affected);
        Assert.Contains("Other", affected);
        // ...and Core's dependents come along via the reverse closure.
        Assert.Contains("Mid", affected);
        Assert.Contains("AppTests", affected);
    }

    // Failure mode: a link-compiled shared file (attributed via the FullPath index, not a project
    // directory) is omitted from AttributedPaths, so the selector's run-all fallback would escalate it
    // to ALL instead of trusting Layer 1. The graph must report it as attributed.
    [Fact]
    public void LinkedSharedFileIsReportedAsAttributed()
    {
        using var repo = new GraphFixture();

        var result = repo.ComputeResult("Shared/Linked.cs");

        Assert.Contains("Shared/Linked.cs", result.AttributedPaths);
    }

    // Failure mode: the graph reports THAT a test is affected but not HOW, so the step summary cannot
    // show the decision path. The reverse closure must record, per affected project, the shortest chain
    // of project names from the directly-changed project to it, plus the changed file that seeded it.
    // Core/Core.cs -> Core -> Mid -> AppTests, seeded by Core/Core.cs.
    [Fact]
    public void AffectedProjectCarriesTheChainAndSeedFile()
    {
        using var repo = new GraphFixture();

        var result = repo.ComputeResult("Core/Core.cs");

        var appTests = result.Paths["AppTests"];
        Assert.Equal("Core/Core.cs", appTests.ChangedFile);
        Assert.Equal(new[] { "Core", "Mid", "AppTests" }, appTests.ProjectChain);

        // The directly-changed project's own chain is just itself.
        var core = result.Paths["Core"];
        Assert.Equal("Core/Core.cs", core.ChangedFile);
        Assert.Equal(new[] { "Core" }, core.ProjectChain);
    }

    // Failure mode: a changed .csproj is not attributed to its own project, so a project-file edit
    // (e.g. adding a dependency) selects nothing for that project.
    [Fact]
    public void ChangedProjectFileSelectsThatProject()
    {
        using var repo = new GraphFixture();

        var affected = repo.Compute("Mid/Mid.csproj");

        Assert.Contains("Mid", affected);
        Assert.Contains("AppTests", affected);
        // A Mid.csproj edit does not implicate Core (Mid references Core, not the other way around).
        Assert.DoesNotContain("Core", affected);
    }

    // Failure mode: a DELETED file (or the old side of a cross-project rename) maps to no project at
    // HEAD because no project lists it anymore, so its owning project's dependents' tests are silently
    // skipped. The project-directory containment fallback must still attribute it to the owning project.
    [Fact]
    public void DeletedFileUnderProjectDirAttributedViaContainmentFallback()
    {
        using var repo = new GraphFixture();

        // Ghost.cs never existed as an item and is not on disk — exactly the deleted-file shape.
        var affected = repo.Compute("Core/Ghost.cs");

        Assert.Contains("Core", affected);
        Assert.Contains("Mid", affected);
        Assert.Contains("AppTests", affected);
    }

    // Failure mode: a path that belongs to no project (e.g. docs) is wrongly attributed, over-selecting
    // — or worse, masks a real miss. It must map to nothing here (Layer 2 / the run-all fallback in the
    // selector handles loose files; Layer 1 reports only graph-attributable projects).
    [Fact]
    public void FileOutsideEveryProjectDirSelectsNothing()
    {
        using var repo = new GraphFixture();

        var affected = repo.Compute("docs/notes.md");

        Assert.Empty(affected);
    }

    // Failure mode: a change to a repo build file imported by every project (Directory.Build.props,
    // and by the same mechanism eng/Versions.props etc., captured via ProjectInstance.ImportPaths) is
    // not attributed to the projects that import it, so a global build/version change silently runs no
    // tests. It must fan out to every importing project. (The file lives at the repo root, under no
    // project dir, so ONLY the ImportPaths index — not the containment fallback — can catch it.)
    [Fact]
    public void ChangeToImportedBuildPropsAffectsImportingProjects()
    {
        using var repo = new GraphFixture();

        var affected = repo.Compute("Directory.Build.props");

        Assert.Contains("Core", affected);
        Assert.Contains("Mid", affected);
        Assert.Contains("AppTests", affected);
        Assert.Contains("Other", affected);
    }

    // Failure mode: an empty diff (e.g. a PR whose changed-file set resolved to nothing) builds the
    // graph and/or throws instead of cheaply returning "nothing affected". It must short-circuit to an
    // empty set so the selector falls through to Layer 2 alone.
    [Fact]
    public void EmptyChangeSetSelectsNothing()
    {
        using var repo = new GraphFixture();

        var affected = repo.Compute();

        Assert.Empty(affected);
    }

    // Failure mode: a deleted/unmodeled file under a project that is itself nested inside another
    // project's directory is attributed to the OUTER (parent-dir) project, over-selecting the parent's
    // dependents and missing the real owner. The longest-directory-first containment fallback must pick
    // the deepest (most specific) owning project. Nested is isolated, so only it should be affected.
    [Fact]
    public void DeletedFileInNestedProjectDirAttributedToDeepestProject()
    {
        using var repo = new GraphFixture();

        // Ghost.cs never existed and sits under Core/Nested (a project nested below Core/).
        var affected = repo.Compute("Core/Nested/Ghost.cs");

        Assert.Contains("Nested", affected);
        Assert.DoesNotContain("Core", affected);
        Assert.DoesNotContain("Mid", affected);
        Assert.DoesNotContain("AppTests", affected);
    }

    // Failure mode: a cross-project rename (git -M reports one record "R<sim> <old> <new>") is parsed
    // as only one path, so the project that LOST the file (old side) is not marked changed and its
    // dependents' tests are silently skipped. data.txt is not a declared item, so neither side touches
    // a .csproj — the attribution comes purely from parsing BOTH paths of the rename record plus the
    // directory-containment fallback. Exercises the git diff path (GetChangedPathsFromGit), which the
    // changed-files fixtures never reach.
    [Fact]
    public void CrossProjectRenameAttributesBothOldAndNewOwners()
    {
        using var repo = new GitGraphFixture();

        var affected = repo.RenameAcrossProjectsAndCompute("Core/data.txt", "Other/data.txt");

        // Old owner (Core) and its dependents, plus the new owner (Other).
        Assert.Contains("Core", affected);
        Assert.Contains("Mid", affected);
        Assert.Contains("AppTests", affected);
        Assert.Contains("Other", affected);
    }

    /// <summary>
    /// Creates a disposable temp directory containing a minimal but real MSBuild project graph plus an
    /// <c>Aspire.slnx</c>, and runs <see cref="GraphAffectedProjects.Compute"/> against it using a
    /// changed-files list (no git required).
    /// </summary>
    private sealed class GraphFixture : IDisposable
    {
        private readonly TestTempDirectory _temp = new();

        public GraphFixture()
        {
            // Empty Directory.Build.props/targets stop MSBuild's upward walk from picking up anything
            // above the temp dir, keeping the fixture hermetic.
            Write("Directory.Build.props", "<Project />");
            Write("Directory.Build.targets", "<Project />");

            Write("Shared/Linked.cs", "namespace Shared; public static class Linked { }");

            // Core: own file + linked shared file; no references.
            Write("Core/Core.cs", "namespace Core; public class C { }");
            WriteProject("Core/Core.csproj", compiles: ["Core.cs", @"..\Shared\Linked.cs"], references: []);

            // Mid -> Core.
            Write("Mid/Mid.cs", "namespace Mid; public class M { }");
            WriteProject("Mid/Mid.csproj", compiles: ["Mid.cs"], references: [@"..\Core\Core.csproj"]);

            // AppTests -> Mid (a "test" project by name).
            Write("AppTests/AppTests.cs", "namespace AppTests; public class T { }");
            WriteProject("AppTests/AppTests.csproj", compiles: ["AppTests.cs"], references: [@"..\Mid\Mid.csproj"]);

            // Other: own file + linked shared file; isolated leaf.
            Write("Other/Other.cs", "namespace Other; public class O { }");
            WriteProject("Other/Other.csproj", compiles: ["Other.cs", @"..\Shared\Linked.cs"], references: []);

            // Nested: an isolated project that lives UNDER Core's directory, so the containment
            // fallback must prefer it over Core for files under Core/Nested.
            Write("Core/Nested/Nested.cs", "namespace Nested; public class N { }");
            WriteProject("Core/Nested/Nested.csproj", compiles: ["Nested.cs"], references: []);

            Write("Aspire.slnx",
                """
                <Solution>
                  <Project Path="Core/Core.csproj" />
                  <Project Path="Mid/Mid.csproj" />
                  <Project Path="AppTests/AppTests.csproj" />
                  <Project Path="Other/Other.csproj" />
                  <Project Path="Core/Nested/Nested.csproj" />
                </Solution>
                """);
        }

        public IReadOnlyCollection<string> Compute(params string[] changedRepoRelativePaths)
        {
            var changedFilesPath = System.IO.Path.Combine(_temp.Path, "changed.txt");
            File.WriteAllLines(changedFilesPath, changedRepoRelativePaths);
            return GraphAffectedProjects.Compute(_temp.Path, System.IO.Path.Combine(_temp.Path, "Aspire.slnx"), from: null, to: null, changedFilesPath: changedFilesPath).AffectedProjects;
        }

        public AffectedResult ComputeResult(params string[] changedRepoRelativePaths)
        {
            var changedFilesPath = System.IO.Path.Combine(_temp.Path, "changed.txt");
            File.WriteAllLines(changedFilesPath, changedRepoRelativePaths);
            return GraphAffectedProjects.Compute(_temp.Path, System.IO.Path.Combine(_temp.Path, "Aspire.slnx"), from: null, to: null, changedFilesPath: changedFilesPath);
        }

        private void Write(string relativePath, string contents)
        {
            var fullPath = System.IO.Path.Combine(_temp.Path, relativePath.Replace('\\', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, contents);
        }

        private void WriteProject(string relativePath, string[] compiles, string[] references)
        {
            var items = string.Join(Environment.NewLine,
                compiles.Select(c => $"""    <Compile Include="{c}" Link="{System.IO.Path.GetFileName(c)}" />""")
                    .Concat(references.Select(r => $"""    <ProjectReference Include="{r}" />""")));

            // EnableDefaultCompileItems=false: the project's inputs are exactly what is declared above,
            // so the file->project mapping under test is deterministic and not affected by SDK globbing.
            Write(relativePath,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                {items}
                  </ItemGroup>
                </Project>
                """);
        }

        public void Dispose() => _temp.Dispose();
    }

    /// <summary>
    /// A real git repo containing the same minimal project graph, used to exercise the git diff path
    /// (<c>git diff --name-status -M</c>) — including rename records — that the changed-files fixture
    /// cannot reach.
    /// </summary>
    private sealed class GitGraphFixture : IDisposable
    {
        private readonly TestTempDirectory _temp = new();

        public GitGraphFixture()
        {
            Write("Directory.Build.props", "<Project />");
            Write("Directory.Build.targets", "<Project />");

            Write("Shared/Linked.cs", "namespace Shared; public static class Linked { }");

            Write("Core/Core.cs", "namespace Core; public class C { }");
            WriteProject("Core/Core.csproj", compiles: ["Core.cs", @"..\Shared\Linked.cs"], references: []);

            Write("Mid/Mid.cs", "namespace Mid; public class M { }");
            WriteProject("Mid/Mid.csproj", compiles: ["Mid.cs"], references: [@"..\Core\Core.csproj"]);

            Write("AppTests/AppTests.cs", "namespace AppTests; public class T { }");
            WriteProject("AppTests/AppTests.csproj", compiles: ["AppTests.cs"], references: [@"..\Mid\Mid.csproj"]);

            Write("Other/Other.cs", "namespace Other; public class O { }");
            WriteProject("Other/Other.csproj", compiles: ["Other.cs"], references: []);

            // A loose, non-declared file (not a <Compile>/<Content> item). It is attributed purely by
            // directory containment, so renaming it touches no .csproj — isolating the rename-record
            // parse from any project-file edit.
            Write("Core/data.txt", "payload");

            Write("Aspire.slnx",
                """
                <Solution>
                  <Project Path="Core/Core.csproj" />
                  <Project Path="Mid/Mid.csproj" />
                  <Project Path="AppTests/AppTests.csproj" />
                  <Project Path="Other/Other.csproj" />
                </Solution>
                """);

            Git("init", "-q", "-b", "main");
            Git("config", "user.email", "test@example.com");
            Git("config", "user.name", "Test");
            Git("config", "commit.gpgsign", "false");
            Git("add", "-A");
            Git("commit", "-q", "-m", "base");
        }

        /// <summary>
        /// Renames <paramref name="oldRelativePath"/> to <paramref name="newRelativePath"/> in a new
        /// commit, then computes the affected set for <c>base..HEAD</c> via the git diff path.
        /// </summary>
        public IReadOnlyCollection<string> RenameAcrossProjectsAndCompute(string oldRelativePath, string newRelativePath)
        {
            var baseSha = Git("rev-parse", "HEAD");

            var newFull = System.IO.Path.Combine(_temp.Path, newRelativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(newFull)!);
            Git("mv", oldRelativePath, newRelativePath);
            Git("commit", "-q", "-m", "rename across projects");

            return GraphAffectedProjects.Compute(_temp.Path, System.IO.Path.Combine(_temp.Path, "Aspire.slnx"), from: baseSha, to: "HEAD", changedFilesPath: null).AffectedProjects;
        }

        private void Write(string relativePath, string contents)
        {
            var fullPath = System.IO.Path.Combine(_temp.Path, relativePath.Replace('\\', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, contents);
        }

        private void WriteProject(string relativePath, string[] compiles, string[] references)
        {
            var items = string.Join(Environment.NewLine,
                compiles.Select(c => $"""    <Compile Include="{c}" Link="{System.IO.Path.GetFileName(c)}" />""")
                    .Concat(references.Select(r => $"""    <ProjectReference Include="{r}" />""")));

            Write(relativePath,
                $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                {items}
                  </ItemGroup>
                </Project>
                """);
        }

        private string Git(params string[] args) => GitCli.Run(_temp.Path, args);

        public void Dispose() => _temp.Dispose();
    }
}
