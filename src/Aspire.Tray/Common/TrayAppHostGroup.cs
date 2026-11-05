// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tray;

/// <summary>An application across checkouts, independent of branch names and process lifetimes.</summary>
internal readonly record struct TrayApplicationKey(string GitDirectory, string AppHostPath);

/// <summary>Optional Git presentation data, captured with discovery rather than during painting.</summary>
internal sealed record TrayGitCheckout(TrayApplicationKey Application, string Directory, string Label)
{
    public static TrayGitCheckout? Read(string appHostPath)
    {
        try
        {
            appHostPath = Path.GetFullPath(appHostPath);
            for (var root = Path.GetDirectoryName(appHostPath); root is not null; root = Path.GetDirectoryName(root))
            {
                var git = Path.Combine(root, ".git");
                if (File.Exists(git))
                {
                    // Linked worktrees use "gitdir: /repo/.git/worktrees/cart"; relative paths
                    // are relative to the pointer file, not the tray's working directory.
                    var pointer = ReadMetadata(git);
                    if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal))
                    {
                        return null;
                    }
                    git = Path.GetFullPath(pointer[7..].Trim(), root);
                }
                else if (!System.IO.Directory.Exists(git))
                {
                    continue;
                }

                // commondir usually contains "../..". HEAD belongs to the individual checkout.
                // https://git-scm.com/docs/gitrepository-layout
                var commonFile = Path.Combine(git, "commondir");
                var common = File.Exists(commonFile) ? Path.GetFullPath(ReadMetadata(commonFile), git) : git;
                if (!System.IO.Directory.Exists(common))
                {
                    return null;
                }
                var headFile = Path.Combine(git, "HEAD");
                var head = File.Exists(headFile) ? ReadMetadata(headFile) : "";
                const string BranchPrefix = "ref: refs/heads/";
                var label = head.StartsWith(BranchPrefix, StringComparison.Ordinal) ? head[BranchPrefix.Length..] : Path.GetFileName(root);
                if (string.IsNullOrWhiteSpace(label))
                {
                    label = Path.GetFileName(root);
                }
                var relative = Path.GetRelativePath(root, appHostPath);
                common = Path.TrimEndingDirectorySeparator(Path.GetFullPath(common));
                // Match the platform's path comparison without making branch labels case-insensitive.
                var key = OperatingSystem.IsWindows()
                    ? new TrayApplicationKey(common.ToUpperInvariant(), relative.ToUpperInvariant())
                    : new TrayApplicationKey(common, relative);
                return new(key, root, label.ReplaceLineEndings(" ").Replace('\t', ' '));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Missing or unreadable Git metadata must never hide an AppHost or guess a group.
        }
        return null;
    }

    private static string ReadMetadata(string path)
    {
        using var reader = File.OpenText(path);
        var buffer = new char[4097];
        var length = reader.ReadBlock(buffer, 0, buffer.Length);
        return length <= 4096 ? new string(buffer, 0, length).Trim() : throw new IOException("Git metadata exceeds 4096 characters.");
    }
}

/// <summary>A section's application row and its individually actionable instances.</summary>
internal sealed record TrayAppHostGroup(TrayApplicationKey? Key, string Name, IReadOnlyList<AppHostMenuItem> Hosts)
{
    public bool IsGroup => Key is not null && Hosts.Count > 1;

    public static IReadOnlyList<TrayAppHostGroup> Create(IReadOnlyList<AppHostMenuItem> hosts)
    {
        var groups = new List<List<AppHostMenuItem>>();
        var byApplication = new Dictionary<TrayApplicationKey, List<AppHostMenuItem>>();
        foreach (var host in hosts)
        {
            if (host.Checkout is not { } checkout)
            {
                groups.Add([host]);
                continue;
            }
            if (!byApplication.TryGetValue(checkout.Application, out var group))
            {
                group = [];
                byApplication.Add(checkout.Application, group);
                groups.Add(group);
            }
            group.Add(host);
        }
        return groups.Select(group => new TrayAppHostGroup(group[0].Checkout?.Application, group[0].DisplayName,
            group.Count > 1 ? LabelInstances(group) : group)).ToArray();
    }

    private static AppHostMenuItem[] LabelInstances(List<AppHostMenuItem> hosts)
    {
        var labels = hosts.Select(host => host.Checkout!.Label).ToArray();
        var used = new HashSet<string>(StringComparer.Ordinal);
        return hosts.Select((host, index) =>
        {
            var label = labels[index];
            // Put disambiguators first so native text ellipsis cannot hide them. Long branch
            // names can otherwise differ only beyond the narrow row's visible text.
            if (label.Length > 24 || labels.Count(other => other == label) > 1)
            {
                label = $"{index + 1}. {label}";
            }
            // A generated label can itself be another branch's literal name.
            for (var attempt = 1; !used.Add(label); attempt++)
            {
                label = $"{index + 1}.{attempt}. {labels[index]}";
            }
            return host with { Title = label, DisplayName = label };
        }).ToArray();
    }

    public PanelStatusGlyph Status(bool live)
    {
        var statuses = Hosts.Select(host => TrayPanel.GetStatusGlyph(host, live)).ToArray();
        // An unknown/stopped child cannot make a partially known application appear healthy.
        foreach (var status in new[] { PanelStatusGlyph.Unhealthy, PanelStatusGlyph.Warning, PanelStatusGlyph.Busy,
            PanelStatusGlyph.Unknown, PanelStatusGlyph.Stopped })
        {
            if (statuses.Contains(status))
            {
                return status;
            }
        }
        return PanelStatusGlyph.Healthy;
    }
}
