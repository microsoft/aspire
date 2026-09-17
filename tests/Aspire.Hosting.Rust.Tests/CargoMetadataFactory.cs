// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Rust.Tests;

/// <summary>
/// Builds <c>cargo metadata --format-version 1 --no-deps</c> documents for tests, so publish behaviour can be
/// exercised without a Rust toolchain on the machine.
/// </summary>
internal static class CargoMetadataFactory
{
    /// <summary>
    /// A single-package crate whose only bin target is named after the package.
    /// </summary>
    public static string SinglePackage(string packageName, string? defaultRun = null, params string[] extraBins)
        => Workspace(new CargoPackageSpec(packageName, [packageName, .. extraBins], defaultRun));

    /// <summary>
    /// A workspace document in which every package is a default member.
    /// </summary>
    public static string Workspace(params CargoPackageSpec[] packages)
        => Workspace(packages, defaultMembers: null);

    /// <summary>
    /// A workspace document whose default members are narrowed to <paramref name="defaultMembers"/>,
    /// mirroring the <c>[workspace] default-members</c> key.
    /// </summary>
    public static string Workspace(IReadOnlyList<CargoPackageSpec> packages, IReadOnlyList<string>? defaultMembers)
    {
        var packageJson = packages.Select(p =>
        {
            var targets = p.BinTargetNames
                .Select(bin => $$"""
                            { "kind": ["bin"], "crate_types": ["bin"], "name": "{{bin}}", "src_path": "/app/src/bin/{{bin}}.rs" }
                    """)
                .Append("""
                            { "kind": ["lib"], "crate_types": ["lib"], "name": "shared", "src_path": "/app/src/lib.rs" }
                    """);

            var defaultRun = p.DefaultRun is null ? "null" : $"\"{p.DefaultRun}\"";

            return $$"""
                    {
                      "name": "{{p.Name}}",
                      "id": "{{PackageId(p.Name)}}",
                      "manifest_path": "/app/{{p.Name}}/Cargo.toml",
                      "default_run": {{defaultRun}},
                      "rust_version": "1.85",
                      "targets": [
                {{string.Join(",\n", targets)}}
                      ]
                    }
                """;
        });

        var members = (defaultMembers ?? [.. packages.Select(p => p.Name)]).Select(name => $"\"{PackageId(name)}\"");

        return $$"""
            {
              "packages": [
            {{string.Join(",\n", packageJson)}}
              ],
              "workspace_members": [{{string.Join(", ", packages.Select(p => $"\"{PackageId(p.Name)}\""))}}],
              "workspace_default_members": [{{string.Join(", ", members)}}],
              "resolve": null,
              "target_directory": "/app/target",
              "workspace_root": "/app",
              "version": 1
            }
            """;
    }

    // Cargo 1.77+ package id syntax: "<source>#<name>@<version>".
    private static string PackageId(string packageName) => $"path+file:///app/{packageName}#{packageName}@0.1.0";
}

internal sealed record CargoPackageSpec(string Name, IReadOnlyList<string> BinTargetNames, string? DefaultRun = null);
