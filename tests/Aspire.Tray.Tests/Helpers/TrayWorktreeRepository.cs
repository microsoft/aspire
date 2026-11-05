// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray.Tests.Helpers;

internal sealed class TrayWorktreeRepository : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("aspire-tray-worktrees-");
    public string CommonDirectory => Path.Combine(_directory.FullName, "main", ".git");

    public string CreateHost(string checkout, string? branch, string relativePath = "Shop.AppHost/apphost.cs")
    {
        Directory.CreateDirectory(CommonDirectory);
        if (!File.Exists(Path.Combine(CommonDirectory, "HEAD")))
        {
            File.WriteAllText(Path.Combine(CommonDirectory, "HEAD"), "ref: refs/heads/main");
        }
        var root = Path.Combine(_directory.FullName, checkout);
        Directory.CreateDirectory(root);
        var git = checkout == "main" ? CommonDirectory : Path.Combine(CommonDirectory, "worktrees", checkout);
        Directory.CreateDirectory(git);
        if (checkout != "main")
        {
            File.WriteAllText(Path.Combine(root, ".git"), "gitdir: " + Path.GetRelativePath(root, git));
            File.WriteAllText(Path.Combine(git, "commondir"), "../..");
            File.WriteAllText(Path.Combine(git, "gitdir"), Path.GetRelativePath(git, Path.Combine(root, ".git")));
        }
        File.WriteAllText(Path.Combine(git, "HEAD"), branch is null ? new string('a', 40) : "ref: refs/heads/" + branch);
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
