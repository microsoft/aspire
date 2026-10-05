// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Rust;

/// <summary>
/// The executable a cargo build produces, and where cargo writes it.
/// </summary>
/// <param name="Name">The file name cargo writes, without any platform executable extension.</param>
/// <param name="ProfileDirectory">The profile directory under <c>target/</c>.</param>
/// <param name="Target">The <c>--target</c> triple, or <see langword="null"/> when building for the host.</param>
/// <param name="IsExample">Whether the target is an example rather than a binary.</param>
internal sealed record RustCargoTarget(string Name, string ProfileDirectory, string? Target, bool IsExample)
{
    /// <summary>
    /// The path segments below the cargo target directory, in order.
    /// </summary>
    /// <remarks>
    /// Cargo only inserts a triple directory when <c>--target</c> is passed, and examples get their own
    /// <c>examples/</c> directory, so a host build of a binary lands in <c>&lt;target-dir&gt;/&lt;profile&gt;/</c>
    /// while a cross build of an example lands in <c>&lt;target-dir&gt;/&lt;triple&gt;/&lt;profile&gt;/examples/</c>.
    /// See https://doc.rust-lang.org/cargo/guide/build-cache.html
    /// </remarks>
    private IEnumerable<string> Segments
    {
        get
        {
            if (Target is not null)
            {
                yield return Target;
            }

            yield return ProfileDirectory;

            if (IsExample)
            {
                yield return "examples";
            }

            yield return Name;
        }
    }

    /// <summary>
    /// The path of the produced executable relative to the crate's <c>target</c> directory, with any
    /// target-triple directory left out, using forward slashes so it can be written straight into a
    /// Dockerfile.
    /// </summary>
    /// <remarks>
    /// Cargo inserts a triple directory whenever a target is selected, and <c>--target</c> is not the only
    /// way to select one: <c>[build] target</c> in a <c>.cargo/config.toml</c> and the
    /// <c>CARGO_BUILD_TARGET</c> environment variable both do it without passing through
    /// <see cref="Target"/>. The container build therefore searches for this path under both layouts rather
    /// than predicting which one applies.
    /// </remarks>
    public string RelativePathWithoutTarget => string.Join('/', Target is null ? Segments : Segments.Skip(1));

    /// <summary>
    /// The absolute path of the produced executable, given the target directory cargo reported.
    /// </summary>
    /// <remarks>
    /// The executable extension is applied for the host platform because the app host, the debugger, and the
    /// build all run on the same machine. It is deliberately not applied to
    /// <see cref="RelativePathWithoutTarget"/>, which describes output produced inside a Linux container.
    /// </remarks>
    public string GetExecutablePath(string targetDirectory)
    {
        var path = Path.Combine([targetDirectory, .. Segments]);

        return OperatingSystem.IsWindows() ? $"{path}.exe" : path;
    }
}

/// <summary>
/// Resolves which executable a cargo build produces, using only manifest information from
/// <c>cargo metadata</c> and the resource's configured cargo options. Nothing here compiles.
/// </summary>
/// <remarks>
/// <para>
/// This is the single answer to "which file does this resource's cargo command produce", shared by
/// publishing (which needs it to emit <c>COPY</c>/<c>ENTRYPOINT</c> without building on the host) and
/// debugging (which needs it to point the native debugger at a program). Resolving it once here keeps the
/// container image and the debugged process the same binary.
/// </para>
/// <para>
/// A plain <c>cargo run</c> never gets here: the arguments go straight to cargo and cargo picks the binary
/// itself. Run mode only reaches this when a debug launch needs somewhere to attach.
/// </para>
/// <para>
/// Anything cargo would itself have rejected at <c>cargo run</c> time is passed straight through rather
/// than re-validated. The only reported failures are the cases where run mode succeeds but the produced
/// file name is still unknowable.
/// </para>
/// </remarks>
internal static class RustCargoTargetResolver
{
    public static RustCargoTarget Resolve(
        CargoMetadata metadata,
        RustCargoOptionsAnnotation options,
        DistributedApplicationExecutionContext executionContext,
        string resourceName)
    {
        var profileDirectory = ResolveProfileDirectory(options, executionContext);

        if (options.Example is { } example)
        {
            return new RustCargoTarget(example, profileDirectory, options.Target, IsExample: true);
        }

        var name = options.BinTarget ?? ResolveBinaryName(metadata, options.Package, resourceName);

        return new RustCargoTarget(name, profileDirectory, options.Target, IsExample: false);
    }

