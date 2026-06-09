// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Analyzers;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Analyzers;

public class InferDepsFilterTests
{
    [Fact]
    public void Apply_EmptyInferDeps_ReturnsAllProjectsUnchanged()
    {
        var input = new[] { "tests/A.Tests/A.Tests.csproj", "tests/B.Tests/B.Tests.csproj" };

        var result = InferDepsFilter.Apply(input, new Dictionary<string, bool>(), Array.Empty<string>());

        Assert.Equal(input, result);
    }

    [Fact]
    public void Apply_InferDepsTrue_IsNoOp()
    {
        // inferDeps:true is the default; an explicit true entry suppresses nothing.
        var input = new[] { "tests/A.Tests/A.Tests.csproj", "tests/B.Tests/B.Tests.csproj" };
        var inferDeps = new Dictionary<string, bool>
        {
            ["tests/A.Tests/A.Tests.csproj"] = true,
        };

        var result = InferDepsFilter.Apply(input, inferDeps, Array.Empty<string>());

        Assert.Equal(input, result);
    }

    [Fact]
    public void InferDeps_False_KeepsProjectWhenDeclaredEdgeHits()
    {
        // A declared edge (mapping/edge) resolved to the inferDeps:false project → it's an explicit
        // opt-in → keep it. Reverting the "keep when declared" branch turns this red.
        var all = new[] { "tests/Foo.Tests/Foo.Tests.csproj", "tests/Restricted.Tests/Restricted.Tests.csproj" };
        var inferDeps = new Dictionary<string, bool>
        {
            ["tests/Restricted.Tests/Restricted.Tests.csproj"] = false,
        };
        var declared = new[] { "tests/Restricted.Tests/Restricted.Tests.csproj" };

        var result = InferDepsFilter.Apply(all, inferDeps, declared);

        Assert.Contains("tests/Restricted.Tests/Restricted.Tests.csproj", result);
        Assert.Contains("tests/Foo.Tests/Foo.Tests.csproj", result);
    }

    [Fact]
    public void InferDeps_False_DropsProjectFromAffectedOnly()
    {
        // inferDeps:false project pulled in ONLY by dotnet-affected (inferred build-graph reference),
        // with no declared edge resolving to it → drop it. Reverting the suppression turns this red.
        var all = new[] { "tests/Foo.Tests/Foo.Tests.csproj", "tests/Restricted.Tests/Restricted.Tests.csproj" };
        var inferDeps = new Dictionary<string, bool>
        {
            ["tests/Restricted.Tests/Restricted.Tests.csproj"] = false,
        };
        var declared = new[] { "tests/Foo.Tests/Foo.Tests.csproj" }; // only Foo, not Restricted

        var result = InferDepsFilter.Apply(all, inferDeps, declared);

        Assert.Contains("tests/Foo.Tests/Foo.Tests.csproj", result);
        Assert.DoesNotContain("tests/Restricted.Tests/Restricted.Tests.csproj", result);
    }

    [Fact]
    public void Apply_NormalizesSeparatorsAndCase()
    {
        // The rule comparison must tolerate Windows backslashes and case differences,
        // since paths can come from MSBuild on Windows and rules are author-supplied
        // strings.
        var all = new[] { "tests\\Restricted.Tests\\Restricted.Tests.csproj" };
        var inferDeps = new Dictionary<string, bool>
        {
            ["TESTS/restricted.tests/Restricted.Tests.csproj"] = false,
        };
        var declared = Array.Empty<string>();

        var result = InferDepsFilter.Apply(all, inferDeps, declared);

        Assert.Empty(result);
    }

    [Fact]
    public void Apply_ProjectsWithoutInferDepsEntry_PassThroughEvenWithoutDeclaredEdge()
    {
        // Only inferDeps:false projects need the explicit opt-in. Projects with no entry keep their
        // default behavior: any source of inclusion (mapping or dotnet-affected) keeps them.
        var all = new[] { "tests/Foo.Tests/Foo.Tests.csproj", "tests/Bar.Tests/Bar.Tests.csproj" };
        var inferDeps = new Dictionary<string, bool>
        {
            ["tests/Restricted.Tests/Restricted.Tests.csproj"] = false,
        };

        var result = InferDepsFilter.Apply(all, inferDeps, Array.Empty<string>());

        Assert.Equal(all, result);
    }

    [Fact]
    public void Apply_MultipleSuppressedProjects_FiltersEachIndependently()
    {
        var all = new[]
        {
            "tests/A.Tests/A.Tests.csproj",                           // no entry, kept
            "tests/Acquisition.Tests/Acquisition.Tests.csproj",      // false, declared → kept
            "tests/Infrastructure.Tests/Infrastructure.Tests.csproj", // false, not declared → dropped
        };
        var inferDeps = new Dictionary<string, bool>
        {
            ["tests/Acquisition.Tests/Acquisition.Tests.csproj"] = false,
            ["tests/Infrastructure.Tests/Infrastructure.Tests.csproj"] = false,
        };
        var declared = new[] { "tests/Acquisition.Tests/Acquisition.Tests.csproj" };

        var result = InferDepsFilter.Apply(all, inferDeps, declared);

        Assert.Equal(
            new[]
            {
                "tests/A.Tests/A.Tests.csproj",
                "tests/Acquisition.Tests/Acquisition.Tests.csproj",
            },
            result);
    }
}
