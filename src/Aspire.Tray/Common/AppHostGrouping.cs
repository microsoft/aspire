// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

internal sealed record AppHostRepository(string CommonDirectory, string RelativeAppHostPath, string WorktreeDirectory, string? Branch)
{
    public static AppHostRepository? Read(string appHostPath)
    {
        try
        {
            for (var directory = Path.GetDirectoryName(appHostPath); directory is not null; directory = Path.GetDirectoryName(directory))
            {
                var git = Path.Combine(directory, ".git");
                if (File.Exists(git))
                {
                    // Linked worktrees/submodules use a file: "gitdir: /repo/.git/worktrees/cart".
                    // Relative gitdir paths are relative to this file, not the process directory.
                    var pointer = ReadMetadata(git).Trim();
                    if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal))
                    {
                        return null;
                    }
                    git = Path.GetFullPath(pointer[8..], directory);
                }
                else if (!Directory.Exists(git))
                {
                    continue;
                }

                var commonFile = Path.Combine(git, "commondir");
                // Git's commondir usually contains "../.." in a linked worktree.
                // HEAD belongs to the worktree; repository identity belongs to the common dir.
                // https://git-scm.com/docs/gitrepository-layout
                var common = File.Exists(commonFile) ? Path.GetFullPath(ReadMetadata(commonFile).Trim(), git) : git;
                if (!Directory.Exists(common))
                {
                    return null;
                }
                string? branch = null;
                var headFile = Path.Combine(git, "HEAD");
                if (File.Exists(headFile))
                {
                    var head = ReadMetadata(headFile).Trim();
                    // "ref: refs/heads/feature/cart" preserves slashes in branch names.
                    // A detached HEAD contains a commit ID and uses the worktree folder label.
                    const string Prefix = "ref: refs/heads/";
                    branch = head.StartsWith(Prefix, StringComparison.Ordinal) ? head[Prefix.Length..] : null;
                }
                return new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(common)),
                    Path.GetRelativePath(directory, appHostPath), directory, branch);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            // Git metadata is optional presentation data. Never infer a relationship from
            // display names when a checkout is missing or its metadata cannot be read.
        }
        return null;
    }

    private static string ReadMetadata(string path)
    {
        using var reader = File.OpenText(path);
        var buffer = new char[4097];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        return length <= 4096 ? new string(buffer, 0, length) : throw new IOException("Git metadata is too large.");
    }
}

internal sealed record AppHostMenuGroup(string Title, IReadOnlyList<AppHostMenuItem> Instances)
{
    public bool IsGroup => Instances.Count > 1;

    public AppHostHealth GetHealth(bool discoveryAvailable)
    {
        var health = Instances.Select(host => !discoveryAvailable ? AppHostHealth.Unknown
            : host.Error is not null ? AppHostHealth.Unhealthy
            : host.IsStarting || host.IsStopping ? AppHostHealth.Warning
            : host.IsRunning ? host.Health : AppHostHealth.Unknown).ToArray();
        // Unknown/stopped children must never make a partially known group look healthy.
        return health.Contains(AppHostHealth.Unhealthy) ? AppHostHealth.Unhealthy
            : health.Contains(AppHostHealth.Warning) ? AppHostHealth.Warning
            : health.Contains(AppHostHealth.Unknown) ? AppHostHealth.Unknown : AppHostHealth.Healthy;
    }

    public bool IsRunning => Instances.Any(host => host.IsRunning || host.IsStarting);
}

internal static class AppHostGrouping
{
    public static IReadOnlyList<AppHostMenuGroup> Create(IReadOnlyList<AppHostMenuItem> hosts)
    {

        // Only the same common Git directory AND source path inside that repository
        // identify an application. Clones and different AppHosts with identical names stay separate.
        var groups = new List<List<AppHostMenuItem>>();
        foreach (var host in hosts)
        {
            var group = groups.FirstOrDefault(items => SameApplication(items[0], host));
            if (group is null)
            {
                groups.Add([host]);
            }
            else
            {
                group.Add(host);
            }
        }
        return groups.Select(items => new AppHostMenuGroup(items.Count == 1 ? items[0].Title : items[0].ApplicationName,
            items.Count == 1 ? items.ToArray() : LabelInstances(items))).ToArray();
    }

    public static bool HasSameStructure(IReadOnlyList<AppHostMenuGroup> first, IReadOnlyList<AppHostMenuGroup> second)
        => first.Count == second.Count && first.Zip(second).All(pair => pair.First.Title == pair.Second.Title
            && pair.First.Instances.Select(host => (host.Id, host.Title)).SequenceEqual(pair.Second.Instances.Select(host => (host.Id, host.Title))));

    private static bool SameApplication(AppHostMenuItem first, AppHostMenuItem second)
        => first.Repository is { } a && second.Repository is { } b
            ? TrayAppHostPath.Comparer.Equals(a.CommonDirectory, b.CommonDirectory)
                && TrayAppHostPath.Comparer.Equals(a.RelativeAppHostPath, b.RelativeAppHostPath)
            : TrayAppHostPath.Comparer.Equals(first.Id.AppHostPath, second.Id.AppHostPath);

    private static AppHostMenuItem[] LabelInstances(List<AppHostMenuItem> hosts)
    {
        var labels = hosts.Select(host => host.Repository is { } repository
            ? string.IsNullOrWhiteSpace(repository.Branch) ? Path.GetFileName(repository.WorktreeDirectory) : repository.Branch
            : Path.GetDirectoryName(host.Id.AppHostPath) ?? host.Id.AppHostPath).ToArray();
        var duplicates = labels.GroupBy(label => label, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < labels.Length; i++)
        {
            if (duplicates.Contains(labels[i]))
            {
                labels[i] += " · " + (hosts[i].Repository?.WorktreeDirectory ?? Path.GetDirectoryName(hosts[i].Id.AppHostPath));
            }
        }
        duplicates = labels.GroupBy(label => label, StringComparer.Ordinal).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < labels.Length; i++)
        {
            if (duplicates.Contains(labels[i]))
            {
                labels[i] += GetDiscriminator(hosts[i], i);
            }
        }
        var compactDuplicates = labels.Select(AppHostPresentation.GetPathLabel).GroupBy(label => label, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
        var titles = new HashSet<string>(StringComparer.Ordinal);
        return hosts.Select((host, index) =>
        {
            // Middle truncation can also collapse distinct long branches or directories.
            // Saved entries all have PID 0, so use an ordinal when no process exists.
            var label = labels[index] + (compactDuplicates.Contains(AppHostPresentation.GetPathLabel(labels[index])) ? GetDiscriminator(host, index) : "");
            var title = AppHostPresentation.GetPathLabel(label);
            // A generated suffix can itself match another branch's literal label. Check
            // the final visible title rather than assuming a discriminator cannot collide.
            for (var attempt = 1; !titles.Add(title); attempt++)
            {
                label = $"{labels[index]} · #{index + 1}.{attempt}";
                title = AppHostPresentation.GetPathLabel(label);
            }
            // Preserve every action flag and the lifetime identity; only presentation changes.
            return host with { Title = title, DisplayName = label };
        }).ToArray();
    }

    private static string GetDiscriminator(AppHostMenuItem host, int index)
        => host.Id.AppHostPid > 0 ? $" · PID {host.Id.AppHostPid}" : $" · #{index + 1}";
}
