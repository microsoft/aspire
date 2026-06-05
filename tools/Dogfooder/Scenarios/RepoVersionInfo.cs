// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;

namespace Aspire.Dogfooder.Scenarios;

/// <summary>
/// Reads the current released version from the repo's <c>eng/Versions.props</c>
/// so scenarios that synthesise vCurrent / vNext-minor / vNext-hotfix labels
/// can do so without hard-coding numbers that go stale every release cycle.
/// </summary>
/// <remarks>
/// The repo's authoritative version is composed from <c>&lt;MajorVersion&gt;</c>,
/// <c>&lt;MinorVersion&gt;</c>, and <c>&lt;PatchVersion&gt;</c> properties in
/// <c>eng/Versions.props</c>. Example:
/// <code>
///   &lt;MajorVersion&gt;13&lt;/MajorVersion&gt;
///   &lt;MinorVersion&gt;5&lt;/MinorVersion&gt;
///   &lt;PatchVersion&gt;0&lt;/PatchVersion&gt;
/// </code>
/// Failure to read or parse the file falls back to a placeholder
/// <c>0.0.0</c> rather than throwing — scenarios still work, just with the
/// version surface clearly marked as broken so the user notices.
/// </remarks>
internal sealed class RepoVersionInfo
{
    public RepoVersionInfo(int major, int minor, int patch, string sourcePath)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        SourcePath = sourcePath;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string SourcePath { get; }

    public string CurrentVersionString => $"{Major}.{Minor}.{Patch}";
    public string NextMinorVersionString => $"{Major}.{Minor + 1}.0";
    public string NextPatchVersionString => $"{Major}.{Minor}.{Patch + 1}";

    public static RepoVersionInfo Load()
    {
        var path = FindRepoRoot() is { } root
            ? Path.Combine(root, "eng", "Versions.props")
            : "(eng/Versions.props not found)";

        if (!File.Exists(path))
        {
            return new RepoVersionInfo(0, 0, 0, path);
        }

        try
        {
            var doc = XDocument.Load(path);
            // Versions.props uses the default (no) namespace; descendants
            // ignore any future namespace prefix the build infra introduces.
            int? major = ReadInt(doc, "MajorVersion");
            int? minor = ReadInt(doc, "MinorVersion");
            int? patch = ReadInt(doc, "PatchVersion");
            return new RepoVersionInfo(major ?? 0, minor ?? 0, patch ?? 0, path);
        }
        catch
        {
            return new RepoVersionInfo(0, 0, 0, path);
        }
    }

    private static int? ReadInt(XDocument doc, string localName)
    {
        var element = doc.Descendants().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.Ordinal));
        return element is { Value: { Length: > 0 } v } && int.TryParse(v.Trim(), out var n)
            ? n
            : null;
    }

    private static string? FindRepoRoot()
    {
        // Walk up from AppContext.BaseDirectory looking for global.json — the
        // canonical repo-root marker we use elsewhere (PackageBuildRunner,
        // DogfoodSessionPreparer.FindRepoRootOrCwd).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
