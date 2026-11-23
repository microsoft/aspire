// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Tasks;
using Xunit;

namespace Aspire.Hosting.Sdk.Tests;

public class CommandPathResolverTests
{
    private static readonly string s_root = Path.Combine(Path.GetTempPath(), "aspire-command-path-resolver-tests");
    private static readonly string s_firstDirectory = Path.Combine(s_root, "first");
    private static readonly string s_secondDirectory = Path.Combine(s_root, "second");

    [Fact]
    public void PathExtensionsAreTriedInOrderAndOnlyTheFirstMatchInEachDirectoryIsReturned()
    {
        var existingFiles = CreateFileSet(
            Path.Combine(s_firstDirectory, "aspire.CMD"),
            Path.Combine(s_firstDirectory, "aspire.EXE"),
            Path.Combine(s_firstDirectory, "aspire"),
            Path.Combine(s_secondDirectory, "aspire"));

        var candidates = CommandPathResolver.EnumerateFromPath(
            "aspire",
            string.Join(Path.PathSeparator.ToString(), s_firstDirectory, s_secondDirectory),
            [".EXE", ".CMD"],
            existingFiles.Contains);

        Assert.Equal([Path.Combine(s_firstDirectory, "aspire.EXE"), Path.Combine(s_secondDirectory, "aspire")], candidates);
    }

    [Fact]
    public void CommandWithAPathExtensionIsSearchedAsIs()
    {
        var existingFiles = CreateFileSet(
            Path.Combine(s_firstDirectory, "dnx.cmd.EXE"),
            Path.Combine(s_firstDirectory, "dnx.cmd"));

        var candidates = CommandPathResolver.EnumerateFromPath("dnx.cmd", s_firstDirectory, [".EXE", ".CMD"], existingFiles.Contains);

        Assert.Equal([Path.Combine(s_firstDirectory, "dnx.cmd")], candidates);
    }

    [Fact]
    public void DuplicatePathEntriesAreSearchedOnce()
    {
        var existingFiles = CreateFileSet(Path.Combine(s_firstDirectory, "aspire"));
        var path = string.Join(Path.PathSeparator.ToString(), s_firstDirectory, s_firstDirectory, string.Empty);

        var candidates = CommandPathResolver.EnumerateFromPath("aspire", path, pathExtensions: null, existingFiles.Contains);

        Assert.Equal([Path.Combine(s_firstDirectory, "aspire")], candidates);
    }

    [Fact]
    public void QuotedPathEntriesAreUnquotedOnlyOnWindows()
    {
        var existingFiles = CreateFileSet(Path.Combine(s_firstDirectory, "aspire"));

        var candidates = CommandPathResolver.EnumerateFromPath("aspire", $" \"{s_firstDirectory}\" ", pathExtensions: null, existingFiles.Contains);

        // Unix PATH has no quoting, so the quotes and spaces are part of a directory name that doesn't exist.
        Assert.Equal(OperatingSystem.IsWindows() ? [Path.Combine(s_firstDirectory, "aspire")] : [], candidates);
    }

    [Fact]
    public void FileProbeFailuresSkipTheCandidate()
    {
        var candidates = CommandPathResolver.EnumerateFromPath(
            "aspire",
            string.Join(Path.PathSeparator.ToString(), s_firstDirectory, s_secondDirectory),
            pathExtensions: null,
            path => path.StartsWith(s_firstDirectory, StringComparison.Ordinal) ? throw new UnauthorizedAccessException() : true);

        Assert.Equal([Path.Combine(s_secondDirectory, "aspire")], candidates);
    }

    [Theory]
    [InlineData(null, new[] { ".COM", ".EXE", ".BAT", ".CMD" })]
    [InlineData(";", new[] { ".COM", ".EXE", ".BAT", ".CMD" })]
    [InlineData(".EXE;.PS1;", new[] { ".EXE", ".PS1" })]
    public void WindowsPathExtensionsFallBackToCommandShellDefaultsWhenUnset(string? pathExtensions, string[] expected)
    {
        Assert.Equal(expected, CommandPathResolver.GetWindowsPathExtensions(pathExtensions));
    }

    // CommandPathResolver mirrors src/Shared/PathLookupHelper.cs because the tasks must also build for net472.
    // These cases pin the lookup rules the two must share. Collapsing duplicate PATH entries is intentionally
    // task-only and is covered by the tests above.
    [Theory]
    [InlineData("aspire", new[] { "first/aspire", "second/aspire" }, null, new[] { "first/aspire", "second/aspire" })]
    [InlineData("aspire", new[] { "first/aspire.CMD", "first/aspire.EXE", "first/aspire", "second/aspire.BAT" }, new[] { ".EXE", ".CMD", ".BAT" }, new[] { "first/aspire.EXE", "second/aspire.BAT" })]
    [InlineData("aspire", new[] { "first/aspire" }, new[] { ".EXE" }, new[] { "first/aspire" })]
    [InlineData("aspire", new[] { "first/aspire.EXE" }, new string[0], new string[0])]
    [InlineData("dnx.cmd", new[] { "first/dnx.cmd.EXE", "first/dnx.cmd" }, new[] { ".EXE", ".CMD" }, new[] { "first/dnx.cmd" })]
    [InlineData("aspire", new string[0], new[] { ".EXE" }, new string[0])]
    public void ResolvesConsistentlyWithPathLookupHelper(string command, string[] files, string[]? pathExtensions, string[] expected)
    {
        var existingFiles = CreateFileSet(files.Select(ToFullPath).ToArray());
        // Includes an empty entry, which both helpers skip.
        var path = string.Join(Path.PathSeparator.ToString(), s_firstDirectory, string.Empty, s_secondDirectory);

        var taskCandidates = CommandPathResolver.EnumerateFromPath(command, path, pathExtensions, existingFiles.Contains).ToArray();
        var sharedCandidates = PathLookupHelper.FindAllFullPathsFromPath(command, path, Path.PathSeparator, existingFiles.Contains, pathExtensions).ToArray();

        Assert.Equal(expected.Select(ToFullPath), taskCandidates);
        Assert.Equal(sharedCandidates, taskCandidates);
    }

    [Fact]
    public void QuotedPathEntriesResolveConsistentlyWithPathLookupHelper()
    {
        var existingFiles = CreateFileSet(Path.Combine(s_secondDirectory, "aspire"));
        var path = string.Join(Path.PathSeparator.ToString(), s_firstDirectory, $" \"{s_secondDirectory}\" ");

        var taskCandidates = CommandPathResolver.EnumerateFromPath("aspire", path, pathExtensions: null, existingFiles.Contains).ToArray();
        var sharedCandidates = PathLookupHelper.FindAllFullPathsFromPath("aspire", path, Path.PathSeparator, existingFiles.Contains, pathExtensions: null).ToArray();

        Assert.Equal(OperatingSystem.IsWindows() ? [Path.Combine(s_secondDirectory, "aspire")] : [], taskCandidates);
        Assert.Equal(sharedCandidates, taskCandidates);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(";")]
    [InlineData(".EXE;.PS1;")]
    public void WindowsPathExtensionsMatchPathLookupHelper(string? pathExtensions)
    {
        Assert.Equal(PathLookupHelper.GetWindowsPathExtensions(pathExtensions), CommandPathResolver.GetWindowsPathExtensions(pathExtensions));
    }

    private static string ToFullPath(string relativePath) => Path.Combine(s_root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static HashSet<string> CreateFileSet(params string[] paths) => new(paths, StringComparer.OrdinalIgnoreCase);
}
