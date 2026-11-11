#!/usr/bin/env dotnet

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Usage: dotnet run --file BumpVersion.cs -- <version> --branch <branch> --milestone <milestone>
//            [--api-diff-release-branch <release/X.Y>]

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

const string Usage = "<X.Y or X.Y.Z> --branch <target branch> --milestone <open milestone> [--api-diff-release-branch <release/X.Y>]";

// The scheduled API/ATS diff workflows and their doc name the newest release branch.
// The order must match the patterns in RetargetApiDiffs.
string[] apiDiffPaths =
[
    ".github/workflows/generate-api-diffs.yml",
    ".github/workflows/generate-ats-diffs.yml",
    "docs/ci/typescript-api-compat.md",
];

try
{
    if (args is ["--help"] or ["-h"])
    {
        Console.WriteLine($"Usage: BumpVersion.cs {Usage}");
        return 0;
    }

    if (args.Length is not (5 or 7))
    {
        throw new ArgumentException($"Expected {Usage}.");
    }

    var version = args[0];
    string? branch = null;
    string? milestone = null;
    string? apiDiffReleaseBranch = null;
    for (var i = 1; i < args.Length; i += 2)
    {
        switch (args[i])
        {
            case "--branch" when branch is null:
                branch = args[i + 1];
                break;
            case "--milestone" when milestone is null:
                milestone = args[i + 1];
                break;
            case "--api-diff-release-branch" when apiDiffReleaseBranch is null:
                apiDiffReleaseBranch = args[i + 1];
                break;
            default:
                throw new ArgumentException($"Unknown or duplicate option: {args[i]}");
        }
    }

    if (branch is null || milestone is null)
    {
        throw new ArgumentException("Both --branch and --milestone are required.");
    }

    var root = GetRepositoryRoot();
    List<string> paths = ["eng/Versions.props", ".github/policies/milestoneAssignment.prClosed.yml"];
    if (apiDiffReleaseBranch is not null)
    {
        paths.AddRange(apiDiffPaths);
    }
    var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    var originals = paths.Select(path => encoding.GetString(File.ReadAllBytes(Path.Combine(root, path)))).ToArray();
    var (versions, policy) = PrepareUpdates(originals[0], originals[1], version, branch, milestone);
    List<string> updates = [versions, policy];
    if (apiDiffReleaseBranch is not null)
    {
        updates.AddRange(RetargetApiDiffs(apiDiffPaths, originals[2..], apiDiffReleaseBranch));
    }

    // Validate every input before writing any file.
    for (var i = 0; i < paths.Count; i++)
    {
        if (originals[i] != updates[i])
        {
            File.WriteAllBytes(Path.Combine(root, paths[i]), encoding.GetBytes(updates[i]));
            Console.WriteLine($"Updated {paths[i]}");
        }
        else
        {
            Console.WriteLine($"Unchanged {paths[i]}");
        }
    }

    return 0;
}
catch (Exception error) when (error is ArgumentException or InvalidOperationException or XmlException
    or IOException or UnauthorizedAccessException or RegexMatchTimeoutException)
{
    Console.Error.WriteLine($"error: {error.Message}");
    return 1;
}

static string GetRepositoryRoot([CallerFilePath] string sourcePath = "")
{
    // File-based apps execute from a build cache; resolve relative to the source,
    // not the binary or the caller's working directory.
    return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../.."));
}

