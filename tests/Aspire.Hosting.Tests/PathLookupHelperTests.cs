// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Utils;

[Trait("Partition", "2")]
public class PathLookupHelperTests
{
    [Fact]
    public void FindFullPathFromPath_WhenCommandExistsOnPath_ReturnsFullPath()
    {
        // Arrange
        var existingFiles = new HashSet<string>
        {
            Path.Combine("/usr/bin", "mycommand")
        };

        // Act
        var result = PathLookupHelper.FindFullPathFromPath("mycommand", "/usr/bin:/usr/local/bin", ':', existingFiles.Contains, null);

        // Assert
        Assert.Equal(Path.Combine("/usr/bin", "mycommand"), result);
    }

    [Fact]
    public void FindFullPathFromPath_WhenCommandNotOnPath_ReturnsNull()
    {
        // Arrange
        static bool AlwaysFalse(string _) => false;

        // Act
        var result = PathLookupHelper.FindFullPathFromPath("mycommand", "/usr/bin:/usr/local/bin", ':', AlwaysFalse, null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindFullPathFromPath_WhenPathVariableIsEmpty_ReturnsNull()
    {
        // Arrange - when path is empty, no files should exist on that (non-existent) path
        static bool AlwaysFalse(string _) => false;

        // Act
        var result = PathLookupHelper.FindFullPathFromPath("mycommand", "", ':', AlwaysFalse, null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindFullPathFromPath_WhenPathVariableIsNull_ReturnsNull()
    {
        // Arrange
        static bool AlwaysFalse(string _) => false;

        // Act
        var result = PathLookupHelper.FindFullPathFromPath("mycommand", null, ':', AlwaysFalse, null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindFullPathFromPath_ReturnsFirstMatchFromPath()
    {
        // Arrange
        var existingFiles = new HashSet<string>
        {
            Path.Combine("/first/path", "mycommand"),
            Path.Combine("/second/path", "mycommand")
        };

        // Act
        var result = PathLookupHelper.FindFullPathFromPath("mycommand", "/first/path:/second/path", ':', existingFiles.Contains, null);

        // Assert
        Assert.Equal(Path.Combine("/first/path", "mycommand"), result);
    }

    [Fact]
    public void FindFullPathFromPath_UsesCorrectPathSeparator()
    {
        // Arrange - use platform-agnostic paths for testing
        var dir = Path.Combine("testdir", "bin");
        var expectedPath = Path.Combine(dir, "mycommand");
        var existingFiles = new HashSet<string>
        {
            expectedPath
        };

        // Act
        var result = PathLookupHelper.FindFullPathFromPath("mycommand", $"{dir};otherdir", ';', existingFiles.Contains, null);

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void FindFullPathFromPath_WithPathExtensions_FindsCommandWithExtension()
    {
        // Arrange - simulate Windows behavior where "code" is actually "code.CMD"
        var dir = Path.Combine("testdir", "bin");
        var expectedPath = Path.Combine(dir, "code.CMD");
        var existingFiles = new HashSet<string>
        {
            expectedPath
        };
        var pathExtensions = new[] { ".COM", ".EXE", ".BAT", ".CMD" };

        // Act - searching for "code" should find "code.CMD"
        var result = PathLookupHelper.FindFullPathFromPath("code", dir, ';', existingFiles.Contains, pathExtensions);

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void FindFullPathFromPath_WithPathExtensions_FindsFirstMatchingExtension()
    {
        // Arrange - when multiple extensions match, returns the first one in PATHEXT order
        var dir = Path.Combine("testdir", "bin");
        var exePath = Path.Combine(dir, "code.EXE");
        var cmdPath = Path.Combine(dir, "code.CMD");
        var existingFiles = new HashSet<string>
        {
            exePath,
            cmdPath
        };
        var pathExtensions = new[] { ".COM", ".EXE", ".BAT", ".CMD" };

        // Act - should find .EXE before .CMD because .EXE comes first in PATHEXT
        var result = PathLookupHelper.FindFullPathFromPath("code", dir, ';', existingFiles.Contains, pathExtensions);

        // Assert
        Assert.Equal(exePath, result);
    }

    [Fact]
    public void FindFullPathFromPath_WithPathExtensions_PrefersExtensionOverExactMatch()
    {
        // Arrange - when both exist, the extension-based file is preferred on Windows
        // This is important because Windows cannot execute extension-less scripts directly.
        // For example, "code" in VS Code's bin folder is a shell script that Windows can't run,
        // but "code.cmd" is the proper executable wrapper.
        var dir = Path.Combine("testdir", "bin");
        var exactPath = Path.Combine(dir, "code");
        var cmdPath = Path.Combine(dir, "code.CMD");
        var existingFiles = new HashSet<string>
        {
            exactPath,
            cmdPath
        };
        var pathExtensions = new[] { ".COM", ".EXE", ".BAT", ".CMD" };

        // Act - should find "code.CMD", not "code" (extension-based files preferred on Windows)
        var result = PathLookupHelper.FindFullPathFromPath("code", dir, ';', existingFiles.Contains, pathExtensions);

        // Assert
        Assert.Equal(cmdPath, result);
    }

    [Fact]
    public void FindFullPathFromPath_WithPathExtensions_FallsBackToExactMatchIfNoExtensionFound()
    {
        // Arrange - when no extension-based file exists, fall back to exact match
        var dir = Path.Combine("testdir", "bin");
        var exactPath = Path.Combine(dir, "mytool");
        var existingFiles = new HashSet<string>
        {
            exactPath
        };
        var pathExtensions = new[] { ".COM", ".EXE", ".BAT", ".CMD" };

        // Act - no extension files exist, so should fall back to exact match
        var result = PathLookupHelper.FindFullPathFromPath("mytool", dir, ';', existingFiles.Contains, pathExtensions);

        // Assert
        Assert.Equal(exactPath, result);
    }

    [Fact]
    public void FindFullPathFromPath_WithPathExtensions_DoesNotDuplicateExtension()
    {
        // Arrange - when command already has extension, don't duplicate it
        var dir = Path.Combine("testdir", "bin");
        var expectedPath = Path.Combine(dir, "code.CMD");
        var existingFiles = new HashSet<string>
        {
            expectedPath
        };
        var pathExtensions = new[] { ".COM", ".EXE", ".BAT", ".CMD" };

        // Act - searching for "code.CMD" should find "code.CMD", not "code.CMD.CMD"
        var result = PathLookupHelper.FindFullPathFromPath("code.CMD", dir, ';', existingFiles.Contains, pathExtensions);

        // Assert
        Assert.Equal(expectedPath, result);
    }

    [Fact]
    public void FindFullPathFromPath_WithPathExtensions_CommandWithExtensionNotFound_ReturnsNull()
    {
        // Arrange - command has known extension but file doesn't exist
        var dir = Path.Combine("testdir", "bin");
        var existingFiles = new HashSet<string>();
        var pathExtensions = new[] { ".COM", ".EXE", ".BAT", ".CMD" };

        // Act - searching for "code.EXE" when it doesn't exist should return null
        var result = PathLookupHelper.FindFullPathFromPath("code.EXE", dir, ';', existingFiles.Contains, pathExtensions);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindFullPathFromPath_WithPathExtensions_CommandWithExtension_DoesNotTryOtherExtensions()
    {
        // Arrange - when command has a known extension, don't try other extensions
        var dir = Path.Combine("testdir", "bin");
        var cmdPath = Path.Combine(dir, "code.CMD");
        var existingFiles = new HashSet<string>
        {
            cmdPath
        };
        var pathExtensions = new[] { ".COM", ".EXE", ".BAT", ".CMD" };

        // Act - searching for "code.EXE" should NOT find "code.CMD"
        // (we should not strip .EXE and try .CMD)
        var result = PathLookupHelper.FindFullPathFromPath("code.EXE", dir, ';', existingFiles.Contains, pathExtensions);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindFullPathFromPath_WithNullPathExtensions_DoesNotTryExtensions()
    {
        // Arrange - simulate non-Windows behavior where PATHEXT is not used
        var dir = "/usr/bin";
        var existingFiles = new HashSet<string>
        {
            Path.Combine(dir, "code.cmd")
        };

        // Act - with null pathExtensions, should NOT find "code.cmd" when searching for "code"
        var result = PathLookupHelper.FindFullPathFromPath("code", dir, ':', existingFiles.Contains, null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindFullPathFromPath_WithEmptyPathExtensions_DoesNotTryExtensions()
    {
        // Arrange
        var dir = "/usr/bin";
        var existingFiles = new HashSet<string>
        {
            Path.Combine(dir, "code.cmd")
        };

        // Act - with empty pathExtensions array, should NOT find "code.cmd" when searching for "code"
        var result = PathLookupHelper.FindFullPathFromPath("code", dir, ':', existingFiles.Contains, []);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindFullPathFromPath_WithPathExtensions_DirectoryOrderTakesPrecedenceOverExtensionOrder()
    {
        // Arrange - This test validates Windows PATH lookup semantics:
        // Windows searches each directory completely (trying all PATHEXT extensions) before moving to the next.
        // So if dir1 has code.CMD and dir2 has code.EXE, dir1/code.CMD should be found first,
        // even though .EXE comes before .CMD in PATHEXT order.
        var dir1 = Path.Combine("first", "bin");
        var dir2 = Path.Combine("second", "bin");
        var dir1CmdPath = Path.Combine(dir1, "code.CMD");
        var dir2ExePath = Path.Combine(dir2, "code.EXE");
        var existingFiles = new HashSet<string>
        {
            dir1CmdPath,  // Lower priority extension, but in first directory
            dir2ExePath   // Higher priority extension, but in second directory
        };
        var pathExtensions = new[] { ".COM", ".EXE", ".BAT", ".CMD" };

        // Act - should find dir1/code.CMD because directory order takes precedence
        var result = PathLookupHelper.FindFullPathFromPath("code", $"{dir1};{dir2}", ';', existingFiles.Contains, pathExtensions);

        // Assert - directory order (dir1) takes precedence over extension order (.EXE before .CMD)
        Assert.Equal(dir1CmdPath, result);
    }

    [Fact]
    public void FindFullPathFromPath_WithPathExtensions_ExactMatchInFirstDirBeatsExtensionInSecondDir()
    {
        // Arrange - exact match in first directory should be found before extension match in second directory
        var dir1 = Path.Combine("first", "bin");
        var dir2 = Path.Combine("second", "bin");
        var dir1ExactPath = Path.Combine(dir1, "mytool");
        var dir2ExePath = Path.Combine(dir2, "mytool.EXE");
        var existingFiles = new HashSet<string>
        {
            dir1ExactPath,  // Exact match in first directory
            dir2ExePath     // Extension match in second directory
        };
        var pathExtensions = new[] { ".COM", ".EXE", ".BAT", ".CMD" };

        // Act - should find dir1/mytool because directory order takes precedence
        // Note: Within a directory, extension matches are tried first, then exact match.
        // But across directories, the first directory's results (including fallback) take precedence.
        var result = PathLookupHelper.FindFullPathFromPath("mytool", $"{dir1};{dir2}", ';', existingFiles.Contains, pathExtensions);

        // Assert - dir1 exact match is found because no extension match exists in dir1
        Assert.Equal(dir1ExactPath, result);
    }
}
