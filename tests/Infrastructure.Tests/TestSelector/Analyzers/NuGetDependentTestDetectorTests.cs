// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using TestSelector.Analyzers;
using Xunit;

namespace Infrastructure.Tests.TestSelector.Analyzers;

public class NuGetDependentTestDetectorTests
{
    private static string GetFixturesDir()
    {
        // The test fixtures are copied to the output directory
        return Path.Combine(AppContext.BaseDirectory, "TestFixtures");
    }

    [Fact]
    public void IsPackableProject_ReturnsTrueForPackableProject()
    {
        var path = Path.Combine(GetFixturesDir(), "PackableProject.csproj");
        Assert.True(NuGetDependentTestDetector.IsPackableProject(path));
    }

    [Fact]
    public void IsPackableProject_ReturnsFalseForNonPackableProject()
    {
        var path = Path.Combine(GetFixturesDir(), "TestProject.csproj");
        Assert.False(NuGetDependentTestDetector.IsPackableProject(path));
    }

    [Fact]
    public void IsPackableProject_ReturnsFalseForConditionalIsPackable()
    {
        var path = Path.Combine(GetFixturesDir(), "ConditionalPackableProject.csproj");
        // Conditional <IsPackable> should not count as definitively packable
        Assert.False(NuGetDependentTestDetector.IsPackableProject(path));
    }

    [Fact]
    public void IsPackableProject_ReturnsFalseForMissingFile()
    {
        Assert.False(NuGetDependentTestDetector.IsPackableProject("/nonexistent/path.csproj"));
    }

    [Fact]
    public void HasRequiredNuGetsForTesting_ReturnsTrueForNuGetDependentProject()
    {
        var path = Path.Combine(GetFixturesDir(), "NuGetDependentTestProject.csproj");
        Assert.True(NuGetDependentTestDetector.HasRequiredNuGetsForTesting(path));
    }

    [Fact]
    public void HasRequiredNuGetsForTesting_ReturnsFalseForRegularProject()
    {
        var path = Path.Combine(GetFixturesDir(), "TestProject.csproj");
        Assert.False(NuGetDependentTestDetector.HasRequiredNuGetsForTesting(path));
    }