static Regex Pattern(string pattern, RegexOptions options = RegexOptions.None)
{
    return new Regex(pattern, options | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

static (string Versions, string Policy) PrepareUpdates(
    string versions, string policy, string version, string branch, string milestone)
{
    if (!Pattern(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?\z").IsMatch(version))
    {
        throw new ArgumentException("Version must be X.Y or X.Y.Z with non-negative integer components.");
    }
    if (!Pattern(@"\A[A-Za-z0-9._/-]+\z").IsMatch(branch))
    {
        throw new ArgumentException("Branch must be a literal branch name.");
    }
    if (!Pattern(@"\A[0-9]+\.[0-9]+(?:\.(?:[0-9]+|x))?\z").IsMatch(milestone))
    {
        throw new ArgumentException("Milestone must be a release title such as 17.0, 17.0.1, or 17.0.x.");
    }

    var components = version.Split('.');
    if (components.Length == 2)
    {
        components = [.. components, "0"];
    }
    var document = XDocument.Parse(versions.TrimStart('\uFEFF'));
    var group = document.Root?.Element("PropertyGroup");
    if (group?.Element("VersionPrefix")?.Value != "$(MajorVersion).$(MinorVersion).$(PatchVersion)")
    {
        throw new InvalidOperationException("Expected the composed VersionPrefix in the first property group.");
    }

    string[] names = ["MajorVersion", "MinorVersion", "PatchVersion"];
    for (var i = 0; i < names.Length; i++)
    {
        var name = names[i];
        if (document.Descendants(name).Count() != 1 || group.Element(name) is null)
        {
            throw new InvalidOperationException($"Expected exactly one {name} in the first property group.");
        }
        var pattern = Pattern($@"(<{name}>)[0-9]+(</{name}>)");
        if (pattern.Matches(versions).Count != 1)
        {
            throw new InvalidOperationException($"Unexpected {name} format.");
        }
        versions = pattern.Replace(versions, match => match.Groups[1].Value + components[i] + match.Groups[2].Value);
    }

    // The policy uses task blocks shaped as:
    //     - if:
    //       - targetsBranch:
    //           branch: main
    //       then:
    //       - addMilestone:
    //           milestone: 17.0
    // Match only this layout, preserving comments and unrelated rules rather than
    // reserializing YAML (which can reinterpret numeric milestone titles).
    var tasks = Pattern(@"^    - if:\r?\n.*?(?=^    - if:|\z)", RegexOptions.Multiline | RegexOptions.Singleline);
    var target = Pattern($@"^      - targetsBranch:\r?\n          branch: {Regex.Escape(branch)}\r?$", RegexOptions.Multiline);
    var matches = tasks.Matches(policy).Where(task => target.IsMatch(task.Value)).ToArray();
    if (matches.Length != 1)
    {
        throw new InvalidOperationException($"Expected exactly one milestone policy task for branch '{branch}'.");
    }
    var task = matches[0];
    if (Pattern("^      - targetsBranch:", RegexOptions.Multiline).Matches(task.Value).Count != 1)
    {
        throw new InvalidOperationException("Target policy task must have exactly one branch condition.");
    }
    var milestonePattern = Pattern(
        @"(^      - addMilestone:\r?\n          milestone: )[0-9]+\.[0-9]+(?:\.(?:[0-9]+|x))?(\r?$)",
        RegexOptions.Multiline);
    if (milestonePattern.Matches(task.Value).Count != 1)
    {
        throw new InvalidOperationException("Expected exactly one numeric release milestone in the target task.");
    }
    var updatedTask = milestonePattern.Replace(task.Value, match => match.Groups[1].Value + milestone + match.Groups[2].Value);
    policy = policy[..task.Index] + updatedTask + policy[(task.Index + task.Length)..];

    return (versions, policy);
}

static string[] RetargetApiDiffs(string[] paths, string[] files, string releaseBranch)
{
    if (!Pattern(@"\Arelease/[0-9]+\.[0-9]+\z").IsMatch(releaseBranch))
    {
        throw new ArgumentException("API diff release branch must be release/X.Y.");
    }

    // Each workflow's scheduled matrix is a JSON literal inside a GitHub expression:
    //     || '["main","release/17.0"]') }}
    // Keep main and swap only the single release branch.
    var matrix = Pattern(@"(?<=\x27\[""main"","")release/[0-9]+\.[0-9]+(?=""\]\x27)");

    // The doc names the scheduled branch as: matrix (currently `release/17.0`).
    var docNote = Pattern(@"(?<=\(currently `)release/[0-9]+\.[0-9]+(?=`\))");

    Regex[] patterns = [matrix, matrix, docNote];
    var results = new string[files.Length];
    for (var i = 0; i < files.Length; i++)
    {
        if (patterns[i].Matches(files[i]).Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly one scheduled release branch in {paths[i]}.");
        }
        results[i] = patterns[i].Replace(files[i], releaseBranch);
    }

    return results;
}
