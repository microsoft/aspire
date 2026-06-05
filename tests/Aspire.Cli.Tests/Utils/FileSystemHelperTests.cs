// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Tests.Utils;

public class FileSystemHelperTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void CopyDirectory_WithSimpleFiles_CopiesAllFiles()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "destination");

        // Create some test files
        File.WriteAllText(Path.Combine(sourceDir.FullName, "file1.txt"), "content1");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "file2.txt"), "content2");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "file3.cs"), "using System;");

        // Act
        FileSystemHelper.CopyDirectory(sourceDir.FullName, destDir);

        // Assert
        Assert.True(Directory.Exists(destDir));
        Assert.True(File.Exists(Path.Combine(destDir, "file1.txt")));
        Assert.True(File.Exists(Path.Combine(destDir, "file2.txt")));
        Assert.True(File.Exists(Path.Combine(destDir, "file3.cs")));
        
        Assert.Equal("content1", File.ReadAllText(Path.Combine(destDir, "file1.txt")));
        Assert.Equal("content2", File.ReadAllText(Path.Combine(destDir, "file2.txt")));
        Assert.Equal("using System;", File.ReadAllText(Path.Combine(destDir, "file3.cs")));
    }

    [Fact]
    public void CopyDirectory_WithSubdirectories_CopiesRecursively()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "destination");

        // Create nested directory structure
        var subDir1 = sourceDir.CreateSubdirectory("subdir1");
        var subDir2 = subDir1.CreateSubdirectory("subdir2");
        
        File.WriteAllText(Path.Combine(sourceDir.FullName, "root.txt"), "root content");
        File.WriteAllText(Path.Combine(subDir1.FullName, "level1.txt"), "level 1 content");
        File.WriteAllText(Path.Combine(subDir2.FullName, "level2.txt"), "level 2 content");

        // Act
        FileSystemHelper.CopyDirectory(sourceDir.FullName, destDir);

        // Assert
        Assert.True(Directory.Exists(destDir));
        Assert.True(File.Exists(Path.Combine(destDir, "root.txt")));
        Assert.True(Directory.Exists(Path.Combine(destDir, "subdir1")));
        Assert.True(File.Exists(Path.Combine(destDir, "subdir1", "level1.txt")));
        Assert.True(Directory.Exists(Path.Combine(destDir, "subdir1", "subdir2")));
        Assert.True(File.Exists(Path.Combine(destDir, "subdir1", "subdir2", "level2.txt")));
        
        Assert.Equal("root content", File.ReadAllText(Path.Combine(destDir, "root.txt")));
        Assert.Equal("level 1 content", File.ReadAllText(Path.Combine(destDir, "subdir1", "level1.txt")));
        Assert.Equal("level 2 content", File.ReadAllText(Path.Combine(destDir, "subdir1", "subdir2", "level2.txt")));
    }

    [Fact]
    public void CopyDirectory_WithEmptyDirectory_CreatesDestination()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("empty_source");
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "empty_destination");

        // Act
        FileSystemHelper.CopyDirectory(sourceDir.FullName, destDir);

        // Assert
        Assert.True(Directory.Exists(destDir));
        Assert.Empty(Directory.GetFiles(destDir));
        Assert.Empty(Directory.GetDirectories(destDir));
    }

    [Fact]
    public void CopyDirectory_WithNonExistentSource_ThrowsDirectoryNotFoundException()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nonExistentSource = Path.Combine(workspace.WorkspaceRoot.FullName, "nonexistent");
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "destination");

        // Act & Assert
        Assert.Throws<DirectoryNotFoundException>(() => 
            FileSystemHelper.CopyDirectory(nonExistentSource, destDir));
    }

    [Fact]
    public void CopyDirectory_WithNullSource_ThrowsArgumentNullException()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "destination");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            FileSystemHelper.CopyDirectory(null!, destDir));
    }

    [Fact]
    public void CopyDirectory_WithNullDestination_ThrowsArgumentNullException()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            FileSystemHelper.CopyDirectory(sourceDir.FullName, null!));
    }

    [Fact]
    public void CopyDirectory_WithEmptySource_ThrowsArgumentException()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "destination");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            FileSystemHelper.CopyDirectory(string.Empty, destDir));
    }

    [Fact]
    public void CopyDirectory_WithEmptyDestination_ThrowsArgumentException()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            FileSystemHelper.CopyDirectory(sourceDir.FullName, string.Empty));
    }

    [Fact]
    public void CopyDirectory_PreservesFileContent_WithBinaryFiles()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "destination");

        // Create a binary file with random content
        var binaryFilePath = Path.Combine(sourceDir.FullName, "binary.dat");
        var randomBytes = new byte[1024];
        Random.Shared.NextBytes(randomBytes);
        File.WriteAllBytes(binaryFilePath, randomBytes);

        // Act
        FileSystemHelper.CopyDirectory(sourceDir.FullName, destDir);

        // Assert
        var copiedFilePath = Path.Combine(destDir, "binary.dat");
        Assert.True(File.Exists(copiedFilePath));
        
        var copiedBytes = File.ReadAllBytes(copiedFilePath);
        Assert.Equal(randomBytes, copiedBytes);
    }

    [Fact]
    public void CopyDirectory_WithMultipleLevelsOfSubdirectories_CopiesAll()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "destination");

        // Create a deep directory structure
        var current = sourceDir;
        for (int i = 0; i < 5; i++)
        {
            current = current.CreateSubdirectory($"level{i}");
            File.WriteAllText(Path.Combine(current.FullName, $"file{i}.txt"), $"content at level {i}");
        }

        // Act
        FileSystemHelper.CopyDirectory(sourceDir.FullName, destDir);

        // Assert
        var currentDest = destDir;
        for (int i = 0; i < 5; i++)
        {
            currentDest = Path.Combine(currentDest, $"level{i}");
            Assert.True(Directory.Exists(currentDest));
            var filePath = Path.Combine(currentDest, $"file{i}.txt");
            Assert.True(File.Exists(filePath));
            Assert.Equal($"content at level {i}", File.ReadAllText(filePath));
        }
    }

    [Fact]
    public void CopyDirectory_WithExistingDestinationFiles_OverwritesThem()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "destination");

        // Create source files with new content
        File.WriteAllText(Path.Combine(sourceDir.FullName, "file1.txt"), "new content 1");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "file2.txt"), "new content 2");
        
        var subDir = sourceDir.CreateSubdirectory("obj");
        File.WriteAllText(Path.Combine(subDir.FullName, "project.assets.json"), "new assets");
        File.WriteAllText(Path.Combine(subDir.FullName, "project.csproj.nuget.dgspec.json"), "new dgspec");

        // Create destination directory with existing files that have old content
        Directory.CreateDirectory(destDir);
        File.WriteAllText(Path.Combine(destDir, "file1.txt"), "old content 1");
        File.WriteAllText(Path.Combine(destDir, "file2.txt"), "old content 2");
        
        var destSubDir = Directory.CreateDirectory(Path.Combine(destDir, "obj"));
        File.WriteAllText(Path.Combine(destSubDir.FullName, "project.assets.json"), "old assets");
        File.WriteAllText(Path.Combine(destSubDir.FullName, "project.csproj.nuget.dgspec.json"), "old dgspec");

        // Act
        FileSystemHelper.CopyDirectory(sourceDir.FullName, destDir, overwrite: true);

        // Assert - files should be overwritten with new content
        Assert.Equal("new content 1", File.ReadAllText(Path.Combine(destDir, "file1.txt")));
        Assert.Equal("new content 2", File.ReadAllText(Path.Combine(destDir, "file2.txt")));
        Assert.Equal("new assets", File.ReadAllText(Path.Combine(destDir, "obj", "project.assets.json")));
        Assert.Equal("new dgspec", File.ReadAllText(Path.Combine(destDir, "obj", "project.csproj.nuget.dgspec.json")));
    }

    [Fact]
    public void CopyDirectory_WithExistingDestinationFilesAndOverwriteFalse_ThrowsIOException()
    {
        // Arrange
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var destDir = Path.Combine(workspace.WorkspaceRoot.FullName, "destination");

        // Create source file
        File.WriteAllText(Path.Combine(sourceDir.FullName, "file1.txt"), "new content");

        // Create destination directory with existing file
        Directory.CreateDirectory(destDir);
        File.WriteAllText(Path.Combine(destDir, "file1.txt"), "old content");

        // Act & Assert - should throw IOException when overwrite is false
        Assert.Throws<IOException>(() => 
            FileSystemHelper.CopyDirectory(sourceDir.FullName, destDir, overwrite: false));
    }

    [Fact]
    public void ShortenPaths_UniqueFilenames_ReturnsJustFilename()
    {
        var paths = new List<string>
        {
            "/home/user/folder1/App1.AppHost.csproj",
            "/home/user/folder2/App2.AppHost.csproj"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal("App1.AppHost.csproj", result[paths[0]]);
        Assert.Equal("App2.AppHost.csproj", result[paths[1]]);
    }

    [Fact]
    public void ShortenPaths_DuplicateFilenames_AddsParentDirectoryToDisambiguate()
    {
        var paths = new List<string>
        {
            "/home/user/folder1/Project.csproj",
            "/home/user/folder2/Project.csproj"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(Path.Combine("folder1", "Project.csproj"), result[paths[0]]);
        Assert.Equal(Path.Combine("folder2", "Project.csproj"), result[paths[1]]);
    }

    [Fact]
    public void ShortenPaths_DuplicateFilenamesWithBackslashes_AddsParentDirectoryToDisambiguate()
    {
        var paths = new List<string>
        {
            @"C:\folder1\Project.csproj",
            @"C:\folder2\Project.csproj"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(Path.Combine("folder1", "Project.csproj"), result[paths[0]]);
        Assert.Equal(Path.Combine("folder2", "Project.csproj"), result[paths[1]]);
    }

    [Fact]
    public void ShortenPaths_DuplicateFilenamesWithSameParent_AddsMoreSegments()
    {
        var paths = new List<string>
        {
            "/home/a/shared/Project.csproj",
            "/home/b/shared/Project.csproj"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(Path.Combine("a", "shared", "Project.csproj"), result[paths[0]]);
        Assert.Equal(Path.Combine("b", "shared", "Project.csproj"), result[paths[1]]);
    }

    [Fact]
    public void ShortenPaths_SinglePath_CsFile_ReturnsParentAndFilename()
    {
        var paths = new List<string> { "/home/user/repos/MyApp/AppHost.cs" };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(Path.Combine("MyApp", "AppHost.cs"), result[paths[0]]);
    }

    [Fact]
    public void ShortenPaths_SinglePath_Csproj_ReturnsJustFilename()
    {
        var paths = new List<string> { "/home/user/repos/MyApp/MyApp.AppHost.csproj" };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal("MyApp.AppHost.csproj", result[paths[0]]);
    }

    [Fact]
    public void ShortenPaths_SingleCsFile_ReturnsParentAndFilename()
    {
        var paths = new List<string>
        {
            "/home/user/App1/AppHost.cs"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(Path.Combine("App1", "AppHost.cs"), result[paths[0]]);
    }

    [Fact]
    public void ShortenPaths_UniqueCsFiles_ReturnsParentAndFilename()
    {
        var paths = new List<string>
        {
            "/home/user/App1/AppHost.cs",
            "/home/user/App2/AppHost.cs"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(Path.Combine("App1", "AppHost.cs"), result[paths[0]]);
        Assert.Equal(Path.Combine("App2", "AppHost.cs"), result[paths[1]]);
    }

    [Fact]
    public void ShortenPaths_DuplicateCsFiles_AddMoreSegments()
    {
        var paths = new List<string>
        {
            "/home/user/a/src/AppHost.cs",
            "/home/user/b/src/AppHost.cs"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(Path.Combine("a", "src", "AppHost.cs"), result[paths[0]]);
        Assert.Equal(Path.Combine("b", "src", "AppHost.cs"), result[paths[1]]);
    }

    [Fact]
    public void ShortenPaths_MixedCsprojAndCsFiles()
    {
        var paths = new List<string>
        {
            "/home/user/App1/App1.AppHost.csproj",
            "/home/user/App2/AppHost.cs"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal("App1.AppHost.csproj", result[paths[0]]);
        Assert.Equal(Path.Combine("App2", "AppHost.cs"), result[paths[1]]);
    }

    [Fact]
    public void ShortenPaths_EmptyList_ReturnsEmptyDictionary()
    {
        var result = FileSystemHelper.ShortenPaths(Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public void ShortenPaths_MixOfUniqueAndDuplicateFilenames()
    {
        var paths = new List<string>
        {
            "/a/folder1/Project.csproj",
            "/a/folder2/Project.csproj",
            "/a/folder3/Unique.csproj"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(Path.Combine("folder1", "Project.csproj"), result[paths[0]]);
        Assert.Equal(Path.Combine("folder2", "Project.csproj"), result[paths[1]]);
        Assert.Equal("Unique.csproj", result[paths[2]]);
    }

    [Fact]
    public void ShortenPaths_DuplicateFilenamesExhaustSegments_ReturnsFullPath()
    {
        var paths = new List<string>
        {
            @"C:\folder\Project.csproj",
            @"D:\folder\Project.csproj"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(@"C:\folder\Project.csproj", result[paths[0]]);
        Assert.Equal(@"D:\folder\Project.csproj", result[paths[1]]);
    }

    [Fact]
    public void ShortenPaths_PathsDifferingOnlyByCase_TreatedAsDistinctOnCaseSensitiveOS()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Case-sensitive filesystem test only runs on Linux.");

        var paths = new List<string>
        {
            "/repo/Folder/Project.csproj",
            "/repo/folder/Project.csproj"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Equal(Path.Combine("Folder", "Project.csproj"), result[paths[0]]);
        Assert.Equal(Path.Combine("folder", "Project.csproj"), result[paths[1]]);
    }

    [Fact]
    public void ShortenPaths_DuplicatePaths_ReturnsSingleEntry()
    {
        var paths = new List<string>
        {
            "/home/user/folder1/Project.csproj",
            "/home/user/folder1/Project.csproj"
        };

        var result = FileSystemHelper.ShortenPaths(paths);

        Assert.Single(result);
        Assert.Equal("Project.csproj", result[paths[0]]);
    }
}
