// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Hosting.Rust;

/// <summary>
/// The subset of <c>cargo metadata --format-version 1 --no-deps</c> output that Aspire needs to work out
/// which file a cargo build produces.
/// </summary>
internal sealed class CargoMetadata
{
    /// <summary>
    /// Every package in the workspace (or the single package for a non-workspace crate).
    /// </summary>
    public required IReadOnlyList<CargoPackage> Packages { get; init; }

    /// <summary>
    /// The package ids cargo would build when no <c>-p</c>/<c>--package</c> is given.
    /// </summary>
    public required IReadOnlyList<string> DefaultMemberIds { get; init; }

    /// <summary>
    /// The absolute path of the directory cargo writes build output to.
    /// </summary>
    /// <remarks>
    /// This is not always <c>&lt;crate&gt;/target</c>: <c>CARGO_TARGET_DIR</c>, <c>build.target-dir</c> in
    /// <c>.cargo/config.toml</c>, and <c>--target-dir</c> all move it, and a workspace member shares the
    /// workspace root's directory. Cargo resolves all of that and reports the answer, so debugging uses this
    /// rather than assuming the default layout.
    /// </remarks>
    public required string TargetDirectory { get; init; }

    /// <summary>
    /// Parses the JSON emitted by <c>cargo metadata --format-version 1 --no-deps</c>.
    /// </summary>
    /// <remarks>
    /// The document looks like this (trimmed to the fields used here):
    /// <code>
    /// {
    ///   "packages": [
    ///     {
    ///       "name": "my-app",
    ///       "id": "path+file:///app#my-app@0.1.0",
    ///       "default_run": "server",
    ///       "rust_version": "1.89",
    ///       "targets": [
    ///         { "kind": ["bin"], "crate_types": ["bin"], "name": "server", "src_path": "/app/src/bin/server.rs" },
    ///         { "kind": ["lib"], "crate_types": ["lib"], "name": "my_app",  "src_path": "/app/src/lib.rs" }
    ///       ]
    ///     }
    ///   ],
    ///   "workspace_members": ["path+file:///app#my-app@0.1.0"],
    ///   "workspace_default_members": ["path+file:///app#my-app@0.1.0"],
    ///   "resolve": null,
    ///   "target_directory": "/app/target"
    /// }
    /// </code>
    /// Notes on the shape that the parser has to tolerate:
    /// <list type="bullet">
    /// <item><c>default_run</c> is <see langword="null"/> (or absent on old cargo) unless the manifest sets it.</item>
    /// <item><c>rust_version</c> is likewise absent unless the manifest declares an MSRV, and may be written
    /// with one, two, or three components.</item>
    /// <item><c>workspace_default_members</c> only exists from cargo 1.71. Older output is rejected because
    /// falling back to every workspace member would ignore an explicit <c>[workspace] default-members</c>.</item>
    /// <item>Package id syntax changed in cargo 1.77 from <c>"my-app 0.1.0 (path+file:///app)"</c> to
    /// <c>"path+file:///app#my-app@0.1.0"</c>. Ids are only ever compared against other ids from the same
    /// document, so both forms work without being parsed.</item>
    /// <item>A target's <c>kind</c> is an array because a single target can be several crate types at once
    /// (for example <c>["lib", "cdylib"]</c>), so a bin target is one whose kind array contains "bin".</item>
    /// </list>
    /// </remarks>
    public static CargoMetadata Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var packages = new List<CargoPackage>();
        if (root.TryGetProperty("packages", out var packagesElement) && packagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var packageElement in packagesElement.EnumerateArray())
            {
                packages.Add(ParsePackage(packageElement));
            }
        }

        if (!root.TryGetProperty("workspace_default_members", out var defaultMembersElement)
            || defaultMembersElement.ValueKind != JsonValueKind.Array)
        {
            throw new DistributedApplicationException(
                "Aspire.Hosting.Rust requires Cargo 1.71 or later because this 'cargo metadata' output does not " +
                "include 'workspace_default_members'. Update the Rust toolchain and try again.");
        }

        return new CargoMetadata
        {
            Packages = packages,
            DefaultMemberIds = ReadStringArray(defaultMembersElement),
            TargetDirectory = root.TryGetProperty("target_directory", out var targetDirectoryElement)
                ? targetDirectoryElement.GetString() ?? string.Empty
                : string.Empty
        };
    }

    private static CargoPackage ParsePackage(JsonElement element)
    {
        var binTargets = new List<string>();
        if (element.TryGetProperty("targets", out var targetsElement) && targetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var targetElement in targetsElement.EnumerateArray())
            {
                if (IsBinTarget(targetElement) && targetElement.TryGetProperty("name", out var targetName)
                    && targetName.GetString() is { Length: > 0 } name)
                {
                    binTargets.Add(name);
                }
            }
        }

        return new CargoPackage
        {
            Name = element.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
            Id = element.TryGetProperty("id", out var idElement) ? idElement.GetString() ?? string.Empty : string.Empty,
            DefaultRun = element.TryGetProperty("default_run", out var defaultRunElement) && defaultRunElement.ValueKind == JsonValueKind.String
                ? defaultRunElement.GetString()
                : null,
            BinTargetNames = binTargets
        };
    }

    private static bool IsBinTarget(JsonElement targetElement)
    {
        if (!targetElement.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var kind in kindElement.EnumerateArray())
        {
            if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "bin")
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ReadStringArray(JsonElement element)
    {
        var values = new List<string>();

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                {
                    values.Add(value);
                }
            }
        }

        return values;
    }
}

/// <summary>
/// A single package from <c>cargo metadata</c> output.
/// </summary>
internal sealed class CargoPackage
{
    /// <summary>
    /// The <c>[package] name</c> from the manifest.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The opaque cargo package id, used to match against the workspace's default members.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// The <c>[package] default-run</c> value, if the manifest declares one.
    /// </summary>
    /// <remarks>
    /// <c>cargo run</c> honours <c>default-run</c> while <c>cargo build</c> ignores it, so publishing has to
    /// resolve it here to produce the same binary the resource runs locally.
    /// See https://doc.rust-lang.org/cargo/reference/manifest.html#the-default-run-field
    /// </remarks>
    public string? DefaultRun { get; init; }

    /// <summary>
    /// The names of the package's <c>bin</c> targets, in the order cargo reported them.
    /// </summary>
    /// <remarks>
    /// Binary target names are used verbatim as file names, so hyphens are NOT translated to underscores the
    /// way they are for library targets.
    /// See https://doc.rust-lang.org/cargo/reference/cargo-targets.html#binaries
    /// </remarks>
    public required IReadOnlyList<string> BinTargetNames { get; init; }
}
