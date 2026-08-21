// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Tests.Utils;

public class PathNormalizerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ResolveSymlinks_IsIdempotent_WhenPathHasNoSymlinks()
    {
        // The input itself may sit under a symlinked root (for example /var -> /private/var
        // on macOS), so we cannot assert the result equals the input. We can assert
        // idempotence: a path with no remaining symlinks must resolve to itself.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var subdir = workspace.WorkspaceRoot.CreateSubdirectory("App");
        var file = new FileInfo(Path.Combine(subdir.FullName, "app.csproj"));
        File.WriteAllText(file.FullName, "<Project />");

        var firstPass = PathNormalizer.ResolveSymlinks(file.FullName);
        var secondPass = PathNormalizer.ResolveSymlinks(firstPass);

        Assert.Equal(firstPass, secondPass);
    }

    [Fact]
    public void ResolveSymlinks_ReturnsInputUnchanged_WhenEmpty()
    {
        Assert.Equal(string.Empty, PathNormalizer.ResolveSymlinks(string.Empty));
    }

    [Fact]
    public void ResolveSymlinks_ResolvesFinalFileSymlink()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var target = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "target.csproj"));
        File.WriteAllText(target.FullName, "<Project />");

        var linkPath = Path.Combine(workspace.WorkspaceRoot.FullName, "link.csproj");
        TryCreateSymlink(linkPath, target.FullName, isDirectory: false);

        var resolved = PathNormalizer.ResolveSymlinks(linkPath);

        // The link's final target should be canonical-equal to the real file. We use
        // ResolveSymlinks on the target as well to account for the temp directory itself
        // sitting under a symlinked root (for example /tmp -> /private/tmp on macOS).
        Assert.Equal(PathNormalizer.ResolveSymlinks(target.FullName), resolved);
    }

    [Fact]
    public void ResolveSymlinks_ResolvesIntermediateDirectorySymlink()
    {
        // The L5 repro relies on a symlink that is NOT the final segment: on macOS,
        // /tmp -> /private/tmp, and the apphost lives at /tmp/L5/x.cs. A single call to
        // Directory.ResolveLinkTarget on the full path would not unwrap /tmp, so the
        // implementation must walk segments.
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var nested = realDirectory.CreateSubdirectory("nested");
        var file = new FileInfo(Path.Combine(nested.FullName, "app.csproj"));
        File.WriteAllText(file.FullName, "<Project />");

        var linkDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "link");
        TryCreateSymlink(linkDirectory, realDirectory.FullName, isDirectory: true);

        // Path through the link should resolve to the same canonical path as the path
        // through the real directory.
        var pathThroughLink = Path.Combine(linkDirectory, "nested", "app.csproj");

        var resolvedThroughLink = PathNormalizer.ResolveSymlinks(pathThroughLink);
        var resolvedThroughReal = PathNormalizer.ResolveSymlinks(file.FullName);

        Assert.Equal(resolvedThroughReal, resolvedThroughLink);
    }

    [Fact]
    public void ResolveSymlinks_PreservesPath_WhenLinkIsBroken()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var missingTarget = Path.Combine(workspace.WorkspaceRoot.FullName, "missing.csproj");
        var linkPath = Path.Combine(workspace.WorkspaceRoot.FullName, "broken-link.csproj");
        TryCreateSymlink(linkPath, missingTarget, isDirectory: false);

        // A broken link should not throw — the method must fall back to returning the
        // path so callers can still surface a useful "file not found" error.
        var resolved = PathNormalizer.ResolveSymlinks(linkPath);

        Assert.False(string.IsNullOrEmpty(resolved));
    }

    [Fact]
    public void ResolveToFilesystemPath_UsesEnumeratedUnicodeNormalization()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var testRoot = workspace.WorkspaceRoot.CreateSubdirectory("unicode-normalization");
        var decomposedName = "Cafe\u0301";
        var composedName = decomposedName.Normalize(NormalizationForm.FormC);
        var decomposedPath = Path.Combine(testRoot.FullName, decomposedName);
        Directory.CreateDirectory(decomposedPath);

        var enumeratedPath = Assert.Single(Directory.EnumerateDirectories(testRoot.FullName));
        var composedPath = Path.Combine(testRoot.FullName, composedName);
        Assert.SkipUnless(
            Directory.Exists(composedPath),
            "The current filesystem does not resolve normalization-equivalent path segments.");
        Assert.SkipWhen(
            enumeratedPath.Equals(composedPath, StringComparison.Ordinal),
            "The current filesystem enumerates the candidate with the same normalization form.");

        Assert.Equal(enumeratedPath, PathNormalizer.ResolveToFilesystemPath(composedPath));
    }

    [Fact]
    public void ResolveToFilesystemPath_UsesUppercaseWindowsDriveLetter()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Drive-letter casing only applies on Windows.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var file = new FileInfo(Path.Combine(workspace.WorkspaceRoot.FullName, "app.csproj"));
        File.WriteAllText(file.FullName, "<Project />");
        Assert.SkipUnless(
            file.FullName.Length >= 3 &&
            file.FullName[1] == ':' &&
            file.FullName[2] == Path.DirectorySeparatorChar,
            "The temporary workspace is not on a drive-letter path.");

        var lowercaseDrivePath = $"{char.ToLowerInvariant(file.FullName[0])}{file.FullName[1..]}";
        var uppercaseDrivePath = $"{char.ToUpperInvariant(file.FullName[0])}{file.FullName[1..]}";

        Assert.NotEqual(lowercaseDrivePath, uppercaseDrivePath);
        Assert.Equal(uppercaseDrivePath, PathNormalizer.ResolveToFilesystemPath(lowercaseDrivePath));
    }

    [Fact]
    public void TryResolveToFilesystemPath_ReturnsFalseForMissingWindowsDriveRoot()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Drive-letter roots only apply on Windows.");

        var missingDriveLetter = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(value => (char)value)
            .FirstOrDefault(driveLetter => !Directory.Exists($"{driveLetter}:{Path.DirectorySeparatorChar}"));
        Assert.SkipWhen(missingDriveLetter == default, "All drive letters are in use.");

        var missingDriveRoot = $"{char.ToLowerInvariant(missingDriveLetter)}:{Path.DirectorySeparatorChar}";

        Assert.False(PathNormalizer.TryResolveToFilesystemPath(missingDriveRoot, out var resolvedPath));
        Assert.Equal(missingDriveRoot, resolvedPath);
    }

    private static void TryCreateSymlink(string linkPath, string targetPath, bool isDirectory)
    {
        try
        {
            if (isDirectory)
            {
                Directory.CreateSymbolicLink(linkPath, targetPath);
            }
            else
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // Creating symlinks on Windows requires either administrator rights or
            // Developer Mode. Skip cleanly on environments that don't allow it rather
            // than failing the test for an environment reason.
            Assert.Skip($"Cannot create symbolic links in this environment: {ex.Message}");
        }
        catch (IOException ex)
        {
            Assert.Skip($"Symbolic link creation failed in this environment: {ex.Message}");
        }
    }
}
