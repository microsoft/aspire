// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests.Helpers;

internal sealed class TestAppHostRepository : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-tray-repository-");

    public string CommonDirectory => Path.Combine(_directory.FullName, "repo", ".git");

    public string CreateWorktree(string name, string? branch, string source = "Shop.AppHost/apphost.cs")
    {
        Directory.CreateDirectory(CommonDirectory);
        var root = Path.Combine(_directory.FullName, name);
        Directory.CreateDirectory(root);
        var git = Path.Combine(CommonDirectory, "worktrees", name);
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: " + Path.GetRelativePath(root, git) + "\n");
        File.WriteAllText(Path.Combine(git, "commondir"), "../..\n");
        File.WriteAllText(Path.Combine(git, "HEAD"), branch is null ? new string('a', 40) + "\n" : "ref: refs/heads/" + branch + "\n");
        var path = Path.Combine(root, source);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