    /// <remarks>
    /// The directory is not always the profile name: the built-in <c>dev</c> and <c>test</c> profiles both
    /// emit to <c>target/debug</c> and <c>bench</c> emits to <c>target/release</c>. Custom profiles use their
    /// own name. See https://doc.rust-lang.org/cargo/reference/profiles.html
    /// </remarks>
    private static string ResolveProfileDirectory(RustCargoOptionsAnnotation options, DistributedApplicationExecutionContext executionContext)
    {
        // A debug build takes cargo's own default profile (dev) unless the resource asked for an optimized
        // build, so it reuses whatever `cargo run` already compiled. Publish always optimizes unless the
        // resource explicitly opted out.
        var profile = options.Profile
            ?? ((options.ReleaseBuild ?? executionContext.IsPublishMode) ? "release" : "dev");

        return profile switch
        {
            "dev" or "test" => "debug",
            "bench" => "release",
            _ => profile
        };
    }

    private static string ResolveBinaryName(CargoMetadata metadata, string? requestedPackage, string resourceName)
    {
        var package = ResolvePackage(metadata, requestedPackage, resourceName);

        if (package.DefaultRun is { Length: > 0 } defaultRun)
        {
            return defaultRun;
        }

        return package.BinTargetNames switch
        {
            [var single] => single,
            // A package with no binary at all is a different mistake from an ambiguous one, and pointing the
            // user at WithCargoBinTarget would send them looking for a target that does not exist.
            [] => throw new DistributedApplicationException(
                $"Unable to work out which binary the Rust app '{resourceName}' produces: the package '{package.Name}' declares no " +
                $"binary targets. Point the app directory at a package with a binary, or select one with WithCargoPackage(\"<name>\")."),
            var many => throw new DistributedApplicationException(
                $"Unable to work out which binary the Rust app '{resourceName}' produces: the package '{package.Name}' declares " +
                $"{many.Count} binary targets. Call WithCargoBinTarget(\"<name>\") to select one.")
        };
    }

    private static CargoPackage ResolvePackage(CargoMetadata metadata, string? requestedPackage, string resourceName)
    {
        if (requestedPackage is not null)
        {
            // Reported here rather than left to cargo because this runs before any build: the debugger needs
            // the executable path up front, so a typo would otherwise surface as an unexplained
            // "Sequence contains no matching element" from LINQ.
            return metadata.Packages.FirstOrDefault(p => p.Name == requestedPackage)
                ?? throw new DistributedApplicationException(
                    $"The Rust app '{resourceName}' requested the cargo package '{requestedPackage}' with WithCargoPackage, but " +
                    $"'cargo metadata' reported no such package. Available packages: {FormatPackageNames(metadata)}.");
        }

        var defaultPackages = metadata.Packages.Where(p => metadata.DefaultMemberIds.Contains(p.Id)).ToList();

        if (defaultPackages is [var onlyMember])
        {
            return onlyMember;
        }

        // `cargo run` only needs one *runnable* member, so the common workspace shape of an app crate beside
        // library crates runs fine. Library-only members are dropped before the choice is called ambiguous.
        var runnablePackages = defaultPackages.Where(static p => p.BinTargetNames.Count > 0).ToList();

        return runnablePackages switch
        {
            [var single] => single,
            _ => throw new DistributedApplicationException(
                $"Unable to work out which binary the Rust app '{resourceName}' produces: 'cargo metadata' reported " +
                $"{runnablePackages.Count} default workspace members with a binary target. Call WithCargoPackage(\"<name>\") to select one. " +
                $"Available packages: {FormatPackageNames(metadata)}.")
        };
    }

    private static string FormatPackageNames(CargoMetadata metadata)
        => metadata.Packages is { Count: > 0 } packages
            ? string.Join(", ", packages.Select(static p => $"'{p.Name}'"))
            : "none";
}
