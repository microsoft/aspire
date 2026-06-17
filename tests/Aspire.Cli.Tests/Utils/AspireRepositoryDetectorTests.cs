// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if DEBUG

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class AspireRepositoryDetectorTests : IDisposable
{
    private const string RepoRootEnvironmentVariableName = "ASPIRE_REPO_ROOT";
    private readonly List<string> _directoriesToDelete = [];
    private readonly string? _originalRepoRoot = Environment.GetEnvironmentVariable(RepoRootEnvironmentVariableName);

    public AspireRepositoryDetectorTests()
    {
        AspireRepositoryDetector.ResetCache();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RepoRootEnvironmentVariableName, _originalRepoRoot);
        AspireRepositoryDetector.ResetCache();

        foreach (var directory in _directoriesToDelete)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void DetectRepositoryRoot_ReturnsDirectoryContainingAspireSolution()
    {
        var repoRoot = CreateTempDirectory();
        File.WriteAllText(Path.Combine(repoRoot, "Aspire.slnx"), string.Empty);

        var nestedDirectory = Directory.CreateDirectory(Path.Combine(repoRoot, "src", "Project")).FullName;

        var detectedRoot = AspireRepositoryDetector.DetectRepositoryRoot(nestedDirectory);

        Assert.Equal(repoRoot, detectedRoot);
    }

    [Fact]
    public void DetectRepositoryRoot_UsesEnvironmentVariable_WhenNoSolutionFound()
    {
        var repoRoot = CreateTempDirectory();
        var workingDirectory = CreateTempDirectory();

        Environment.SetEnvironmentVariable(RepoRootEnvironmentVariableName, repoRoot);

        var detectedRoot = AspireRepositoryDetector.DetectRepositoryRoot(workingDirectory);

        Assert.Equal(repoRoot, detectedRoot);
    }

    [Fact]
    public void DetectRepositoryRoot_PrefersSolutionSearchOverEnvironmentVariable()
    {
        var repoRoot = CreateTempDirectory();
        File.WriteAllText(Path.Combine(repoRoot, "Aspire.slnx"), string.Empty);

        var envRoot = CreateTempDirectory();
        Environment.SetEnvironmentVariable(RepoRootEnvironmentVariableName, envRoot);

        var nestedDirectory = Directory.CreateDirectory(Path.Combine(repoRoot, "playground", "polyglot")).FullName;

        var detectedRoot = AspireRepositoryDetector.DetectRepositoryRoot(nestedDirectory);

        Assert.Equal(repoRoot, detectedRoot);
    }

    private string CreateTempDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("aspire-repo-detector-tests-").FullName;
        _directoriesToDelete.Add(directory);
        return directory;
    }
}

#endif