    [Fact]
    public void Detect_ReturnsNull_WhenNoProducersAffected()
    {
        var detector = new NuGetDependentTestDetector(["eng/clipack/**"]);

        var result = detector.Detect(
            affectedProjects: ["tests/Foo.Tests/Foo.Tests.csproj"],
            activeFiles: ["src/Foo/Bar.cs"],
            workingDir: "/nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void Detect_MatchesGlobPattern()
    {
        // Create a temp directory structure to test with
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        try
        {
            // Set up test directory with a nuget-dependent test project
            var testsDir = Path.Combine(tempDir, "tests", "FakeNuGetTests");
            Directory.CreateDirectory(testsDir);
            File.WriteAllText(Path.Combine(testsDir, "FakeNuGetTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <RequiredNuGetsForTesting>true</RequiredNuGetsForTesting>
                  </PropertyGroup>
                </Project>
                """);

            var detector = new NuGetDependentTestDetector(["eng/clipack/**"]);
            var result = detector.Detect(
                affectedProjects: [],
                activeFiles: ["eng/clipack/build.sh"],
                workingDir: tempDir);

            Assert.NotNull(result);
            Assert.True(result.Triggered);
            Assert.Contains("eng/clipack/build.sh", result.AffectedPackableProjects);
            Assert.Contains("tests/FakeNuGetTests/FakeNuGetTests.csproj", result.Projects);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Detect_MatchesIsPackableCsproj()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        try
        {
            // Set up a packable source project
            var srcDir = Path.Combine(tempDir, "src", "MyLib");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "MyLib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IsPackable>true</IsPackable>
                  </PropertyGroup>
                </Project>
                """);

            // Set up a nuget-dependent test project
            var testsDir = Path.Combine(tempDir, "tests", "NuGetTests");
            Directory.CreateDirectory(testsDir);
            File.WriteAllText(Path.Combine(testsDir, "NuGetTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <RequiredNuGetsForTesting>true</RequiredNuGetsForTesting>
                  </PropertyGroup>
                </Project>
                """);

            var detector = new NuGetDependentTestDetector([]);
            var result = detector.Detect(
                affectedProjects: ["src/MyLib/MyLib.csproj"],
                activeFiles: [],
                workingDir: tempDir);

            Assert.NotNull(result);
            Assert.True(result.Triggered);
            Assert.Contains("src/MyLib/MyLib.csproj", result.AffectedPackableProjects);
            Assert.Contains("tests/NuGetTests/NuGetTests.csproj", result.Projects);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Detect_ReturnsNull_WhenEmptyPatternsAndNoCsprojCandidates()
    {
        // No glob patterns configured, and no .csproj paths in candidates
        var detector = new NuGetDependentTestDetector([]);

        var result = detector.Detect(
            affectedProjects: [],
            activeFiles: ["src/Foo/Bar.cs", "eng/scripts/build.sh"],
            workingDir: "/nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public void Detect_SkipsNonPackableCsproj()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        try
        {
            // Set up a non-packable source project (IsPackable=false explicitly)
            var srcDir = Path.Combine(tempDir, "src", "MyTestLib");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "MyTestLib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                </Project>
                """);

            // Set up a nuget-dependent test project (should NOT be triggered)
            var testsDir = Path.Combine(tempDir, "tests", "NuGetTests");
            Directory.CreateDirectory(testsDir);
            File.WriteAllText(Path.Combine(testsDir, "NuGetTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <RequiredNuGetsForTesting>true</RequiredNuGetsForTesting>
                  </PropertyGroup>
                </Project>
                """);

            var detector = new NuGetDependentTestDetector([]);
            var result = detector.Detect(
                affectedProjects: ["src/MyTestLib/MyTestLib.csproj"],
                activeFiles: [],
                workingDir: tempDir);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Detect_DeduplicatesCandidatesFromBothSources()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        try
        {
            var srcDir = Path.Combine(tempDir, "src", "MyLib");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "MyLib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IsPackable>true</IsPackable>
                  </PropertyGroup>
                </Project>
                """);

            var testsDir = Path.Combine(tempDir, "tests", "NuGetTests");
            Directory.CreateDirectory(testsDir);
            File.WriteAllText(Path.Combine(testsDir, "NuGetTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <RequiredNuGetsForTesting>true</RequiredNuGetsForTesting>
                  </PropertyGroup>
                </Project>
                """);

            // Same csproj appears in both affectedProjects and activeFiles
            var detector = new NuGetDependentTestDetector([]);
            var result = detector.Detect(
                affectedProjects: ["src/MyLib/MyLib.csproj"],
                activeFiles: ["src/MyLib/MyLib.csproj"],
                workingDir: tempDir);

            Assert.NotNull(result);
            // Should only appear once due to Distinct()
            Assert.Single(result.AffectedPackableProjects);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Detect_FindsMultipleNuGetDependentTestProjects()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        try
        {
            var testsDir1 = Path.Combine(tempDir, "tests", "Templates.Tests");
            var testsDir2 = Path.Combine(tempDir, "tests", "EndToEnd.Tests");
            var testsDir3 = Path.Combine(tempDir, "tests", "Regular.Tests");
            Directory.CreateDirectory(testsDir1);
            Directory.CreateDirectory(testsDir2);
            Directory.CreateDirectory(testsDir3);

            File.WriteAllText(Path.Combine(testsDir1, "Templates.Tests.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RequiredNuGetsForTesting>true</RequiredNuGetsForTesting></PropertyGroup></Project>""");
            File.WriteAllText(Path.Combine(testsDir2, "EndToEnd.Tests.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><RequiredNuGetsForTesting>true</RequiredNuGetsForTesting></PropertyGroup></Project>""");
            File.WriteAllText(Path.Combine(testsDir3, "Regular.Tests.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>""");

            var detector = new NuGetDependentTestDetector(["eng/clipack/**"]);
            var result = detector.Detect(
                affectedProjects: [],
                activeFiles: ["eng/clipack/build.sh"],
                workingDir: tempDir);

            Assert.NotNull(result);
            Assert.Equal(2, result.Projects.Count);
            Assert.Contains("tests/Templates.Tests/Templates.Tests.csproj", result.Projects);
            Assert.Contains("tests/EndToEnd.Tests/EndToEnd.Tests.csproj", result.Projects);
            Assert.DoesNotContain("tests/Regular.Tests/Regular.Tests.csproj", result.Projects);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FindNuGetDependentTestProjects_ReturnsEmpty_WhenNoTestsDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = NuGetDependentTestDetector.FindNuGetDependentTestProjects(tempDir);
            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void IsPackableProject_ReturnsFalseForExplicitlyNotPackable()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var csprojPath = Path.Combine(tempDir, "NotPackable.csproj");
            File.WriteAllText(csprojPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                </Project>
                """);
            Assert.False(NuGetDependentTestDetector.IsPackableProject(csprojPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void IsPackableProject_ReturnsFalseForProjectWithNoIsPackableElement()
    {
        var path = Path.Combine(GetFixturesDir(), "SourceProject.csproj");
        Assert.False(NuGetDependentTestDetector.IsPackableProject(path));
    }

    [Fact]
    public void HasRequiredNuGetsForTesting_ReturnsFalseForMissingFile()
    {
        Assert.False(NuGetDependentTestDetector.HasRequiredNuGetsForTesting("/nonexistent/path.csproj"));
    }

    [Fact]
    public void Detect_ReturnsNull_WhenNoNuGetDependentTestsExist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        try
        {
            // Set up a packable source project but no nuget-dependent test projects
            var srcDir = Path.Combine(tempDir, "src", "MyLib");
            Directory.CreateDirectory(srcDir);
            File.WriteAllText(Path.Combine(srcDir, "MyLib.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IsPackable>true</IsPackable>
                  </PropertyGroup>
                </Project>
                """);

            var testsDir = Path.Combine(tempDir, "tests", "RegularTests");
            Directory.CreateDirectory(testsDir);
            File.WriteAllText(Path.Combine(testsDir, "RegularTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                </Project>
                """);

            var detector = new NuGetDependentTestDetector([]);
            var result = detector.Detect(
                affectedProjects: ["src/MyLib/MyLib.csproj"],
                activeFiles: [],
                workingDir: tempDir);

            Assert.Null(result);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FindNuGetDependentTestProjects_SkipsTestFixtures()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        try
        {
            // Create a real NuGet-dependent test project
            var realTestDir = Path.Combine(tempDir, "tests", "RealNuGetTests");
            Directory.CreateDirectory(realTestDir);
            File.WriteAllText(Path.Combine(realTestDir, "RealNuGetTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <RequiredNuGetsForTesting>true</RequiredNuGetsForTesting>
                  </PropertyGroup>
                </Project>
                """);

            // Create a TestFixtures project that also has RequiredNuGetsForTesting
            var fixtureDir = Path.Combine(tempDir, "tests", "SomeProject", "TestFixtures");
            Directory.CreateDirectory(fixtureDir);
            File.WriteAllText(Path.Combine(fixtureDir, "FixtureProject.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <RequiredNuGetsForTesting>true</RequiredNuGetsForTesting>
                  </PropertyGroup>
                </Project>
                """);

            var result = NuGetDependentTestDetector.FindNuGetDependentTestProjects(tempDir);

            // Should find the real test project but skip the TestFixtures one
            Assert.Single(result);
            Assert.Contains("tests/RealNuGetTests/RealNuGetTests.csproj", result);
            Assert.DoesNotContain(result, p => p.Contains("/TestFixtures/"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FindNuGetDependentTestProjects_ReturnsCsprojPaths()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        try
        {
            var testsDir = Path.Combine(tempDir, "tests", "MyTests");
            Directory.CreateDirectory(testsDir);
            File.WriteAllText(Path.Combine(testsDir, "MyTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <RequiredNuGetsForTesting>true</RequiredNuGetsForTesting>
                  </PropertyGroup>
                </Project>
                """);

            var result = NuGetDependentTestDetector.FindNuGetDependentTestProjects(tempDir);

            Assert.Single(result);
            // Should return .csproj path, not directory with trailing slash
            Assert.EndsWith(".csproj", result[0]);
            Assert.Equal("tests/MyTests/MyTests.csproj", result[0]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void FindNuGetDependentTestProjects_ReadsRequiredNuGetsForTesting()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"nuget-test-{Guid.NewGuid():N}");
        try
        {
            // Project with RequiredNuGetsForTesting - should be found
            var newTestDir = Path.Combine(tempDir, "tests", "NewStyleTests");
            Directory.CreateDirectory(newTestDir);
            File.WriteAllText(Path.Combine(newTestDir, "NewStyleTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <RequiredNuGetsForTesting>true</RequiredNuGetsForTesting>
                  </PropertyGroup>
                </Project>
                """);

            // Project with old RequiresNugets only - should NOT be found
            var oldTestDir = Path.Combine(tempDir, "tests", "OldStyleTests");
            Directory.CreateDirectory(oldTestDir);
            File.WriteAllText(Path.Combine(oldTestDir, "OldStyleTests.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <RequiresNugets>true</RequiresNugets>
                  </PropertyGroup>
                </Project>
                """);

            var result = NuGetDependentTestDetector.FindNuGetDependentTestProjects(tempDir);

            Assert.Single(result);
            Assert.Contains("tests/NewStyleTests/NewStyleTests.csproj", result);
            Assert.DoesNotContain(result, p => p.Contains("OldStyleTests"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
